import { MemoryState } from "./adapters/memory-state.js";
import { ConsoleNotifier } from "./adapters/notifier.js";
import { SimRobot } from "./adapters/sim-robot.js";
import { loadConfig, loadEnvFile } from "./config.js";
import { buildApp } from "./wiring.js";

/**
 * Offline end-to-end run: simulated robot + in-memory state.
 * Works with LLM_MODE=mock (no key) or LLM_MODE=nvidia (real Nemotron decides).
 * A simulated rescuer takes over whenever the agent requests it, then hands back.
 */
loadEnvFile();
const config = { ...loadConfig({ ...process.env, ROBOT_MODE: "sim", STATE_MODE: "memory", NOTIFY_MODE: process.env.NOTIFY_MODE ?? "console" }) };
config.sitrepIntervalMs = Number(process.env.SITREP_INTERVAL_MS ?? 60_000);
const app = buildApp(config, { logDir: config.logDir });
const { runner, robot, state, logger } = app;
const sim = robot as SimRobot;
const mem = state as MemoryState;

logger.info(`Demo: llm=${config.llmMode}${config.llmMode === "nvidia" ? ` (${config.reasoningModel})` : " (offline mock planner)"}, simulated robot, in-memory state.`);

// Simulated rescuer: claims any takeover request, looks, then releases.
async function simulatedRescuer(): Promise<void> {
  // Only requests the agent has finished with (triaged case, or not about a case).
  const triaged = new Set(mem.cases.filter((c) => c.triage !== "unknown" || c.description).map((c) => c.id));
  const waiting = mem.requests.filter((r) => r.status === "waiting" && (r.caseId === 0 || triaged.has(r.caseId)));
  if (waiting.length === 0) return;
  for (const r of waiting) r.status = "done";
  logger.info(`\n👤 Rescuer takes over for ${waiting.map((r) => `case ${r.caseId}`).join(", ")}, inspects, then releases.`);
  await sim.setMode("searching");
}

runner.startMission("Search the room for survivors.");
const episodes = await runner.runUntilIdle(Number(process.env.DEMO_EPISODES ?? 40), simulatedRescuer);

const cases = await state.getCases();
const coverage = await state.getCoverage();
const total = (config.searchArea.maxX - config.searchArea.minX) * (config.searchArea.maxY - config.searchArea.minY);
logger.info("\n================ RESULT ================");
logger.info(`Episodes: ${episodes.length}, tool calls: ${episodes.reduce((n, e) => n + e.toolCalls.length, 0)}`);
logger.info(`Coverage: ${Math.round((coverage.length / total) * 100)}% of ${total} cells`);
for (const c of cases) logger.info(`Case ${c.id}: ${c.triage.toUpperCase()} ${c.status} at (${c.x}, ${c.y}) — ${c.description}`);
logger.info(`Hazards: ${mem.hazards.map((h) => `${h.kind}@(${h.x},${h.y})`).join(", ") || "none"}`);
logger.info(`Headset alerts: ${mem.alerts.length}, SMS sent: ${app.notifier instanceof ConsoleNotifier ? app.notifier.sent.length : "n/a"}, robot said: ${sim.spoken.length} lines`);
const s = await robot.getState();
logger.info(`Robot: (${s.x}, ${s.y}) battery ${(s.battery * 100).toFixed(0)}% mode ${s.mode}`);
