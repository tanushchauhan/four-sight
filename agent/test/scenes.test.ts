import assert from "node:assert/strict";
import { copyFileSync, mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { loadConfig } from "../src/config.js";
import { runScenario, type RunEvent } from "../src/scenes/harness.js";
import { loadScenes, readFrameImage } from "../src/scenes/load.js";
import { SceneRobot } from "../src/scenes/scene-robot.js";
import type { Scene } from "../src/scenes/types.js";

const PHOTO = fileURLToPath(new URL("../scenes/images/photo-noperson-01.jpg", import.meta.url));

function fixtureDir(scenes: Scene[]): string {
  const dir = mkdtempSync(join(tmpdir(), "scenes-"));
  mkdirSync(join(dir, "images"));
  for (const n of ["a.jpg", "b.jpg", "c.jpg"]) copyFileSync(PHOTO, join(dir, "images", n));
  writeFileSync(join(dir, "manifest-test.json"), JSON.stringify({ scenes }));
  return dir;
}

const trapped: Scene = {
  id: "t-trapped",
  title: "Trapped",
  category: "trapped_responsive",
  description: "",
  frames: [{ image: "images/a.jpg", caption: "Adult pinned under a slab, conscious", trigger: "start" }],
  detectorConfidence: 0.8,
  replies: ["Yes, I hear you", "My leg is stuck"],
  expected: { personPresent: true, triage: "yellow", mustDo: ["speak", "listen", "set_triage", "request_takeover", "notify_responders"], mustNotDo: [], notes: "" },
  source: { kind: "photo", license: "test" },
};

const evolving: Scene = {
  id: "t-evolving",
  title: "Hidden then worse",
  category: "evolving",
  description: "",
  frames: [
    { image: "images/a.jpg", caption: "A hand behind a slab", trigger: "start" },
    { image: "images/b.jpg", caption: "From the side: adult pinned, conscious", trigger: "reposition" },
    { image: "images/c.jpg", caption: "Same adult slumped, eyes closed", trigger: "time" },
  ],
  detectorConfidence: 0.45,
  replies: ["Help... my leg"],
  frameReplies: [null, null, []],
  expected: { personPresent: true, triage: "red", mustDo: ["speak", "listen", "set_triage", "notify_responders", "dispatch_responders"], mustNotDo: [], notes: "" },
  source: { kind: "generated", license: "test" },
};

const empty: Scene = {
  ...trapped,
  id: "t-empty",
  category: "no_person",
  frames: [{ image: "images/a.jpg", caption: "Rubble, nobody", trigger: "start" }],
  detectorConfidence: 0.35,
  replies: [],
  expected: { personPresent: false, triage: "unknown", mustDo: [], mustNotDo: ["dispatch_responders"], notes: "" },
};

const config = loadConfig({ LLM_MODE: "mock" });

test("loadScenes merges manifests and skips scenes with missing images", () => {
  const dir = fixtureDir([trapped, { ...trapped, id: "t-broken", frames: [{ image: "images/nope.jpg", caption: "", trigger: "start" }] }]);
  const warnings: string[] = [];
  const scenes = loadScenes(dir, (w) => warnings.push(w));
  assert.deepEqual(scenes.map((s) => s.id), ["t-trapped"]);
  assert.match(warnings.join(" "), /t-broken/);
  const img = readFrameImage("images/a.jpg", dir);
  assert.equal(img.mimeType, "image/jpeg");
  assert.ok(img.base64.length > 100);
});

test("SceneRobot: reposition frames need a viewpoint change, time frames need the clock", async () => {
  const dir = fixtureDir([evolving]);
  const frames: number[] = [];
  const r = new SceneRobot({ scene: evolving, scenesDir: dir, onFrame: (f) => frames.push(f.index) });
  r.start();
  await r.takeSnapshot();
  assert.equal(r.frameIndex, 0, "no move, no new frame");
  await r.pointCamera(3, 0);
  await r.takeSnapshot();
  assert.equal(r.frameIndex, 0, "tiny pan does not count");
  await r.turn(45);
  const snap = await r.takeSnapshot();
  assert.equal(r.frameIndex, 1);
  assert.ok(snap.imageBase64 && snap.mimeType === "image/jpeg");
  await r.turn(45);
  await r.takeSnapshot();
  assert.equal(r.frameIndex, 1, "a time frame is not reached by moving");
  assert.equal(r.advanceTime(), true);
  assert.equal(r.frameIndex, 2);
  assert.deepEqual(frames, [0, 1, 2]);
  // Frame 3 replies are silence.
  assert.equal((await r.listen(3)).transcript, null);
});

for (const mode of ["team", "single"] as const) {
  test(`mock run (${mode}): trapped survivor scores full marks and streams events`, async () => {
    const dir = fixtureDir([trapped]);
    const events: RunEvent[] = [];
    const r = await runScenario(trapped, { config, llm: "mock", agentMode: mode, approvals: "auto", scenesDir: dir, emit: (e) => events.push(e) });
    assert.equal(r.score!.triage.actual, "yellow");
    assert.equal(r.score!.passed, r.score!.total, JSON.stringify(r.score));
    const types = new Set(events.map((e) => e.type));
    for (const t of ["status", "frame", "log", "speech", "state", "result"]) assert.ok(types.has(t as RunEvent["type"]), `missing ${t}`);
    assert.ok(events.some((e) => e.type === "speech" && e.data.who === "survivor" && e.data.text === "Yes, I hear you"));
  });
}

test("mock run: evolving scene escalates yellow → red after the clock advances", async () => {
  const dir = fixtureDir([evolving]);
  const events: RunEvent[] = [];
  const r = await runScenario(evolving, { config, llm: "mock", agentMode: "team", approvals: "auto", scenesDir: dir, emit: (e) => events.push(e) });
  const frames = events.filter((e) => e.type === "frame").map((e) => (e.data as { index: number }).index);
  assert.deepEqual(frames, [0, 1, 2]);
  assert.equal(r.frameIndex, 2);
  assert.equal(r.score!.triage.actual, "red");
  // The offline mock only knows the scene's final triage, so it can't show the yellow→red change
  // (real Nemotron reads each frame's pixels); it must still re-triage after the clock advances.
  const triages = events
    .filter((e): e is Extract<RunEvent, { type: "log" }> => e.type === "log")
    .filter((e) => e.data.type === "tool" && e.data.tool === "set_triage" && e.data.ok)
    .map((e) => (e.data.args as { color: string }).color);
  assert.equal(triages.length, 2);
  assert.equal(triages.at(-1), "red");
  assert.ok(events.some((e) => e.type === "log" && e.data.type === "event" && e.data.event === "recheck_case"));
});

test("mock run: empty rubble is not dispatched", async () => {
  const dir = fixtureDir([empty]);
  const r = await runScenario(empty, { config, llm: "mock", agentMode: "team", approvals: "auto", scenesDir: dir, emit: () => {} });
  assert.ok(!r.actions.includes("dispatch_responders"));
  assert.equal(r.score!.passed, r.score!.total, JSON.stringify(r.score));
});

test("approvals=ask emits a confirm event and waits for the answer", async () => {
  const dir = fixtureDir([evolving]);
  let state: { resolveConfirmation(id: number, ok: boolean): void } | undefined;
  const confirms: number[] = [];
  const r = await runScenario(evolving, {
    config,
    llm: "mock",
    agentMode: "team",
    approvals: "ask",
    scenesDir: dir,
    onState: (s) => (state = s),
    emit: (e) => {
      if (e.type === "confirm") {
        confirms.push(e.data.id);
        setTimeout(() => state!.resolveConfirmation(e.data.id, false), 20);
      }
    },
  });
  assert.ok(confirms.length >= 1);
  assert.ok(!r.actions.includes("dispatch_responders"), "denied dispatch must not run");
});
