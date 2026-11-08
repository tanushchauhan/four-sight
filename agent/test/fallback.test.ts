import assert from "node:assert/strict";
import { test } from "node:test";
import { Health, ResilientChat, ResilientVision, type HealthMode } from "../src/llm/fallback.js";
import type { AssistantTurn, ChatModel } from "../src/llm/types.js";
import type { VisionAnalyzer, VisionResult } from "../src/llm/vision.js";
import type { Snapshot } from "../src/types.js";

const turn = (content: string): AssistantTurn => ({ content, toolCalls: [] });

/** Fake primary: behaviour is switchable between ok / throw / hang. */
function fakePrimary(initial: "ok" | "throw" | "hang" = "ok") {
  const m = {
    name: "nemotron-fake",
    behaviour: initial,
    calls: 0,
    lastUsage: undefined as { promptTokens?: number } | undefined,
    async complete(): Promise<AssistantTurn> {
      m.calls++;
      if (m.behaviour === "throw") throw Object.assign(new Error("rate limited"), { status: 429 });
      if (m.behaviour === "hang") return new Promise<AssistantTurn>(() => {});
      m.lastUsage = { promptTokens: 42 };
      return turn("primary");
    },
  };
  return m;
}

function fakeFallback() {
  const m = { name: "mock-planner", calls: 0, async complete() { m.calls++; return turn("fallback"); } };
  return m satisfies ChatModel;
}

function clock(start = 1_000) {
  const c = { t: start, now: () => c.t, advance: (ms: number) => { c.t += ms; } };
  return c;
}

const snap: Snapshot = { id: "s1", robotId: 1, takenAt: 0, x: 0, y: 0, heading: 0, pan: 0, tilt: 0 } as Snapshot;

test("primary success passes through, with lastUsage", async () => {
  const p = fakePrimary("ok");
  const f = fakeFallback();
  const chat = new ResilientChat(p, f);
  const out = await chat.complete([{ role: "user", content: "hi" }], []);
  assert.equal(out.content, "primary");
  assert.equal(f.calls, 0);
  assert.equal(chat.mode, "healthy");
  assert.equal(chat.name, "nemotron-fake");
  assert.deepEqual(chat.lastUsage, { promptTokens: 42 });
  assert.deepEqual({ ...chat.stats }, { calls: 1, primaryOk: 1, fallbackUsed: 0, failures: 0 });
});

test("primary throw -> fallback answers this call, still healthy below threshold", async () => {
  const p = fakePrimary("throw");
  const f = fakeFallback();
  const chat = new ResilientChat(p, f, { failureThreshold: 2 });
  const out = await chat.complete([], []);
  assert.equal(out.content, "fallback");
  assert.equal(chat.mode, "healthy");
  assert.equal(chat.lastUsage, undefined);
  assert.equal(chat.name, "nemotron-fake→fallback");
  assert.match(chat.lastError ?? "", /429.*rate limited/);
  assert.equal(chat.stats.failures, 1);
  assert.equal(chat.stats.fallbackUsed, 1);
});

test("timeout -> fallback", async () => {
  const p = fakePrimary("hang");
  const f = fakeFallback();
  const chat = new ResilientChat(p, f, { timeoutMs: 20 });
  const t0 = Date.now();
  const out = await chat.complete([], []);
  assert.equal(out.content, "fallback");
  assert.ok(Date.now() - t0 < 100);
  assert.match(chat.lastError ?? "", /timed out after 20 ms/);
});

test("breaker opens after threshold, skips primary during cooldown, half-open probe recovers", async () => {
  const c = clock();
  const p = fakePrimary("throw");
  const f = fakeFallback();
  const changes: [HealthMode, string][] = [];
  const chat = new ResilientChat(p, f, { failureThreshold: 2, cooldownMs: 60_000, now: c.now, onModeChange: (m, r) => changes.push([m, r]) });

  await chat.complete([], []);
  assert.equal(chat.mode, "healthy");
  await chat.complete([], []);
  assert.equal(chat.mode, "degraded");
  assert.equal(p.calls, 2);

  // during cooldown the primary is not called at all, even if it is back
  p.behaviour = "ok";
  c.advance(30_000);
  assert.equal((await chat.complete([], [])).content, "fallback");
  assert.equal(p.calls, 2);
  assert.equal(chat.cooldownRemainingMs, 30_000);

  // cooldown over: probe primary, succeed, healthy
  c.advance(30_000);
  assert.equal((await chat.complete([], [])).content, "primary");
  assert.equal(p.calls, 3);
  assert.equal(chat.mode, "healthy");
  assert.equal(chat.name, "nemotron-fake");

  assert.deepEqual(changes.map((x) => x[0]), ["degraded", "healthy"]);
  assert.match(changes[0]![1], /rate limited/);
});

test("failed half-open probe stays degraded and restarts the cooldown, no extra mode change", async () => {
  const c = clock();
  const p = fakePrimary("throw");
  const f = fakeFallback();
  let fired = 0;
  const chat = new ResilientChat(p, f, { failureThreshold: 1, cooldownMs: 1_000, now: c.now, onModeChange: () => fired++ });
  await chat.complete([], []);
  assert.equal(chat.mode, "degraded");
  c.advance(1_000);
  await chat.complete([], []); // probe fails
  assert.equal(p.calls, 2);
  assert.equal(chat.mode, "degraded");
  assert.equal(chat.cooldownRemainingMs, 1_000);
  await chat.complete([], []);
  assert.equal(p.calls, 2);
  assert.equal(fired, 1);
});

test("a success resets the consecutive-failure count", async () => {
  const p = fakePrimary("throw");
  const chat = new ResilientChat(p, fakeFallback(), { failureThreshold: 2 });
  await chat.complete([], []);
  p.behaviour = "ok";
  await chat.complete([], []);
  p.behaviour = "throw";
  await chat.complete([], []);
  assert.equal(chat.mode, "healthy");
});

test("vision fallback is unsure / source fallback, never yes", async () => {
  const c = clock();
  const primary: VisionAnalyzer = {
    async analyze(): Promise<VisionResult> {
      throw new Error("ECONNRESET");
    },
  };
  const vision = new ResilientVision(primary, { now: c.now, failureThreshold: 2 });
  for (let i = 0; i < 4; i++) {
    const r = await vision.analyze(snap);
    assert.equal(r.personVisible, "unsure");
    assert.notEqual(r.personVisible, "yes");
    assert.equal(r.source, "fallback");
    assert.equal(r.confidence, 0);
    assert.equal(r.suggestedTriage, "unknown");
    assert.match(r.description, /Vision unavailable .*ECONNRESET.*Treat as unconfirmed/);
  }
  assert.equal(vision.mode, "degraded");
  assert.equal(vision.stats.failures, 2); // last two calls skipped the primary
  assert.equal(vision.stats.fallbackUsed, 4);
});

test("vision timeout -> safe fallback; success passes through", async () => {
  let hang = true;
  const good: VisionResult = { personVisible: "yes", confidence: 0.9, description: "person", injuries: [], hazards: [], suggestedTriage: "red", nextLook: null, source: "nemotron" };
  const primary: VisionAnalyzer = { analyze: () => (hang ? new Promise(() => {}) : Promise.resolve(good)) };
  const vision = new ResilientVision(primary, { timeoutMs: 20 });
  assert.equal((await vision.analyze(snap)).source, "fallback");
  hang = false;
  assert.deepEqual(await vision.analyze(snap), good);
});

test("shared Health: degraded when either reporter is, onChange fires once per transition", async () => {
  const c = clock();
  const health = new Health();
  const seen: HealthMode[] = [];
  health.onChange((m) => seen.push(m));
  const p = fakePrimary("throw");
  const chat = new ResilientChat(p, fakeFallback(), { health, failureThreshold: 1, cooldownMs: 10, now: c.now });
  let visionFails = true;
  const ok: VisionResult = { personVisible: "no", confidence: 0.8, description: "", injuries: [], hazards: [], suggestedTriage: "unknown", nextLook: null, source: "nemotron" };
  const vision = new ResilientVision({ analyze: async () => { if (visionFails) throw new Error("down"); return ok; } }, { health, failureThreshold: 1, cooldownMs: 10, now: c.now });

  assert.equal(health.mode, "healthy");
  await chat.complete([], []);
  await vision.analyze(snap);
  assert.equal(health.mode, "degraded");
  assert.equal(health.snapshot().chat?.mode, "degraded");
  assert.equal(health.snapshot().vision?.mode, "degraded");

  c.advance(10);
  p.behaviour = "ok";
  await chat.complete([], []);
  assert.equal(health.mode, "degraded"); // vision still down
  visionFails = false;
  await vision.analyze(snap);
  assert.equal(health.mode, "healthy");
  assert.deepEqual(seen, ["degraded", "healthy"]);
});
