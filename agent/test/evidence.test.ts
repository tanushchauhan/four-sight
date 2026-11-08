import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizeDeg } from "../src/adapters/robot.js";
import { MemoryState } from "../src/adapters/memory-state.js";
import { ConsoleNotifier } from "../src/adapters/notifier.js";
import { AutoVision, SimVision, unsupportedMovement, type MovementResult, type VisionAnalyzer, type VisionResult } from "../src/llm/vision.js";
import { SceneRobot } from "../src/scenes/scene-robot.js";
import type { Scene } from "../src/scenes/types.js";
import { ALL_TOOLS, buildRegistry } from "../src/tools/index.js";
import type { ToolContext } from "../src/tools/registry.js";
import type { SimScene, Snapshot } from "../src/types.js";
import type { ChatModel } from "../src/llm/types.js";
import { setup } from "./helpers.js";

const snap = (id: string, simScene?: SimScene, extra: Partial<Snapshot> = {}): Snapshot => ({
  id,
  robotId: 1,
  takenAt: 0,
  x: 0,
  y: 0,
  heading: 0,
  pan: 0,
  tilt: 0,
  simScene,
  ...extra,
});

// The tool tests never call the chat model.
const noModel = {} as ChatModel;

test("SimVision.compareFrames uses the moving ground truth", async () => {
  const v = new SimVision();
  const yes = await v.compareFrames(snap("a", { visibility: "clear", description: "", moving: true }), snap("b", { visibility: "clear", description: "", moving: true }));
  assert.equal(yes.movement, "yes");
  assert.equal(yes.source, "sim");
  const no = await v.compareFrames(snap("a", { visibility: "clear", description: "", moving: false }), snap("b", { visibility: "clear", description: "", moving: false }));
  assert.equal(no.movement, "no");
  const unknown = await v.compareFrames(snap("a", { visibility: "clear", description: "" }), snap("b", { visibility: "clear", description: "" }));
  assert.equal(unknown.movement, "unsure");
  const empty = await v.compareFrames(snap("a", { visibility: "none", description: "" }), snap("b", { visibility: "none", description: "" }));
  assert.equal(empty.movement, "unsure");
});

test("sim world: yellow survivor behind the couch moves, red one by the shelf does not", async () => {
  const { robot } = setup(noModel);
  const v = new SimVision();
  // Red: walk to (5.5, 1.0) and face the person at (7.2, 1.0).
  await robot.navigateTo(5.5, 1.0);
  let s = await robot.getState();
  await robot.turn(normalizeDeg(0 - s.heading));
  const r1 = await robot.takeSnapshot();
  const r2 = await robot.takeSnapshot();
  assert.equal(r1.simScene?.truthTriage, "red");
  assert.equal(r1.simScene?.moving, false);
  assert.equal((await v.compareFrames(r1, r2)).movement, "no");
  // Yellow: view from the north side of the couch, looking south at (4.2, 4.1).
  await robot.navigateTo(5.5, 5.5);
  await robot.navigateTo(4.2, 5.5);
  s = await robot.getState();
  await robot.turn(normalizeDeg(-90 - s.heading));
  const y1 = await robot.takeSnapshot();
  const y2 = await robot.takeSnapshot();
  assert.equal(y1.simScene?.truthTriage, "yellow");
  assert.equal(y1.simScene?.moving, true);
  assert.equal((await v.compareFrames(y1, y2)).movement, "yes");
});

test("check_movement tool end to end with the SimRobot", async () => {
  const { robot, registry, ctx } = setup(noModel);
  assert.ok(ALL_TOOLS.some((t) => t.name === "check_movement"));
  await robot.navigateTo(5.5, 1.0);
  const s = await robot.getState();
  await robot.turn(normalizeDeg(0 - s.heading));
  const t0 = Date.now();
  const out = await registry.execute("check_movement", JSON.stringify({ seconds: 3 }), ctx);
  assert.ok(Date.now() - t0 < 1000, "sim robot should not really wait");
  assert.equal(out.ok, true);
  const r = out.result as { movement: string; detail: string; confidence: number; snapshot_ids: string[] };
  assert.equal(r.movement, "no");
  assert.equal(r.snapshot_ids.length, 2);
  assert.notEqual(r.snapshot_ids[0], r.snapshot_ids[1]);
  for (const id of r.snapshot_ids) assert.ok(ctx.snapshots.has(id));
  assert.ok(r.confidence > 0 && r.confidence <= 1);
  // Out-of-range seconds is rejected by the schema.
  const bad = await registry.execute("check_movement", JSON.stringify({ seconds: 9 }), ctx);
  assert.equal(bad.ok, false);
});

function scene(frames: Scene["frames"]): Scene {
  return {
    id: "t-move",
    title: "movement test",
    category: "unresponsive",
    description: "",
    frames,
    detectorConfidence: 0.8,
    replies: [],
    expected: { personPresent: true, triage: "red", mustDo: [], mustNotDo: [], notes: "" },
    source: { kind: "generated", license: "test" },
  };
}

function sceneCtx(robot: SceneRobot, vision: VisionAnalyzer): ToolContext {
  return {
    robot,
    state: new MemoryState({ autoConfirm: "approve" }),
    vision,
    notifier: new ConsoleNotifier(() => {}),
    robotId: 1,
    responderPhones: [],
    confirmTimeoutMs: 100,
    snapshots: new Map(),
    event: { kind: "search_step" },
    trace: [],
  };
}

test("scene frames carry the moving flag into check_movement (offline vision)", async () => {
  const sr = new SceneRobot({
    scene: scene([
      { image: "images/gen-deteriorating-1.jpg", caption: "alert, propped up", trigger: "start", moving: true },
      { image: "images/gen-deteriorating-2.jpg", caption: "face down, still", trigger: "time", moving: false },
      { image: "images/gen-deteriorating-3.jpg", caption: "no flag", trigger: "time" },
    ]),
  });
  const registry = buildRegistry();
  // AutoVision with no real model: frames have pixels, but the sim stand-in answers.
  const ctx = sceneCtx(sr, new AutoVision(null));
  const run = async () => (await registry.execute("check_movement", "{}", ctx)).result as { movement: string; snapshot_ids: string[] };
  assert.equal((await run()).movement, "yes");
  assert.equal(sr.advanceTime(), true);
  assert.equal((await run()).movement, "no");
  assert.equal(sr.advanceTime(), true);
  const last = await run();
  assert.equal(last.movement, "unsure");
  assert.notEqual(last.snapshot_ids[0], last.snapshot_ids[1]);
});

class FakeReal implements VisionAnalyzer {
  calls = 0;
  async analyze(): Promise<VisionResult> {
    throw new Error("not used");
  }
  async compareFrames(): Promise<MovementResult> {
    this.calls++;
    return { movement: "yes", detail: "chest rises", confidence: 0.7, source: "nemotron" };
  }
}

test("AutoVision.compareFrames: real model for pixel frames, sim otherwise", async () => {
  const real = new FakeReal();
  const auto = new AutoVision(real);
  const px = { imageBase64: "AAAA", mimeType: "image/jpeg" };
  const r = await auto.compareFrames(snap("a", { visibility: "clear", description: "", moving: false }, px), snap("b", { visibility: "clear", description: "", moving: false }, px));
  assert.equal(r.source, "nemotron");
  assert.equal(r.movement, "yes");
  assert.equal(real.calls, 1);
  // One frame without pixels: the sim answers from ground truth.
  const s = await auto.compareFrames(snap("a", { visibility: "clear", description: "", moving: false }, px), snap("b", { visibility: "clear", description: "", moving: false }));
  assert.equal(s.source, "sim");
  assert.equal(s.movement, "no");
  assert.equal(real.calls, 1);
  // A real analyzer without compareFrames falls back to "unsure".
  const noCmp: VisionAnalyzer = { analyze: real.analyze };
  const u = await new AutoVision(noCmp).compareFrames(snap("a", undefined, px), snap("b", undefined, px));
  assert.equal(u.movement, "unsure");
  assert.equal(u.detail, "movement check not supported");
});

test("check_movement with an analyzer that cannot compare frames returns unsure", async () => {
  const { registry, ctx } = setup(noModel);
  ctx.vision = { analyze: new SimVision().analyze };
  const out = await registry.execute("check_movement", "{}", ctx);
  assert.equal(out.ok, true);
  const r = out.result as { movement: string; detail: string; confidence: number };
  const { source: _s, ...expected } = unsupportedMovement();
  assert.deepEqual({ movement: r.movement, detail: r.detail, confidence: r.confidence }, expected);
});

test("get_sensor_readings describes heat as supporting evidence", () => {
  const t = ALL_TOOLS.find((x) => x.name === "get_sensor_readings")!;
  assert.match(t.description, /null = no heat sensor/);
  assert.match(t.description, /supporting evidence/);
});
