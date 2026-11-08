import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import jpeg from "jpeg-js";
import { MemoryState } from "../src/adapters/memory-state.js";
import { ConsoleNotifier } from "../src/adapters/notifier.js";
import { PradaRobot, shrinkJpeg } from "../src/adapters/prada-robot.js";
import type { AgentEvent } from "../src/agent/events.js";
import { DetectionIntake } from "../src/agent/intake.js";
import { AgentLogger } from "../src/agent/logger.js";
import { buildTriageDecision, newMemo, runDetectionWorkflow } from "../src/agent/workflow.js";
import { MockPlanner } from "../src/llm/mock-planner.js";
import type { VisionAnalyzer, VisionResult } from "../src/llm/vision.js";
import { buildRegistry } from "../src/tools/index.js";
import type { ToolContext } from "../src/tools/registry.js";

const PHOTO = readFileSync(fileURLToPath(new URL("../scenes/images/photo-trapped-01.jpg", import.meta.url)));

/** A fake prada: /frame serves a JPEG; /say and /listen behave like the Pi speech service. */
function fakePrada(opts: { frame?: Buffer; listen?: (n: number) => { status: number; body: unknown } } = {}) {
  const calls: { path: string; body?: unknown; auth?: string }[] = [];
  let listens = 0;
  const f = (async (input: string | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const body = init?.body ? JSON.parse(String(init.body)) : undefined;
    calls.push({ path: url.pathname, body, auth: (init?.headers as Record<string, string> | undefined)?.Authorization });
    if (url.pathname === "/frame") return new Response(new Uint8Array(opts.frame ?? PHOTO), { status: 200, headers: { "Content-Type": "image/jpeg" } });
    if (url.pathname === "/say") return new Response(JSON.stringify({ ok: true, id: 1, queued: 1, engine: "piper", cached: true }), { status: 202 });
    if (url.pathname === "/listen") {
      const r = opts.listen?.(++listens) ?? { status: 200, body: { ok: true, transcript: "Yes, I can hear you", silent: false } };
      return new Response(JSON.stringify(r.body), { status: r.status });
    }
    return new Response("{}", { status: 404 });
  }) as unknown as typeof fetch;
  const robot = new PradaRobot({ cameraUrl: "http://prada.local:8080", speechUrl: "http://prada.local:8081", token: "tok", fetchImpl: f });
  return { robot, calls };
}

test("prada: snapshot comes from GET /frame as a base64 JPEG; movement and aiming are refused", async () => {
  const { robot, calls } = fakePrada();
  const s = await robot.takeSnapshot();
  assert.equal(calls[0]!.path, "/frame");
  assert.equal(s.mimeType, "image/jpeg");
  assert.equal(Buffer.from(s.imageBase64!, "base64").subarray(0, 2).toString("hex"), "ffd8");
  await assert.rejects(robot.move(), /joystick-only/);
  await assert.rejects(robot.navigateTo(), /joystick-only/);
  await assert.rejects(robot.pointCamera(), /pan-tilt/);
  assert.deepEqual(robot.capabilities, { move: false, aim: false, beacon: false });
});

test("prada: big frames are shrunk under the NVIDIA inline limit", () => {
  // 1600x1200 noisy image → large JPEG.
  const w = 1600, h = 1200, data = Buffer.alloc(w * h * 4);
  for (let i = 0; i < data.length; i++) data[i] = (i * 2654435761) >>> 24;
  const big = Buffer.from(jpeg.encode({ width: w, height: h, data }, 95).data);
  assert.ok(big.length > 170_000);
  const small = shrinkJpeg(big, 170_000);
  assert.ok(small.length <= 170_000, `${small.length}`);
  const dec = jpeg.decode(small);
  assert.ok(Math.max(dec.width, dec.height) <= 1024);
});

test("prada: speak posts to /say; listen maps silence vs unavailable correctly", async () => {
  const outcomes = [
    { status: 200, body: { ok: true, transcript: null, silent: true } },
    { status: 200, body: { ok: true, transcript: null, stt_unavailable: true } },
    { status: 503, body: { ok: false, error: "no microphone" } },
  ];
  const { robot, calls } = fakePrada({ listen: (n) => outcomes[n - 1]! });
  await robot.speak("Can you hear me?");
  assert.deepEqual(calls.at(-1), { path: "/say", body: { text: "Can you hear me?", interrupt: false }, auth: "Bearer tok" });
  assert.deepEqual(await robot.listen(5), { transcript: null });
  assert.equal((await robot.listen(5)).unavailable, true);
  const r = await robot.listen(5);
  assert.equal(r.unavailable, true);
  assert.match(r.reason!, /no microphone/);
});

function wf(robot: PradaRobot, vision: VisionAnalyzer) {
  const state = new MemoryState();
  const registry = buildRegistry();
  const logger = new AgentLogger({ state, dir: null, print: () => {}, color: false });
  const ctx = (event: AgentEvent): ToolContext => ({
    robot, state, vision, notifier: new ConsoleNotifier(() => {}), robotId: 1, responderPhones: [], confirmTimeoutMs: 500, snapshots: new Map(), event, trace: [],
  });
  const deps = { registry, logger, decision: buildTriageDecision(registry, new MockPlanner()), memo: newMemo(), secondLookWaitMs: 0 };
  return { state, ctx, deps };
}

const look = (personVisible: VisionResult["personVisible"], suggestedTriage: VisionResult["suggestedTriage"] = "yellow"): VisionResult => ({
  personVisible, confidence: personVisible === "yes" ? 0.9 : 0.4, description: personVisible === "yes" ? "Adult pinned under concrete, eyes open." : "Something behind debris.", injuries: [], hazards: [], suggestedTriage, nextLook: null, source: "nemotron",
});

test("prada workflow: unclear first look → asks the rescuer to turn the robot (no driving), looks again", async () => {
  const { robot, calls } = fakePrada();
  const answers = [look("unsure"), look("yes")];
  const vision: VisionAnalyzer = { analyze: async () => answers.shift() ?? look("yes") };
  const { state, ctx, deps } = wf(robot, vision);
  const c = await state.reportDetection(1, 3, 0, 0.6, "visor://x");
  const r = await runDetectionWorkflow({ kind: "detection", caseId: c!.id }, ctx({ kind: "detection", caseId: c!.id }), deps);
  const names = r.toolCalls.map((t) => t.name);
  assert.ok(!names.some((n) => ["navigate_to", "turn", "point_camera", "move"].includes(n)), names.join(","));
  assert.ok(state.alerts.some((a) => /Turn the robot toward the person/.test(a.text)));
  assert.equal(calls.filter((x) => x.path === "/frame").length, 2);
  assert.equal((await state.getCases())[0]!.triage, "yellow");
});

test("prada workflow: if the mic is unavailable, the survivor is NOT triaged red for 'not answering'", async () => {
  const { robot } = fakePrada({ listen: () => ({ status: 503, body: { ok: false, error: "no microphone" } }) });
  const vision: VisionAnalyzer = { analyze: async () => look("yes", "yellow"), compareFrames: async () => ({ movement: "yes", detail: "chest moving", confidence: 0.8, source: "nemotron" }) };
  const { state, ctx, deps } = wf(robot, vision);
  const c = await state.reportDetection(1, 3, 0, 0.8, "visor://x");
  await runDetectionWorkflow({ kind: "detection", caseId: c!.id }, ctx({ kind: "detection", caseId: c!.id }), deps);
  const k = (await state.getCases())[0]!;
  assert.equal(k.triage, "yellow");
});

test("intake: stable detection opens one case; repeats fold in; low confidence ignored; re-checks while seen", async () => {
  let now = 1_000_000;
  const state = new MemoryState();
  const events: AgentEvent[] = [];
  const intake = new DetectionIntake(state, (e) => events.push(e), { token: "secret", robotId: 1, now: () => now, recheckMs: 45_000, closeAfterMs: 120_000 });
  assert.equal((await intake.sight({ source: "visor", confidence: 0.3, count: 1, box: null, frameTime: null })).body.ignored, "low_confidence");
  const first = await intake.sight({ source: "visor", confidence: 0.8, count: 1, box: null, frameTime: null });
  assert.equal(first.status, 202);
  assert.equal(events[0]!.kind, "detection");
  now += 5_000;
  assert.equal((await intake.sight({ source: "visor", confidence: 0.9, count: 1, box: null, frameTime: null })).body.ignored, "active_case");
  // A second person shows up → re-check.
  await intake.sight({ source: "visor", confidence: 0.9, count: 2, box: null, frameTime: null });
  assert.equal(events.at(-1)!.kind, "recheck_case");
  // Still seen after 45 s → timed re-check.
  now += 44_000;
  await intake.sight({ source: "visor", confidence: 0.9, count: 2, box: null, frameTime: null });
  now += 2_000;
  const before = events.length;
  await intake.tick();
  assert.equal(events.length, before + 1);
  // Nobody seen for 2 minutes → closed; the next sighting is a new case.
  now += 130_000;
  await intake.tick();
  assert.equal(intake.status().active_case, null);
  assert.equal((await intake.sight({ source: "visor", confidence: 0.8, count: 1, box: null, frameTime: null })).status, 202);
  assert.equal((await state.getCases()).length, 2);
});

test("intake HTTP: LAN access, body validated, status endpoint", async () => {
  const { startOperatorFeed } = await import("../src/operator/feed-server.js");
  const { OperatorVoice } = await import("../src/operator/voice.js");
  const state = new MemoryState();
  const events: AgentEvent[] = [];
  const intake = new DetectionIntake(state, (e) => events.push(e), { token: "s3cret-token", robotId: 1 });
  const feed = await startOperatorFeed(new OperatorVoice({ setTimer: false }), { port: 0, routes: intake.routes });
  try {
    const post = (body: unknown, token = "s3cret-token") =>
      fetch(`${feed.url}/api/detections`, { method: "POST", headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` }, body: JSON.stringify(body) });
    assert.equal((await post({ confidence: 7, count: 1 })).status, 400);
    const ok = await post({ source: "visor", confidence: 0.9, count: 1, box: [0.1, 0.2, 0.3, 0.4], frame_time: Date.now() });
    assert.equal(ok.status, 202);
    assert.deepEqual(await ok.json(), { ok: true, case_id: 1, new_case: true });
    const st = (await (await fetch(`${feed.url}/api/status`)).json()) as { active_case: number };
    assert.equal(st.active_case, 1);
    assert.equal(events[0]!.kind, "detection");
  } finally {
    await feed.close();
  }
});

test("runner: a detection pushed by the intake is not queued a second time by the watcher", async () => {
  const { setup } = await import("./helpers.js");
  const { runner, state } = setup(new MockPlanner());
  const c = await state.reportDetection(1, 3, 3, 0.8, "visor://x");
  runner.push({ kind: "detection", caseId: c!.id });
  runner.queue.tryShift(); // the episode is "running"
  await runner.tick();
  assert.equal(runner.queue.has("detection"), false);
});
