import { MissionLink } from "./adapters/mission.js";
import { createInterface } from "node:readline";
import { MemoryState } from "./adapters/memory-state.js";
import { loadConfig, loadEnvFile } from "./config.js";
import { DetectionIntake } from "./agent/intake.js";
import { startOperatorFeed } from "./operator/feed-server.js";
import { OperatorVoice } from "./operator/voice.js";
import { commandRoute } from "./operator/commands.js";
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

// On the real robot the visor's YOLO detector starts each case through POST /api/detections.
const mission = config.robotMode === "prada" ? new MissionLink(process.env.DASHBOARD_URL || "http://127.0.0.1:8090", config.agentToken) : null;
mission?.start();
const intake =
  config.robotMode === "prada" ? new DetectionIntake(app.state, (e) => runner.push(e), { token: config.agentToken, robotId: config.robotId, admit: (s) => mission!.admit(s) }) : null;
if (app.voice || intake) {
  const commands = commandRoute(config.agentToken, (text) => runner.push({ kind: "voice_command", text }));
  const feed = await startOperatorFeed(app.voice ?? new OperatorVoice(), {
    host: config.operatorFeedHost, port: config.operatorFeedPort,
    routes: async (req, res, url) => (await commands(req, res, url)) || (await intake?.routes(req, res, url)) || false,
  });
  const url = feed.url.replace("127.0.0.1", "localhost");
  if (app.voice) logger.info(`Operator voice: open ${url}/operator on the rescuer's phone or laptop and tap "enable voice"`);
  if (intake) {
    intake.start();
    logger.info(`Detections: the visor posts to ${url}/api/detections${config.operatorFeedHost === "127.0.0.1" ? "  (set OPERATOR_FEED_HOST=0.0.0.0 so the phone can reach it)" : ""}`);
  }
}

// The agent can't drive prada, so there is no autonomous search mission: it waits for detections.
if (config.robotMode === "prada") logger.info("prada mode: waiting for the visor to report a person (or type a voice command).");
else runner.startMission(process.argv.slice(2).join(" ") || undefined);
await runner.runForever(config.watchIntervalMs);
