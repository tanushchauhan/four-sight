import assert from "node:assert/strict";
import { test } from "node:test";
import { loadConfig } from "../src/config.js";
import { startOperatorFeed } from "../src/operator/feed-server.js";
import type { MemoryState } from "../src/adapters/memory-state.js";
import type { SimRobot } from "../src/adapters/sim-robot.js";
import { buildApp } from "../src/wiring.js";
import { simulatedRescuer } from "./helpers.js";

test("app wiring: headset alerts during a mission become spoken operator updates on the feed", async () => {
  const config = loadConfig({ LLM_MODE: "mock", ROBOT_MODE: "sim", STATE_MODE: "memory", NOTIFY_MODE: "console" });
  const app = buildApp(config, { print: () => {}, logDir: null });
  assert.ok(app.voice, "operator voice on by default");
  const feed = await startOperatorFeed(app.voice!, { port: 0 });
  try {
    const heard: { text: string; level: string }[] = [];
    app.voice!.subscribe((u) => heard.push(u));
    app.runner.startMission();
    await app.runner.runUntilIdle(40, simulatedRescuer(app.state as MemoryState, app.robot as SimRobot));
    const texts = heard.map((u) => u.text).join(" | ");
    // Non-critical lines are paced 4 s apart (this mission takes milliseconds), critical ones always go out.
    assert.ok(heard.length >= 2, texts);
    assert.match(texts, /Person seen|red|yellow/i);
    assert.ok(heard.some((u) => u.level === "critical"), "a red case should be spoken as critical");
    // And the feed serves what was spoken.
    const recent = (await (await fetch(`${feed.url}/api/operator/recent?n=50`)).json()) as unknown[];
    assert.ok(recent.length >= 1);
  } finally {
    await feed.close();
  }
});

test("app wiring: OPERATOR_VOICE=off and SPEAKER_MODE validation", () => {
  const off = buildApp(loadConfig({ LLM_MODE: "mock", OPERATOR_VOICE: "off" }), { print: () => {}, logDir: null });
  assert.equal(off.voice, null);
  assert.throws(() => loadConfig({ SPEAKER_MODE: "pi" }), /ROBOT_CONTROL_TOKEN/);
});
