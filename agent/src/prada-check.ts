import jpeg from "jpeg-js";
import { PradaRobot } from "./adapters/prada-robot.js";
import { loadConfig, loadEnvFile } from "./config.js";
import { NemotronVision } from "./llm/vision.js";

/**
 * Check each link to the real robot, one at a time, before a full run.
 *   npm run prada:check              camera + speech health + Nemotron VL on a live frame
 *   npm run prada:check -- --say     also say a line through the robot speaker
 *   npm run prada:check -- --listen  also record 4 s from the robot mic and transcribe
 * Needs agent/.env: ROBOT_CONTROL_TOKEN (for say/listen), NVIDIA_API_KEY (for vision), CAMERA_URL/SPEAKER_URL if not prada.local.
 */
loadEnvFile();
const args = process.argv.slice(2);
const cfg = loadConfig({ ...process.env, LLM_MODE: "mock" });
const robot = new PradaRobot({ cameraUrl: cfg.cameraUrl, speechUrl: cfg.speakerUrl, token: cfg.robotControlToken });
let failed = 0;

async function check(name: string, fn: () => Promise<string>): Promise<void> {
  const t0 = performance.now();
  try {
    const detail = await fn();
    console.log(`✓ ${name.padEnd(22)} ${String(Math.round(performance.now() - t0)).padStart(5)} ms  ${detail}`);
  } catch (e) {
    failed++;
    console.log(`✗ ${name.padEnd(22)} ${String(Math.round(performance.now() - t0)).padStart(5)} ms  ${(e as Error).message}`);
  }
}

console.log(`camera ${cfg.cameraUrl}   speech ${cfg.speakerUrl}\n`);

let frame: Awaited<ReturnType<PradaRobot["takeSnapshot"]>> | null = null;
await check("camera GET /frame", async () => {
  frame = await robot.takeSnapshot();
  const bytes = Buffer.from(frame.imageBase64!, "base64");
  const { width, height } = jpeg.decode(bytes, { useTArray: true });
  return `${width}x${height} JPEG, ${Math.round(bytes.length / 1024)} KB as sent to Nemotron`;
});

await check("speech GET /health", async () => {
  const r = await fetch(`${cfg.speakerUrl.replace(/\/$/, "")}/health`, { signal: AbortSignal.timeout(3000) });
  const j = (await r.json()) as Record<string, unknown>;
  if (!r.ok || !j.ok) throw new Error(`HTTP ${r.status} ${JSON.stringify(j)}`);
  return `tts=${j.engine} speaker=${j.device} stt=${j.stt ?? "?"} mic=${j.mic ?? "?"}${j.last_error ? ` last_error=${j.last_error}` : ""}`;
});

if (cfg.nvidiaApiKey && frame) {
  await check("Nemotron VL on frame", async () => {
    const v = new NemotronVision(cfg.nvidiaApiKey, cfg.nvidiaBaseUrl, cfg.visionModel, cfg.visionThinking);
    const r = await v.analyze(frame!);
    return `person=${r.personVisible} (${r.confidence}) triage=${r.suggestedTriage}: ${r.description.slice(0, 120)}`;
  });
} else console.log("- Nemotron VL skipped (no NVIDIA_API_KEY or no frame)");

if (args.includes("--say")) {
  await check("speaker POST /say", async () => {
    if (!cfg.robotControlToken) throw new Error("ROBOT_CONTROL_TOKEN missing in agent/.env");
    await robot.speak("This is a rescue robot. Help is coming. Can you hear me?");
    return "queued: listen for it on prada";
  });
}

if (args.includes("--listen")) {
  await check("mic POST /listen 4 s", async () => {
    if (!cfg.robotControlToken) throw new Error("ROBOT_CONTROL_TOKEN missing in agent/.env");
    console.log("  … speak near the robot now");
    const r = await robot.listen(4);
    if (r.unavailable) throw new Error(r.reason ?? "unavailable");
    return r.transcript ? `heard: "${r.transcript}"` : "silence (nothing above the voice threshold)";
  });
}

console.log(failed ? `\n${failed} check(s) failed.` : "\nAll checks passed.");
process.exit(failed ? 1 : 0);
