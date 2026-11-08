import { readFileSync } from "node:fs";
import { join } from "node:path";
import { COORDINATOR_PROMPT, SEARCH_PROMPT, TRIAGE_PROMPT } from "../agent/prompts.js";
import { TOOLSETS } from "../agent/team.js";
import { loadConfig, loadEnvFile } from "../config.js";
import { usageOf } from "../llm/nemotron.js";
import { buildRegistry } from "../tools/index.js";
import { runScenario } from "./harness.js";
import { SCENES_DIR, loadScenes } from "./load.js";

/**
 * Response-time benchmark against the real NVIDIA API.
 *   npm run latency -- --micro            per-model call latency on each role's real prompt + tools
 *   npm run latency -- --scenes [ids...]  end-to-end milestones per scene
 *   npm run latency -- --micro --n 5      repetitions per measurement (default 4)
 */
loadEnvFile();
const config = loadConfig({ ...process.env, LLM_MODE: "mock" });
if (!config.nvidiaApiKey) throw new Error("NVIDIA_API_KEY missing in agent/.env");
const args = process.argv.slice(2);
const N = Number(args[args.indexOf("--n") + 1]) || 4;
const BASE = config.nvidiaBaseUrl;

const pct = (xs: number[], p: number) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))]!;
};
const fmt = (ms: number) => `${(ms / 1000).toFixed(2)}s`;

async function chat(body: Record<string, unknown>) {
  const t0 = performance.now();
  const r = await fetch(`${BASE}/chat/completions`, {
    method: "POST",
    headers: { Authorization: `Bearer ${config.nvidiaApiKey}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(120_000),
  });
  const j = (await r.json()) as { choices?: { message?: { content?: string; tool_calls?: unknown[] } }[]; usage?: unknown };
  const ms = performance.now() - t0;
  if (!r.ok) throw new Error(`${r.status} ${JSON.stringify(j).slice(0, 160)}`);
  const msg = j.choices?.[0]?.message;
  return { ms, usage: usageOf(j), toolCalls: msg?.tool_calls?.length ?? 0, content: msg?.content ?? "" };
}

async function measure(label: string, body: () => Record<string, unknown>) {
  const rows: Awaited<ReturnType<typeof chat>>[] = [];
  const errors: string[] = [];
  for (let i = 0; i < N; i++) {
    try {
      rows.push(await chat(body()));
    } catch (e) {
      errors.push((e as Error).message);
    }
  }
  if (!rows.length) return console.log(`${label.padEnd(58)} FAILED: ${errors[0]}`);
  const ms = rows.map((r) => r.ms);
  const reason = Math.round(rows.reduce((n, r) => n + (r.usage.reasoningTokens ?? 0), 0) / rows.length);
  const out = Math.round(rows.reduce((n, r) => n + (r.usage.completionTokens ?? 0), 0) / rows.length);
  const tc = rows.filter((r) => r.toolCalls > 0).length;
  console.log(
    `${label.padEnd(58)} p50 ${fmt(pct(ms, 50))}  min ${fmt(Math.min(...ms))}  max ${fmt(Math.max(...ms))}  out ${String(out).padStart(4)} tok (reasoning ${String(reason).padStart(4)})  tool-call ${tc}/${rows.length}${errors.length ? `  errors ${errors.length}` : ""}`,
  );
}

const registry = buildRegistry();
const toolsFor = (names: readonly string[], extra: { name: string; description: string; parameters: Record<string, unknown> }[] = []) =>
  [...registry.subset(names).specs(), ...extra].map((t) => ({ type: "function", function: t }));
const delegateSpecs = [
  { name: "delegate_triage", description: "Hand a case to the triage agent.", parameters: { type: "object", properties: { case_id: { type: "integer" }, instructions: { type: "string" } }, required: ["case_id"] } },
  { name: "delegate_search", description: "Hand movement to the search agent for one sweep.", parameters: { type: "object", properties: { instructions: { type: "string" } } } },
];
const situation = "Current situation:\n- Robot 1 at (1.0, 3.0) heading 0°, camera pan 0° tilt 0°, battery 92%, mode searching\n- Open cases: #1 unknown found at (2.8, 3.0)";

async function micro() {
  console.log(`\n== Model call latency (n=${N} each, sequential) ==\n`);
  const roles = [
    { role: "coordinator", prompt: COORDINATOR_PROMPT, tools: toolsFor(TOOLSETS.coordinator, delegateSpecs), task: `DETECTION. The robot's on-board detector flagged a possible person: case 1. Confirm and triage it.\n\n${situation}` },
    { role: "triage", prompt: TRIAGE_PROMPT, tools: toolsFor(TOOLSETS.triage), task: `TRIAGE TASK: case 1 at (2.8, 3.0), detector confidence 0.45.\nIt is 1.8 m from the robot at 0° relative to the robot's nose. Pan the camera toward it.\n\n${situation}` },
    { role: "search", prompt: SEARCH_PROMPT, tools: toolsFor(TOOLSETS.search), task: `SEARCH TASK: Continue the search: next unsearched spot, then sweep.\n\n${situation}` },
  ];
  const models = [...new Set([config.coordinatorModel, config.triageModel, config.searchModel, "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning"])];
  for (const r of roles) {
    for (const model of models) {
      const tag = model === ({ coordinator: config.coordinatorModel, triage: config.triageModel, search: config.searchModel } as Record<string, string>)[r.role] ? " (current)" : "";
      await measure(`${r.role.padEnd(11)} ${model.replace("nvidia/", "")}${tag}`, () => ({
        model,
        messages: [{ role: "system", content: r.prompt }, { role: "user", content: r.task }],
        tools: r.tools,
        tool_choice: "auto",
        temperature: 0.2,
        max_tokens: 4096,
      }));
    }
  }

  console.log(`\n== Vision latency by image size (n=${N}) ==\n`);
  const prompt = 'Reply with ONLY JSON: {"person_visible":"yes|no|unsure","confidence":0-1,"description":"...","injuries":[],"hazards":[],"suggested_triage":"red|yellow|green|black|unknown","next_look":null}';
  const scenes = loadScenes(SCENES_DIR, () => {});
  const imgs = ["photo-partial-01", "photo-noperson-01", "photo-trapped-01", "photo-multiple-01"]
    .map((id) => scenes.find((s) => s.id === id))
    .filter(Boolean)
    .map((s) => ({ id: s!.id, b64: readFileSync(join(SCENES_DIR, s!.frames[0]!.image)).toString("base64") }));
  const visionModels = [config.visionModel, "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning", "nvidia/cosmos-reason2-8b"];
  for (const model of visionModels) {
    for (const im of imgs) {
      await measure(`vision ${model.replace("nvidia/", "").slice(0, 34).padEnd(34)} ${im.id} ${Math.round((im.b64.length * 3) / 4 / 1024)}KB`, () => ({
        model,
        temperature: 0.1,
        max_tokens: 3000,
        messages: [{ role: "user", content: [{ type: "text", text: prompt }, { type: "image_url", image_url: { url: `data:image/jpeg;base64,${im.b64}` } }] }],
      }));
    }
  }
}

async function scenes() {
  const ids = args.filter((a) => !a.startsWith("--") && a !== String(N));
  const all = loadScenes(SCENES_DIR, () => {});
  const pick = ids.length ? all.filter((s) => ids.some((i) => s.id.includes(i))) : all;
  const mode = args.includes("--single") ? "single" : "team";
  console.log(`\n== End-to-end response time, ${pick.length} scene(s), ${mode} mode ==\n`);
  console.log(`${"scene".padEnd(26)} ${"total".padStart(6)} | ${"look".padStart(5)} ${"alert".padStart(5)} ${"speak".padStart(5)} ${"triage".padStart(6)} ${"takeov".padStart(6)} ${"notify".padStart(6)} | ${"coord".padStart(12)} ${"triage".padStart(12)} ${"search".padStart(12)} ${"vision".padStart(12)} | score`);
  const sec = (v: number | null) => (v === null ? "  -  " : `${v.toFixed(0)}s`).padStart(5);
  const agent = (t: { calls: number; totalMs: number } | undefined) => (t ? `${t.calls}×${(t.totalMs / t.calls / 1000).toFixed(1)}s` : "-").padStart(12);
  for (const s of pick) {
    try {
      const r = await runScenario(s, { config, llm: "nvidia", agentMode: mode, approvals: "auto", emit: () => {} });
      const t = r.timing!;
      const m = t.milestones;
      console.log(
        `${s.id.slice(0, 26).padEnd(26)} ${`${(t.totalMs / 1000).toFixed(0)}s`.padStart(6)} | ${sec(m.firstLook)} ${sec(m.rescuerAlerted)} ${sec(m.firstWords)} ${sec(m.triage).padStart(6)} ${sec(m.takeover).padStart(6)} ${sec(m.respondersNotified).padStart(6)} | ${agent(t.llm.coordinator ?? t.llm.solo)} ${agent(t.llm.triage)} ${agent(t.llm.search)} ${(t.vision.calls ? `${t.vision.calls}×${(t.vision.avgMs / 1000).toFixed(1)}s` : "-").padStart(12)} | ${r.score ? `${r.score.passed}/${r.score.total}` : "-"}`,
      );
      const tok = Object.entries(t.llm).map(([k, v]) => `${k} ${v.reasoningTokens}/${v.completionTokens}`).join(", ");
      console.log(`${"".padEnd(26)}   reasoning/output tokens: ${tok}; other tools ${t.otherToolsMs} ms`);
    } catch (e) {
      console.log(`${s.id.padEnd(26)} ERROR ${(e as Error).message}`);
    }
  }
}

if (args.includes("--micro")) await micro();
if (args.includes("--scenes")) await scenes();
if (!args.includes("--micro") && !args.includes("--scenes")) console.log("Use --micro and/or --scenes");
