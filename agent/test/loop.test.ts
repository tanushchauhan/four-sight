import assert from "node:assert/strict";
import { test } from "node:test";
import { EventQueue } from "../src/agent/events.js";
import { runEpisode } from "../src/agent/loop.js";
import { MockPlanner, ScriptedModel, mockCall, mockFinish } from "../src/llm/mock-planner.js";
import { setup, simulatedRescuer } from "./helpers.js";

test("loop runs tool calls, feeds results back, and stops when the model stops", async () => {
  const model = new ScriptedModel([
    { content: "check", toolCalls: [mockCall("get_robot_state"), mockCall("get_coverage")] },
    { content: null, toolCalls: [mockCall("turn", { degrees: 90 })] },
    mockFinish("turned left"),
  ]);
  const { logger, ctx, state, solo } = setup(model);
  const r = await runEpisode({ kind: "search_step" }, solo(model, 10), ctx, logger);
  assert.equal(r.summary, "turned left");
  assert.equal(r.steps, 3);
  assert.deepEqual(r.toolCalls.map((t) => t.name), ["get_robot_state", "get_coverage", "turn"]);
  // Third model call saw all earlier tool results.
  assert.equal(model.seen[2]!.filter((m) => m.role === "tool").length, 3);
  // Every call reached the agent_log table.
  assert.equal(state.agentLog.length, 3);
  assert.equal((await ctx.robot.getState()).heading, 90);
});

test("loop enforces the step limit", async () => {
  const model = new ScriptedModel(Array.from({ length: 20 }, () => ({ content: null, toolCalls: [mockCall("get_robot_state")] })));
  const { logger, ctx, solo } = setup(model);
  const r = await runEpisode({ kind: "search_step" }, solo(model, 4), ctx, logger);
  assert.equal(r.hitStepLimit, true);
  assert.equal(r.toolCalls.length, 4);
});

test("tool errors are returned to the model instead of crashing the episode", async () => {
  const model = new ScriptedModel([{ content: null, toolCalls: [mockCall("set_triage", { case_id: 99, color: "red", description: "x" })] }, mockFinish("ok")]);
  const { logger, ctx, solo } = setup(model);
  const r = await runEpisode({ kind: "search_step" }, solo(model, 5), ctx, logger);
  assert.equal(r.toolCalls[0]!.ok, false);
  assert.match(JSON.stringify(r.toolCalls[0]!.result), /no case 99/);
  assert.equal(r.summary, "ok");
});

test("event queue orders by urgency and drops duplicates", () => {
  const q = new EventQueue();
  q.push({ kind: "search_step" });
  q.push({ kind: "sitrep_due" });
  q.push({ kind: "detection", caseId: 1 });
  q.push({ kind: "detection", caseId: 1 });
  assert.equal(q.size, 3);
  assert.equal(q.tryShift()!.kind, "detection");
  assert.equal(q.tryShift()!.kind, "sitrep_due");
});

for (const mode of ["team", "single"] as const) {
  test(`offline mission (${mode}): finds both survivors, second-looks, talks, triages and alerts`, async () => {
    const { runner, state, notifier, robot } = setup(new MockPlanner(), { mode });
    runner.startMission();
    const episodes = await runner.runUntilIdle(40, simulatedRescuer(state, robot));
    assert.ok(episodes.length > 3);

    const cases = await state.getCases();
    assert.equal(cases.length, 2);
    const hidden = cases.find((c) => Math.hypot(c.x - 4.2, c.y - 4.1) < 0.5)!;
    const shelf = cases.find((c) => Math.hypot(c.x - 7.2, c.y - 1.0) < 0.5)!;
    assert.equal(hidden.triage, "yellow");
    assert.equal(shelf.triage, "red");
    assert.equal(shelf.status, "responders_sent");

    // Which case needs a second look depends on the route; at least one must have.
    // (In team mode the coordinator may triage inside a search episode, so look at every episode.)
    const looked = episodes.filter((e) => e.toolCalls.some((t) => t.name === "analyze_image"));
    assert.ok(looked.some((e) => e.toolCalls.filter((t) => t.name === "analyze_image").length >= 2), "no second look happened");
    const listens = episodes.flatMap((e) => e.toolCalls.filter((t) => t.name === "listen"));
    assert.ok(listens.length >= 2, "agent did not talk to both survivors");

    assert.ok(notifier.sent.length >= 1);
    const coverage = await state.getCoverage();
    assert.ok(coverage.length / 48 > 0.8, `coverage ${coverage.length}/48`);

    if (mode === "team") {
      // Work was split: camera/voice by triage, movement+sweeps by search, people-facing by coordinator.
      const by = (agent: string) => new Set(episodes.flatMap((e) => e.toolCalls.filter((t) => t.agent === agent).map((t) => t.name)));
      assert.ok(by("triage").has("analyze_image") && by("triage").has("listen") && by("triage").has("set_triage"));
      assert.ok(by("search").has("sweep_360"));
      assert.ok(by("coordinator").has("delegate_triage") && by("coordinator").has("notify_responders"));
      assert.ok(!by("coordinator").has("move") && !by("coordinator").has("analyze_image"));
      assert.ok(!by("triage").has("notify_responders"));
    }
  });
}
