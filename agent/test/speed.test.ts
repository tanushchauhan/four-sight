import assert from "node:assert/strict";
import { test } from "node:test";
import { runEpisode } from "../src/agent/loop.js";
import { MockPlanner, ScriptedModel, mockCall, mockFinish } from "../src/llm/mock-planner.js";
import { withRetry } from "../src/llm/nemotron.js";
import type { ChatMessage } from "../src/llm/types.js";
import { MAX_LOOKS } from "../src/tools/index.js";
import { setup } from "./helpers.js";

test("instant alert: rescuer is alerted on the first clear sighting, before triage, once per case", async () => {
  const { runner, state, robot } = setup(new MockPlanner());
  // Put the robot facing the shelf survivor with a clear view.
  await robot.navigateTo(7.2, 2.4);
  await state.reportDetection(1, 7.2, 1.0, 0.8, "x");
  const ep = await runner.runEvent({ kind: "detection", caseId: 1 });
  const names = ep.toolCalls.map((t) => t.name);
  const alert = names.indexOf("instant_alert");
  assert.ok(alert >= 0, `no instant alert in ${names.join(",")}`);
  assert.ok(alert < names.indexOf("set_triage"), "alert must come before triage");
  assert.equal(names.filter((n) => n === "instant_alert").length, 1);
  assert.ok(state.alerts.some((a) => /Person seen, case 1/.test(a.text)));
  assert.ok(state.requests.some((r) => r.caseId === 1 && r.priority >= 80));
});

test("look budget: a 4th analyze_image in one triage run is refused", async () => {
  const look = () => [mockCall("take_snapshot")];
  const triage = new ScriptedModel([
    ...Array.from({ length: MAX_LOOKS + 1 }, () => [{ content: null, toolCalls: look() }, { content: null, toolCalls: [mockCall("analyze_image", { snapshot_id: "LAST" })] }]).flat(),
    mockFinish("done"),
  ]);
  // Point each analyze_image at the snapshot just taken.
  const orig = triage.complete.bind(triage);
  triage.complete = async (msgs: ChatMessage[]) => {
    const t = await orig(msgs);
    for (const c of t.toolCalls) {
      if (c.name === "analyze_image") {
        const lastSnap = [...msgs].reverse().find((m) => m.role === "tool" && m.name === "take_snapshot") as { content: string } | undefined;
        c.arguments = JSON.stringify({ snapshot_id: JSON.parse(lastSnap!.content).snapshot_id });
      }
    }
    return t;
  };
  const coordinator = new ScriptedModel([{ content: null, toolCalls: [mockCall("delegate_triage", { case_id: 1 })] }, mockFinish("ok")]);
  const { team, logger, ctx, state } = setup(new ScriptedModel([]), { models: { coordinator, triage } });
  await state.reportDetection(1, 3, 3, 0.4, "x");
  const r = await runEpisode({ kind: "detection", caseId: 1 }, team.lead, ctx, logger);
  const looks = r.toolCalls.filter((t) => t.name === "analyze_image");
  assert.equal(looks.length, MAX_LOOKS + 1);
  assert.equal(looks.filter((t) => t.ok).length, MAX_LOOKS);
  assert.match(JSON.stringify(looks.at(-1)!.result), /Look budget used/);
});

test("withRetry: backs off on 429, fails fast on 404, retries 5xx", async () => {
  const waits: number[] = [];
  const sleep = async (ms: number) => void waits.push(ms);
  let n = 0;
  const ok = await withRetry(async () => {
    if (n++ < 2) throw Object.assign(new Error("rate"), { status: 429 });
    return "done";
  }, 4, sleep);
  assert.equal(ok, "done");
  assert.deepEqual(waits, [2000, 4000]);

  let calls = 0;
  await assert.rejects(withRetry(async () => { calls++; throw Object.assign(new Error("nf"), { status: 404 }); }, 4, sleep));
  assert.equal(calls, 1);

  waits.length = 0;
  let m = 0;
  await withRetry(async () => { if (m++ < 1) throw Object.assign(new Error("x"), { status: 503, headers: { get: () => "1" } }); return 1; }, 4, sleep);
  assert.deepEqual(waits, [1000], "honours Retry-After");
});
