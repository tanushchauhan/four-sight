import { loadConfig, loadEnvFile } from "../config.js";
import { runScenario, type RunResult } from "./harness.js";
import { loadScenes } from "./load.js";

/**
 * Headless scoreboard: run every scene (or a filter) and print pass rates.
 *   npm run eval:scenes                 # all scenes, real Nemotron if a key is set
 *   npm run eval:scenes -- --mock       # offline
 *   npm run eval:scenes -- gen-         # only ids containing "gen-"
 *   npm run eval:scenes -- --single     # single-agent mode
 */
loadEnvFile();
const args = process.argv.slice(2);
const config = loadConfig({ ...process.env, LLM_MODE: "mock" });
const llm = args.includes("--mock") || !config.nvidiaApiKey ? "mock" : "nvidia";
const agentMode = args.includes("--single") ? "single" : "team";
const filter = args.find((a) => !a.startsWith("--"));
const scenes = loadScenes().filter((s) => !filter || s.id.includes(filter));
if (!scenes.length) {
  console.log("No scenes found in agent/scenes/manifest-*.json");
  process.exit(1);
}

console.log(`Running ${scenes.length} scene(s): llm=${llm}, mode=${agentMode}\n`);
const rows: { id: string; r?: RunResult; err?: string }[] = [];
for (const s of scenes) {
  process.stdout.write(`${s.id.padEnd(36)} `);
  try {
    const r = await runScenario(s, { config, llm, agentMode, approvals: "auto", emit: () => {} });
    rows.push({ id: s.id, r });
    const sc = r.score;
    const fails = sc ? [!sc.triage.pass && `triage ${sc.triage.actual}≠${sc.triage.expected}`, ...sc.mustDo.filter((m) => !m.pass).map((m) => `missed ${m.tool}`), ...sc.mustNotDo.filter((m) => !m.pass).map((m) => `did ${m.tool}`), sc.personPresent && !sc.personPresent.pass && "person?"].filter(Boolean) : [];
    console.log(`${sc ? `${sc.passed}/${sc.total}` : "-"}  ${(r.durationMs / 1000).toFixed(0)}s  ${r.toolCalls} calls  ${fails.join(", ")}`);
  } catch (e) {
    rows.push({ id: s.id, err: (e as Error).message });
    console.log(`ERROR ${(e as Error).message}`);
  }
}
const scored = rows.filter((x) => x.r?.score);
const passed = scored.reduce((n, x) => n + x.r!.score!.passed, 0);
const total = scored.reduce((n, x) => n + x.r!.score!.total, 0);
const triageOk = scored.filter((x) => x.r!.score!.triage.pass).length;
console.log(`\nChecks passed: ${passed}/${total} (${total ? Math.round((passed / total) * 100) : 0}%) · triage correct ${triageOk}/${scored.length} · errors ${rows.filter((x) => x.err).length}`);
