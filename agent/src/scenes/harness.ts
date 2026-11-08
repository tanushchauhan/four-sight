import { MemoryState } from "../adapters/memory-state.js";
import { ConsoleNotifier } from "../adapters/notifier.js";
import type { AgentEvent } from "../agent/events.js";
import { AgentLogger } from "../agent/logger.js";
import { runEpisode, type LlmTiming, type TraceEntry } from "../agent/loop.js";
import { buildSolo, buildTeam } from "../agent/team.js";
import { buildTriageDecision, newMemo, runDetectionWorkflow } from "../agent/workflow.js";
import type { Config } from "../config.js";
import { Health, ResilientChat, ResilientVision } from "../llm/fallback.js";
import { MockPlanner } from "../llm/mock-planner.js";
import { NemotronChat } from "../llm/nemotron.js";
import type { ChatModel } from "../llm/types.js";
import { NemotronVision, SimVision, type VisionAnalyzer } from "../llm/vision.js";
import { buildRegistry } from "../tools/index.js";
import type { ToolContext } from "../tools/registry.js";
import type { Snapshot, Triage } from "../types.js";
import { SceneRobot, type FrameChange } from "./scene-robot.js";
import type { Scene } from "./types.js";

export type RunEvent =
  | { type: "status"; data: { phase: "starting" | "running" | "done" | "error"; message?: string } }
  | { type: "log"; data: Record<string, unknown> }
  | { type: "frame"; data: FrameChange }
  | { type: "speech"; data: { who: "robot" | "survivor"; text: string | null } }
  | { type: "confirm"; data: { id: number; action: string; summary: string } }
  | { type: "state"; data: unknown }
  | { type: "result"; data: RunResult }
  | { type: "error"; data: { message: string } };

export interface Score {
  personPresent?: { expected: boolean | "unclear"; actual: boolean | "unclear"; pass: boolean };
  triage: { expected: Triage; actual: Triage; pass: boolean };
  mustDo: { tool: string; pass: boolean }[];
  mustNotDo: { tool: string; pass: boolean }[];
  passed: number;
  total: number;
}

export interface Timing {
  totalMs: number;
  /** Seconds from the detection to each milestone (null = never happened). */
  milestones: Record<"firstLook" | "rescuerAlerted" | "firstWords" | "triage" | "takeover" | "respondersNotified", number | null>;
  /** Model calls per agent: count, total/avg/max ms, hidden reasoning tokens. */
  llm: Record<string, { calls: number; totalMs: number; avgMs: number; maxMs: number; reasoningTokens: number; completionTokens: number }>;
  /** Nemotron VL time = analyze_image tool time. */
  vision: { calls: number; totalMs: number; avgMs: number; maxMs: number };
  /** Everything that is not a model/vision call (sim robot, state, SMS). */
  otherToolsMs: number;
}

export interface RunResult {
  summary: string;
  timing?: Timing;
  cases: unknown[];
  actions: string[];
  score: Score | null;
  toolCalls: number;
  durationMs: number;
  frameIndex: number;
}

export interface RunOptions {
  config: Config;
  llm: "nvidia" | "mock";
  agentMode: "team" | "single";
  /** Default: config.detectionMode. */
  detectionMode?: "workflow" | "agent";
  approvals: "auto" | "ask";
  scenesDir?: string;
  emit: (e: RunEvent) => void;
  /** Lets the caller resolve "ask" approvals (the web server wires POST /confirm to this). */
  onState?: (state: MemoryState) => void;
}

/** MemoryState that reports confirmation requests so the UI can show Approve / Deny. */
class ObservedState extends MemoryState {
  constructor(
    private readonly onConfirm: (c: { id: number; action: string; summary: string }) => void,
    autoConfirm: "approve" | "none",
  ) {
    super({ autoConfirm });
  }
  override async requestConfirmation(action: string, detailsJson: string): Promise<number> {
    const id = await super.requestConfirmation(action, detailsJson);
    let summary = action;
    try {
      summary = (JSON.parse(detailsJson) as { summary?: string }).summary ?? action;
    } catch {
      // keep action name
    }
    if ((await this.getConfirmation(id))?.status === "pending") this.onConfirm({ id, action, summary });
    return id;
  }
}

/** Pixels → Nemotron VL when the key is there and the LLM is real; ground-truth captions for offline mock runs. */
class SceneVision implements VisionAnalyzer {
  private readonly sim = new SimVision();
  constructor(private readonly real: VisionAnalyzer | null) {}
  analyze(s: Snapshot, q?: string) {
    return this.real ? this.real.analyze(s, q) : this.sim.analyze(s);
  }
  compareFrames(a: Snapshot, b: Snapshot) {
    return this.real?.compareFrames ? this.real.compareFrames(a, b) : this.sim.compareFrames(a, b);
  }
}

export function scoreRun(scene: Scene, trace: TraceEntry[], finalTriage: Triage): Score | null {
  const exp = scene.expected;
  if (!exp) return null;
  const okCalls = new Set(trace.filter((t) => t.ok).map((t) => t.name));
  const looks = trace.filter((t) => t.name === "analyze_image" && t.ok).map((t) => (t.result as { personVisible?: string }).personVisible);
  const actualPresent: boolean | "unclear" = looks.includes("yes") ? true : looks.includes("unsure") ? "unclear" : false;
  const triagePass = exp.triage === finalTriage;
  const presentPass = exp.personPresent === "unclear" ? true : exp.personPresent === (actualPresent === true);
  const mustDo = exp.mustDo.map((tool) => ({ tool, pass: okCalls.has(tool) }));
  const mustNotDo = exp.mustNotDo.map((tool) => ({ tool, pass: !okCalls.has(tool) }));
  const checks = [triagePass, presentPass, ...mustDo.map((m) => m.pass), ...mustNotDo.map((m) => m.pass)];
  return {
    personPresent: { expected: exp.personPresent, actual: actualPresent, pass: presentPass },
    triage: { expected: exp.triage, actual: finalTriage, pass: triagePass },
    mustDo,
    mustNotDo,
    passed: checks.filter(Boolean).length,
    total: checks.length,
  };
}

/**
 * Run one scene: the robot's on-board detector flags a possible person, the agents
 * look at the scene's images (real pixels go to Nemotron VL), talk, triage and act.
 * Scenes with "time" frames get a re-check event each time the clock advances.
 */
export async function runScenario(scene: Scene, o: RunOptions): Promise<RunResult> {
  const t0 = Date.now();
  const { config } = o;
  if (o.llm === "nvidia" && !config.nvidiaApiKey) throw new Error("NVIDIA_API_KEY is not set (agent/.env). Use the offline mock instead.");

  const state = new ObservedState((c) => o.emit({ type: "confirm", data: c }), o.approvals === "ask" ? "none" : "approve");
  o.onState?.(state);
  const sms: { to: string[]; body: string }[] = [];
  const notifier = new ConsoleNotifier(() => {});
  const origSend = notifier.send.bind(notifier);
  notifier.send = async (to, body) => {
    sms.push({ to: to.length ? to : ["(no RESPONDER_PHONES set)"], body });
    return origSend(to, body);
  };

  const robot = new SceneRobot({
    scene,
    scenesDir: o.scenesDir,
    onFrame: (f) => o.emit({ type: "frame", data: f }),
    onSpeech: (who, text) => o.emit({ type: "speech", data: { who, text } }),
  });
  // Keep the coverage grid fed so get_coverage works.
  const inner = robot.inner;
  const s0 = await inner.getState();
  await state.robotCheckin(s0, 1);

  const emitState = async () => {
    const s = await robot.getState();
    o.emit({
      type: "state",
      data: {
        cases: (await state.getCases()).map((c) => ({ id: c.id, triage: c.triage, status: c.status, description: c.description, x: c.x, y: c.y })),
        alerts: (await state.getAlerts()).map((a) => ({ text: a.text, level: a.level })),
        sms,
        hazards: (await state.getHazards()).map((h) => ({ kind: h.kind, note: h.note })),
        robot: { x: s.x, y: s.y, heading: s.heading, battery: s.battery, mode: s.mode },
      },
    });
  };

  const logger = new AgentLogger({
    state,
    dir: null,
    print: () => {},
    color: false,
    onRecord: (rec) => {
      o.emit({ type: "log", data: rec });
      if (rec.type === "tool") void emitState();
    },
  });

  const mock = new MockPlanner();
  const health = new Health();
  health.onChange((mode, source, reason) => o.emit({ type: "status", data: { phase: "running", message: `Nemotron ${mode} (${source}): ${reason}` } }));
  const clients = new Map<string, ChatModel>();
  const modelFor = (id: string): ChatModel => {
    if (o.llm !== "nvidia") return mock;
    let m = clients.get(id);
    if (!m) clients.set(id, (m = new ResilientChat(new NemotronChat({ apiKey: config.nvidiaApiKey, baseUrl: config.nvidiaBaseUrl, model: id, toolCallMode: config.toolCallMode }), mock, { health, healthSource: `chat:${id}` })));
    return m;
  };
  const base = buildRegistry();
  const team =
    o.agentMode === "single"
      ? buildSolo(base, modelFor(config.reasoningModel), config.maxSteps)
      : buildTeam(base, { coordinator: modelFor(config.coordinatorModel), triage: modelFor(config.triageModel), search: modelFor(config.searchModel) }, logger, {
          lead: config.maxSteps,
          member: config.subMaxSteps,
        });
  const vision = new SceneVision(o.llm === "nvidia" ? new ResilientVision(new NemotronVision(config.nvidiaApiKey, config.nvidiaBaseUrl, config.visionModel, config.visionThinking), { health }) : null);

  const trace: TraceEntry[] = [];
  const llmTimings: LlmTiming[] = [];
  const snapshots = new Map<string, Snapshot>();
  const ctxFor = (event: AgentEvent): ToolContext => ({
    robot,
    state,
    vision,
    notifier,
    robotId: config.robotId,
    responderPhones: config.responderPhones,
    confirmTimeoutMs: o.approvals === "ask" ? 120_000 : config.confirmTimeoutMs,
    snapshots,
    event,
    trace,
    llmTimings,
  });

  o.emit({ type: "status", data: { phase: "running", message: `${scene.title} · ${o.detectionMode ?? config.detectionMode} · ${o.agentMode} · ${o.llm}` } });
  const tStart = Date.now();
  robot.start();

  // The on-board detector fired: that is what wakes the agents.
  const c = await state.reportDetection(config.robotId, robot.casePos.x, robot.casePos.y, scene.detectorConfidence, `scene://${scene.id}`);
  await emitState();
  const mode = o.detectionMode ?? config.detectionMode;
  const wf = { registry: base, logger, decision: buildTriageDecision(base, modelFor(config.triageModel)), memo: newMemo() };
  const run = (ev: Extract<AgentEvent, { kind: "detection" | "recheck_case" }>) =>
    mode === "workflow" ? runDetectionWorkflow(ev, ctxFor(ev), wf) : runEpisode(ev, team.lead, ctxFor(ev), logger);
  let summary = (await run({ kind: "detection", caseId: c!.id })).summary;

  // Evolving scenes: advance the clock and ask for a re-check each time.
  while (robot.hasTimeFrames()) {
    robot.advanceTime();
    summary = (await run({ kind: "recheck_case", caseId: c!.id, reason: "A few minutes have passed since the last look." })).summary;
  }

  await emitState();
  const finalCase = (await state.getCases()).find((x) => x.id === c!.id)!;
  const result: RunResult = {
    summary,
    cases: (await state.getCases()).map((x) => ({ id: x.id, triage: x.triage, status: x.status, description: x.description })),
    actions: [...new Set(trace.filter((t) => t.ok).map((t) => t.name))],
    score: scoreRun(scene, trace, finalCase.triage),
    toolCalls: trace.length,
    durationMs: Date.now() - t0,
    timing: timingReport(trace, llmTimings, tStart),
    frameIndex: robot.frameIndex,
  };
  o.emit({ type: "result", data: result });
  o.emit({ type: "status", data: { phase: "done" } });
  return result;
}

/** Where the time went in one run. */
export function timingReport(trace: TraceEntry[], llm: LlmTiming[], tStart: number): Timing {
  const first = (pred: (t: TraceEntry) => boolean) => {
    const t = trace.find((x) => x.ok && pred(x));
    return t?.at ? Number(((t.at - tStart) / 1000).toFixed(1)) : null;
  };
  const perAgent: Timing["llm"] = {};
  for (const t of llm) {
    const a = (perAgent[t.agent] ??= { calls: 0, totalMs: 0, avgMs: 0, maxMs: 0, reasoningTokens: 0, completionTokens: 0 });
    a.calls++;
    a.totalMs += t.ms;
    a.maxMs = Math.max(a.maxMs, t.ms);
    a.reasoningTokens += t.reasoningTokens ?? 0;
    a.completionTokens += t.completionTokens ?? 0;
  }
  for (const a of Object.values(perAgent)) a.avgMs = Math.round(a.totalMs / a.calls);
  const vis = trace.filter((t) => t.name === "analyze_image").map((t) => t.ms ?? 0);
  const otherToolsMs = trace.filter((t) => t.name !== "analyze_image" && !t.name.startsWith("delegate_")).reduce((n, t) => n + (t.ms ?? 0), 0);
  return {
    totalMs: Date.now() - tStart,
    milestones: {
      firstLook: first((t) => t.name === "analyze_image"),
      rescuerAlerted: first((t) => ["instant_alert", "push_headset_alert", "request_takeover"].includes(t.name)),
      firstWords: first((t) => t.name === "speak"),
      triage: first((t) => t.name === "set_triage"),
      takeover: first((t) => t.name === "request_takeover"),
      respondersNotified: first((t) => t.name === "notify_responders"),
    },
    llm: perAgent,
    vision: { calls: vis.length, totalMs: vis.reduce((a, b) => a + b, 0), avgMs: vis.length ? Math.round(vis.reduce((a, b) => a + b, 0) / vis.length) : 0, maxMs: vis.length ? Math.max(...vis) : 0 },
    otherToolsMs,
  };
}
