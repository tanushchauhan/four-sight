import assert from "node:assert/strict";
import { test } from "node:test";
import { runEpisode } from "../src/agent/loop.js";
import { TOOLSETS } from "../src/agent/team.js";
import { ScriptedModel, mockCall, mockFinish } from "../src/llm/mock-planner.js";
import { setup } from "./helpers.js";

test("each agent only sees its own tools", () => {
  const { team } = setup(new ScriptedModel([]));
  const [triage, search] = team.members;
  const lead = team.lead.registry.names();
  assert.ok(lead.includes("delegate_triage") && lead.includes("delegate_search"));
  assert.ok(!lead.includes("move") && !lead.includes("analyze_image"));
  assert.deepEqual(triage!.registry.names(), [...TOOLSETS.triage]);
  assert.deepEqual(search!.registry.names(), [...TOOLSETS.search]);
  assert.ok(!triage!.registry.names().includes("notify_responders"));
});

test("coordinator delegates to triage, gets the case back, then acts on it", async () => {
  const coordinator = new ScriptedModel([
    { content: "hand off", toolCalls: [mockCall("delegate_triage", { case_id: 1 })] },
    { content: null, toolCalls: [mockCall("request_takeover", { case_id: 1, priority: 95, reason: "red" })] },
    mockFinish("case 1 queued"),
  ]);
  const triage = new ScriptedModel([
    { content: null, toolCalls: [mockCall("take_snapshot")] },
    // A tool outside its toolbox is refused, not run.
    { content: null, toolCalls: [mockCall("notify_responders", { case_id: 1, message: "x" })] },
    { content: null, toolCalls: [mockCall("set_triage", { case_id: 1, color: "red", description: "not moving" })] },
    mockFinish("case 1 red"),
  ]);
  const { team, logger, ctx, state, notifier } = setup(new ScriptedModel([]), { models: { coordinator, triage } });
  await state.reportDetection(1, 7.2, 1, 0.8, "x");
  ctx.event = { kind: "detection", caseId: 1 };

  const r = await runEpisode(ctx.event, team.lead, ctx, logger);
  assert.equal(r.summary, "case 1 queued");
  assert.deepEqual(
    r.toolCalls.map((t) => `${t.agent}:${t.name}`),
    ["triage:take_snapshot", "triage:notify_responders", "triage:set_triage", "coordinator:delegate_triage", "coordinator:request_takeover"],
  );
  const refused = r.toolCalls.find((t) => t.name === "notify_responders")!;
  assert.equal(refused.ok, false);
  assert.match(JSON.stringify(refused.result), /unknown tool/);
  assert.equal(notifier.sent.length, 0);

  const delegated = r.toolCalls.find((t) => t.name === "delegate_triage")!.result as { case: { triage: string }; summary: string };
  assert.equal(delegated.case.triage, "red");
  assert.equal(delegated.summary, "case 1 red");
  // The coordinator's second call saw the delegation result.
  assert.match(coordinator.seen[1]!.at(-1)!.role === "tool" ? (coordinator.seen[1]!.at(-1) as { content: string }).content : "", /"triage":"red"/);
  // Sub-agent got its own prompt and a task naming the case.
  assert.match((triage.seen[0]![0] as { content: string }).content, /ROLE: triage/);
  assert.match((triage.seen[0]![1] as { content: string }).content, /TRIAGE TASK: case 1/);
  // Log entries are tagged with the agent.
  assert.ok(state.agentLog.some((e) => e.event === "detection>triage" && e.tool === "set_triage"));
});

test("delegate_search reports new case ids found by the search agent", async () => {
  const coordinator = new ScriptedModel([{ content: null, toolCalls: [mockCall("delegate_search", { instructions: "Go to 6.5, 2.5 and sweep." })] }, mockFinish("searched")]);
  const search = new ScriptedModel([
    { content: null, toolCalls: [mockCall("navigate_to", { x: 6.5, y: 2.5 })] },
    { content: null, toolCalls: [mockCall("sweep_360")] },
    mockFinish("found case 1"),
  ]);
  const { team, logger, ctx } = setup(new ScriptedModel([]), { models: { coordinator, search } });
  // Route around the shelf: go up first.
  await ctx.robot.navigateTo(0.5, 3.6);
  await ctx.robot.navigateTo(5.5, 3.6);
  const r = await runEpisode({ kind: "search_step" }, team.lead, ctx, logger);
  const res = r.toolCalls.find((t) => t.name === "delegate_search")!.result as { new_case_ids: number[] };
  assert.ok(res.new_case_ids.length >= 1);
  assert.match((search.seen[0]![1] as { content: string }).content, /Go to 6\.5, 2\.5/);
});

test("guardrail: triage cannot set a card for a visible person without talking first", async () => {
  const triage = new ScriptedModel([
    { content: null, toolCalls: [mockCall("turn", { degrees: 180 }), mockCall("take_snapshot")] },
    { content: null, toolCalls: [mockCall("analyze_image", { snapshot_id: "sim-1-1" })] },
    { content: null, toolCalls: [mockCall("set_triage", { case_id: 1, color: "red", description: "x" })] },
    { content: null, toolCalls: [mockCall("speak", { text: "Can you hear me?" }), mockCall("listen", { seconds: 3 })] },
    { content: null, toolCalls: [mockCall("set_triage", { case_id: 1, color: "red", description: "no answer" })] },
    mockFinish("red"),
  ]);
  const coordinator = new ScriptedModel([{ content: null, toolCalls: [mockCall("delegate_triage", { case_id: 1 })] }, mockFinish("ok")]);
  const { team, logger, ctx, state, robot } = setup(new ScriptedModel([]), { models: { coordinator, triage } });
  // Stand east of the shelf person, facing away (so the turn faces them).
  await robot.navigateTo(7.2, 2.4);
  await robot.turn(90 - (await robot.getState()).heading);
  await state.reportDetection(1, 7.2, 1.0, 0.8, "x");
  const r = await runEpisode({ kind: "detection", caseId: 1 }, team.lead, ctx, logger);
  const sets = r.toolCalls.filter((t) => t.name === "set_triage");
  assert.equal(sets[0]!.ok, false);
  assert.match(JSON.stringify(sets[0]!.result), /Talk to the survivor/);
  assert.equal(sets[1]!.ok, true);
  assert.equal((await state.getCases())[0]!.triage, "red");
});

test("search delegation stops after one sweep and reports area_complete from the grid", async () => {
  const search = new ScriptedModel([
    { content: null, toolCalls: [mockCall("sweep_360")] },
    { content: null, toolCalls: [mockCall("move", { meters: 1 })] }, // must never run
    mockFinish("x"),
  ]);
  const coordinator = new ScriptedModel([{ content: null, toolCalls: [mockCall("delegate_search")] }, mockFinish("ok")]);
  const { team, logger, ctx } = setup(new ScriptedModel([]), { models: { coordinator, search } });
  const r = await runEpisode({ kind: "search_step" }, team.lead, ctx, logger);
  assert.deepEqual(r.toolCalls.filter((t) => t.agent === "search").map((t) => t.name), ["sweep_360"]);
  const res = r.toolCalls.find((t) => t.name === "delegate_search")!.result as { area_complete: boolean; percent_searched: number };
  assert.equal(res.area_complete, false);
  assert.ok(res.percent_searched < 50);
});
