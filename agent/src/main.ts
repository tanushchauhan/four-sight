import { createInterface } from "node:readline";
import { MemoryState } from "./adapters/memory-state.js";
import { loadConfig, loadEnvFile } from "./config.js";
import { buildApp } from "./wiring.js";

/**
 * Long-running agent. Type lines to act as the rescuer's voice (stands in for speech-to-text):
 *   any text      → voice_command event
 *   /release      → rescuer hands control back (operator_released)
 *   /approve N    → approve confirmation N (memory state only)  /deny N
 *   /sitrep       → force a sitrep
 *   /quit
 */
loadEnvFile();
const config = loadConfig();
const app = buildApp(config);
const { runner, robot, state, logger } = app;

logger.info(
  `rescue-agent: llm=${config.llmMode}${config.llmMode === "nvidia" ? ` (${config.reasoningModel}, tools=${config.toolCallMode})` : ""} robot=${config.robotMode} state=${config.stateMode} notify=${config.notifyMode}`,
);
logger.info("Type a voice command, or /release /approve N /deny N /sitrep /quit");

const rl = createInterface({ input: process.stdin });
rl.on("line", async (raw) => {
  const line = raw.trim();
  if (!line) return;
  const [cmd, arg] = line.split(/\s+/);
  try {
    if (cmd === "/quit") {
      runner.stop();
      rl.close();
      await robot.close?.();
      process.exit(0);
    } else if (cmd === "/release") {
      await robot.setMode("searching");
      runner.push({ kind: "operator_released", robotId: config.robotId });
    } else if ((cmd === "/approve" || cmd === "/deny") && state instanceof MemoryState) {
      state.resolveConfirmation(Number(arg), cmd === "/approve");
    } else if (cmd === "/sitrep") {
      runner.push({ kind: "sitrep_due" });
    } else {
      runner.push({ kind: "voice_command", text: line });
    }
  } catch (e) {
    logger.info(`command failed: ${(e as Error).message}`);
  }
});

runner.startMission(process.argv.slice(2).join(" ") || undefined);
await runner.runForever(config.watchIntervalMs);
