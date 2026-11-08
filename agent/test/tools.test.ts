import assert from "node:assert/strict";
import { test } from "node:test";
import { ScriptedModel } from "../src/llm/mock-planner.js";
import { validateArgs } from "../src/tools/registry.js";
import { setup } from "./helpers.js";

test("validateArgs coerces numeric strings and enforces schema", () => {
  const schema = {
    type: "object" as const,
    properties: { n: { type: "number" as const, minimum: 0, maximum: 10 }, c: { type: "string" as const, enum: ["red", "green"] } },
    required: ["n"],
  };
  assert.deepEqual(validateArgs(schema, { n: "3.5", c: "red", extra: 1 }), { n: 3.5, c: "red" });
  assert.throws(() => validateArgs(schema, {}), /missing required/);
  assert.throws(() => validateArgs(schema, { n: 11 }), /<= 10/);
  assert.throws(() => validateArgs(schema, { n: 1, c: "blue" }), /one of/);
});

test("unknown tool and bad JSON come back as readable errors, not throws", async () => {
  const { registry, ctx } = setup(new ScriptedModel([]));
  const a = await registry.execute("teleport", "{}", ctx);
  assert.equal(a.ok, false);
  assert.match(JSON.stringify(a.result), /unknown tool/);
  const b = await registry.execute("move", "{not json", ctx);
  assert.equal(b.ok, false);
  assert.match(JSON.stringify(b.result), /bad arguments/);
});

test("every tool has a description and an object schema", () => {
  const { registry } = setup(new ScriptedModel([]));
  for (const spec of registry.specs()) {
    assert.ok(spec.description.length > 10, spec.name);
    assert.equal((spec.parameters as { type: string }).type, "object", spec.name);
  }
  assert.ok(registry.names().length >= 20);
});

test("dispatch is blocked when the rescuer denies it", async () => {
  const { registry, ctx, state } = setup(new ScriptedModel([]), { autoConfirm: "deny" });
  const c = await state.reportDetection(1, 2, 2, 0.8, "x");
  const out = await registry.execute("dispatch_responders", JSON.stringify({ case_id: c!.id }), ctx);
  assert.equal(out.ok, false);
  assert.equal(out.blocked, true);
  assert.equal((await state.getCases())[0]!.status, "found");
  assert.equal(state.teams.every((t) => t.status === "available"), true);
});

test("dispatch waits for approval and then runs", async () => {
  const { registry, ctx, state } = setup(new ScriptedModel([]), { autoConfirm: "none", confirmTimeoutMs: 5000 });
  const c = await state.reportDetection(1, 2, 2, 0.8, "x");
  const pending = registry.execute("dispatch_responders", JSON.stringify({ case_id: c!.id }), ctx);
  await new Promise((r) => setTimeout(r, 50));
  state.resolveConfirmation(state.confirmations[0]!.id, true);
  const out = await pending;
  assert.equal(out.ok, true);
  assert.equal((await state.getCases())[0]!.status, "responders_sent");
});

test("unanswered confirmation expires", async () => {
  const { registry, ctx, state } = setup(new ScriptedModel([]), { autoConfirm: "none", confirmTimeoutMs: 100 });
  const c = await state.reportDetection(1, 2, 2, 0.8, "x");
  const out = await registry.execute("dispatch_responders", JSON.stringify({ case_id: c!.id }), ctx);
  assert.equal(out.blocked, true);
  assert.match(JSON.stringify(out.result), /expired/);
});

test("notify_responders sends one SMS with case location and triage", async () => {
  const { registry, ctx, state, notifier } = setup(new ScriptedModel([]));
  const c = await state.reportDetection(1, 4.2, 4.1, 0.8, "x");
  await state.setTriage(c!.id, "red", "Not moving.");
  const out = await registry.execute("notify_responders", JSON.stringify({ case_id: c!.id, message: "bring a stretcher" }), ctx);
  assert.equal(out.ok, true);
  assert.equal(notifier.sent.length, 1);
  assert.match(notifier.sent[0]!.body, /RED/);
  assert.match(notifier.sent[0]!.body, /x=4\.2/);
  assert.match(notifier.sent[0]!.body, /stretcher/);
});

test("move stops at the couch and reports why", async () => {
  const { registry, ctx, robot } = setup(new ScriptedModel([]));
  await robot.navigateTo(2.5, 2.9);
  await robot.turn(-(await robot.getState()).heading); // face +x, couch starts at x=3
  const out = await registry.execute("move", JSON.stringify({ meters: 2 }), ctx);
  const r = out.result as { ok: boolean; stoppedBy: string; movedM: number };
  assert.equal(r.ok, false);
  assert.equal(r.stoppedBy, "obstacle");
  assert.ok(r.movedM < 0.5);
});

test("second look: partial view from one side, clear view from the other", async () => {
  const { registry, ctx, robot } = setup(new ScriptedModel([]));
  // South-west of the couch: person hidden.
  await robot.navigateTo(2.8, 2.2);
  const faceDeg = (Math.atan2(4.1 - 2.2, 4.2 - 2.8) * 180) / Math.PI;
  await robot.turn(faceDeg - (await robot.getState()).heading);
  const s1 = (await registry.execute("take_snapshot", "{}", ctx)).result as { snapshot_id: string };
  const a1 = (await registry.execute("analyze_image", JSON.stringify({ snapshot_id: s1.snapshot_id }), ctx)).result as { personVisible: string };
  assert.equal(a1.personVisible, "unsure");
  // East side, facing west (route under the couch, then up).
  await robot.navigateTo(5.5, 2.0);
  await robot.navigateTo(5.6, 4.1);
  await robot.turn(180 - (await robot.getState()).heading);
  const s2 = (await registry.execute("take_snapshot", "{}", ctx)).result as { snapshot_id: string };
  const a2 = (await registry.execute("analyze_image", JSON.stringify({ snapshot_id: s2.snapshot_id }), ctx)).result as { personVisible: string; suggestedTriage: string };
  assert.equal(a2.personVisible, "yes");
  assert.equal(a2.suggestedTriage, "yellow");
});
