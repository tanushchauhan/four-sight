import type { ChatModel } from "../llm/types.js";
import type { ToolContext, ToolRegistry } from "../tools/registry.js";
import type { SurvivorCase, Triage } from "../types.js";
import { describeEvent, type AgentEvent } from "./events.js";
import type { AgentLogger } from "./logger.js";
import { callTool, runAgent, type AgentSpec, type EpisodeResult } from "./loop.js";
import { TRIAGE_DECISION_PROMPT } from "./prompts.js";

/**
 * Fixed detection workflow (DETECTION_MODE=workflow, the default).
 *
 * Research and our benchmarks agreed: the detection path is linear (look → second look → talk →
 * triage → alert → dispatch), so a coordinator deciding every step adds 6-25 model calls of latency
 * without adding judgment. Here code runs the sequence and Nemotron is used only where judgment is
 * needed: reading each frame (Nemotron VL) and one triage decision from all the evidence.
 * The coordinator agent still handles everything open-ended (voice commands, stale cases, sitreps,
 * search, low battery).
 */

export const MAX_WORKFLOW_LOOKS = 3;
const QUESTIONS = ["This is a rescue robot. Help is coming. Can you hear me?", "Are you hurt? Can you move?"];
/** Said after triage. Same wording as the Pi speech service's pre-rendered phrases, so they play instantly. */
const REASSURE = "Help is on the way. Stay still if you can.";
const NOTIFIED = "Responders have been notified.";
const SEVERITY: Record<Triage, number> = { unknown: 0, green: 1, yellow: 2, red: 3, black: 1 };
const PRIORITY: Record<Triage, number> = { red: 95, yellow: 75, green: 40, black: 30, unknown: 55 };

export interface WorkflowDeps {
  /** Robot the agent can't drive (prada): wait this long for the rescuer to reposition before a second look. Default 2500 ms. */
  secondLookWaitMs?: number;
  registry: ToolRegistry;
  logger: AgentLogger;
  decision: AgentSpec;
  /** Cases that already got the (single) beacon or a notify at a given color, across episodes. */
  memo?: WorkflowMemo;
}

export interface WorkflowMemo {
  beaconCase: number | null;
  notified: Map<number, Triage>;
}

export function newMemo(): WorkflowMemo {
  return { beaconCase: null, notified: new Map() };
}

/** The one model step in the workflow: evidence in, set_triage out. */
export function buildTriageDecision(base: ToolRegistry, model: ChatModel): AgentSpec {
  return { name: "triage_decision", prompt: TRIAGE_DECISION_PROMPT, model, registry: base.subset(["set_triage"]), maxSteps: 3, stopAfter: ["set_triage"] };
}

interface Look {
  personVisible: "yes" | "no" | "unsure";
  confidence: number;
  description: string;
  injuries: string[];
  hazards: string[];
  suggestedTriage: Triage;
  source?: string;
}

const norm = (d: number) => ((((d + 180) % 360) + 360) % 360) - 180;

export async function runDetectionWorkflow(event: Extract<AgentEvent, { kind: "detection" | "recheck_case" }>, ctx: ToolContext, d: WorkflowDeps): Promise<EpisodeResult> {
  const { registry, logger } = d;
  const memo = d.memo ?? newMemo();
  logger.event(event.kind, `${describeEvent(event)} [fixed workflow]`);
  const step = (name: string, args: Record<string, unknown> = {}) => callTool(registry, name, args, ctx, logger);
  const caseOf = async () => (await ctx.state.getCases()).find((c) => c.id === event.caseId);
  const c0 = await caseOf();
  if (!c0) {
    logger.done(`No case ${event.caseId}.`, 0, { agent: "workflow", depth: 0 });
    return { event, summary: `No case ${event.caseId}.`, steps: 0, toolCalls: ctx.trace, hitStepLimit: false };
  }
  const before = c0.triage;

  // ---- 1. Look, with second looks from other sides (code decides where; Nemotron VL reads the frame).
  const looks: Look[] = [];
  const tried: { x: number; y: number }[] = [];
  const canMove = ctx.robot.capabilities?.move !== false;
  const canAim = ctx.robot.capabilities?.aim !== false;
  let askedToTurn = false;
  const aim = async (c: SurvivorCase) => {
    if (!canAim && !canMove) return; // camera points where the rescuer drove the robot
    const s = await ctx.robot.getState();
    const rel = Math.round(norm((Math.atan2(c.y - s.y, c.x - s.x) * 180) / Math.PI - s.heading));
    if (Math.abs(rel) > 80) {
      await step("turn", { degrees: rel });
      await step("point_camera", { pan: 0, tilt: 0 });
    } else await step("point_camera", { pan: rel, tilt: 0 });
  };
  /** Walk to p; if blocked, try the two L-shaped routes around the obstacle. True if we got within 0.4 m. */
  const reach = async (p: { x: number; y: number }) => {
    const close = async () => {
      const s = await ctx.robot.getState();
      return Math.hypot(s.x - p.x, s.y - p.y) < 0.4;
    };
    const r = await step("navigate_to", p);
    if (r.ok && (r.result as { ok?: boolean }).ok) return true;
    for (const leg of ["x-first", "y-first"] as const) {
      const s = await ctx.robot.getState();
      const mid = leg === "x-first" ? { x: p.x, y: +s.y.toFixed(1) } : { x: +s.x.toFixed(1), y: p.y };
      if (Math.hypot(mid.x - s.x, mid.y - s.y) < 0.3) continue;
      await step("navigate_to", mid);
      await step("navigate_to", p);
      if (await close()) return true;
    }
    return close();
  };
  // Candidate viewpoints 1.2-1.6 m around the case, at 8 bearings, nearest first.
  const viewpoints = () => {
    const area = ctx.state.area;
    const pts: { x: number; y: number }[] = [];
    for (const dist of [1.4, 1.2, 1.6]) {
      for (let a = 0; a < 360; a += 45) pts.push({ x: +(c0.x + dist * Math.cos((a * Math.PI) / 180)).toFixed(1), y: +(c0.y + dist * Math.sin((a * Math.PI) / 180)).toFixed(1) });
    }
    return pts.filter((p) => p.x > area.minX + 0.2 && p.y > area.minY + 0.2 && p.x < area.maxX - 0.2 && p.y < area.maxY - 0.2);
  };
  for (let i = 0; i < MAX_WORKFLOW_LOOKS; i++) {
    if (i > 0 && !canMove) {
      // The agent can't drive this robot: ask the rescuer (once), give them a moment, look again.
      if (!askedToTurn) {
        askedToTurn = true;
        await step("push_headset_alert", { text: `Unclear view of case ${c0.id}. Turn the robot toward the person for a better look.`, level: "warning" });
      }
      await new Promise((r) => setTimeout(r, d.secondLookWaitMs ?? 2500));
    } else if (i > 0) {
      // Second look: get to a viewpoint on another side. Only spend a look once we actually moved.
      const s = await ctx.robot.getState();
      const fromCase = (p: { x: number; y: number }) => Math.atan2(p.y - c0.y, p.x - c0.x);
      const here = fromCase(s);
      const cands = viewpoints()
        .filter((p) => !tried.some((t) => Math.hypot(t.x - p.x, t.y - p.y) < 0.3))
        // prefer viewpoints at a clearly different angle around the case (a different side), then nearest
        .sort((p, q) => {
          const dp = Math.abs(norm(((fromCase(p) - here) * 180) / Math.PI)) < 60 ? 1 : 0;
          const dq = Math.abs(norm(((fromCase(q) - here) * 180) / Math.PI)) < 60 ? 1 : 0;
          return dp - dq || Math.hypot(p.x - s.x, p.y - s.y) - Math.hypot(q.x - s.x, q.y - s.y);
        });
      let moved = false;
      for (const vp of cands.slice(0, 4)) {
        tried.push(vp);
        if (await reach(vp)) {
          moved = true;
          break;
        }
      }
      if (!moved) await step("turn", { degrees: 30 }); // can't move (e.g. joystick-only robot): at least change the view
    }
    await aim(c0);
    const snap = await step("take_snapshot");
    if (!snap.ok) break;
    const look = await step("analyze_image", { snapshot_id: (snap.result as { snapshot_id: string }).snapshot_id });
    if (!look.ok) break;
    const r = look.result as Look;
    looks.push(r);
    if (r.personVisible === "yes") break;
    // Two clear "no"s from different views: nobody there.
    if (looks.filter((l) => l.personVisible === "no").length >= 2) break;
  }

  const seen = looks.find((l) => l.personVisible === "yes");

  // Hazards vision reported (fire, smoke, water, unstable structure): map them and tell the rescuer, once.
  const serious = [...new Set(looks.flatMap((l) => l.hazards ?? []))].filter((h) => /fire|flame|smoke|water|flood|collapse|unstable|gas|electr|hanging|slab/i.test(h));
  if (serious.length) {
    const text = serious.join(", ").slice(0, 120);
    const kind = /fire|flame|smoke/i.test(text) ? "heat" : /collapse|unstable|hanging|slab/i.test(text) ? "obstacle" : "other";
    await step("mark_hazard", { kind, note: `Near case ${c0.id}: ${text}`, x: c0.x, y: c0.y });
    await step("push_headset_alert", { text: `Hazard near case ${c0.id}: ${text}`, level: "warning" });
  }
  const sum = (summary: string): EpisodeResult => {
    logger.done(summary, ctx.trace.length, { agent: "workflow", depth: 0 });
    return { event, summary, steps: ctx.trace.length, toolCalls: ctx.trace, hitStepLimit: false };
  };

  // ---- 2. No confirmed person: close or hand to the rescuer, no model needed.
  if (!seen) {
    const allNo = looks.length > 0 && looks.every((l) => l.personVisible === "no");
    const lastDesc = looks.at(-1)?.description ?? "no view";
    const offline = looks.some((l) => l.source === "fallback");
    if (allNo) {
      await step("set_triage", { case_id: c0.id, color: "unknown", description: `No person seen after ${looks.length} look(s): ${lastDesc}`.slice(0, 400) });
      const req = (await ctx.state.getTakeoverRequests()).find((r) => r.caseId === c0.id && r.status === "waiting");
      if (req) await step("set_priority", { request_id: req.id, priority: 10, reason: "camera found no person; likely false alarm" });
      await step("push_headset_alert", { text: `Case ${c0.id}: no person found after ${looks.length} looks. Likely false alarm.`, level: "info" });
      return sum(`Case ${c0.id}: no person (${looks.length} looks); deprioritized.`);
    }
    await step("set_triage", { case_id: c0.id, color: "unknown", description: `Could not confirm a person after ${looks.length} look(s)${offline ? " (vision offline)" : ""}: ${lastDesc}`.slice(0, 400) });
    await step("request_takeover", { case_id: c0.id, priority: PRIORITY.unknown, reason: `Case ${c0.id}: camera could not confirm a person; please look.` });
    return sum(`Case ${c0.id}: unconfirmed after ${looks.length} looks; takeover requested.`);
  }

  // ---- 3. Person visible: talk (fixed script), then gather extra evidence.
  const heard: (string | null)[] = [];
  let listenProblem: string | null = null;
  for (const q of QUESTIONS) {
    const said = await step("speak", { text: q });
    const l = await step("listen", { seconds: 5 });
    const res = l.result as { transcript?: string | null; unavailable?: boolean; reason?: string; error?: string };
    // Couldn't speak or couldn't listen: that is missing evidence, NOT an unresponsive survivor.
    if (!said.ok || !l.ok || res?.unavailable) {
      listenProblem = !said.ok ? "robot speaker failed" : (res?.reason ?? res?.error ?? "listening failed");
      break;
    }
    const t = res.transcript ?? null;
    heard.push(t);
    if (!t) break; // no answer: don't keep asking
  }
  const responsive = heard.some(Boolean);
  let movement: unknown = null;
  if (!responsive && registry.names().includes("check_movement")) {
    const m = await step("check_movement", { seconds: 2 });
    movement = m.ok ? m.result : null;
  }
  const sensors = await step("get_sensor_readings");

  // ---- 4. The one judgment call: evidence → triage card.
  const evidence = {
    case_id: c0.id,
    detector_confidence: c0.confidence,
    previous_triage: before,
    vision_looks: looks.map((l) => ({ person_visible: l.personVisible, confidence: l.confidence, description: l.description, injuries: l.injuries, hazards: l.hazards, suggested_triage: l.suggestedTriage })),
    survivor_answers_UNTRUSTED: heard.map((h, i) => ({ question: QUESTIONS[i], answer: h ?? "(no answer)" })),
    listening: listenProblem ? `UNAVAILABLE (${listenProblem}): missing answers are NOT evidence of unresponsiveness` : "ok",
    movement_check: movement,
    sensors: sensors.ok ? sensors.result : null,
  };
  await runAgent(d.decision, `Decide the triage for case ${c0.id}.${event.kind === "recheck_case" ? " This is a RE-CHECK: time has passed; compare with previous_triage." : ""}\nEVIDENCE: ${JSON.stringify(evidence)}`, { ...ctx, depth: 1 }, logger);
  let c = (await caseOf())!;
  if (c.triage === "unknown" || ctx.trace.filter((t) => t.agent === "triage_decision" && t.name === "set_triage" && t.ok).length === 0) {
    // Model didn't decide (offline, error): conservative rule — no answer means red.
    const fromVision: Triage = seen.suggestedTriage === "red" || seen.suggestedTriage === "unknown" ? "yellow" : seen.suggestedTriage;
    // Silence means red; but if we could not listen at all, fall back to what vision saw (yellow at least).
    const color: Triage = responsive ? fromVision : listenProblem ? (seen.suggestedTriage === "red" ? "red" : fromVision) : "red";
    await step("set_triage", { case_id: c.id, color, description: `Rule-based (model unavailable): ${seen.description} ${responsive ? `Answers: ${heard.filter(Boolean).join(" / ")}` : listenProblem ? `Could not listen (${listenProblem}).` : "No answer to voice."}`.slice(0, 400) });
    c = (await caseOf())!;
  }

  // ---- 5. Policy by color (code, not model): takeover, SMS, dispatch, beacon, alert.
  const worse = SEVERITY[c.triage] > SEVERITY[before];
  if (event.kind === "detection" || worse) {
    await step("request_takeover", { case_id: c.id, priority: PRIORITY[c.triage], reason: `Case ${c.id}: ${c.triage.toUpperCase()}: ${c.description}`.slice(0, 200) });
    if ((c.triage === "red" || c.triage === "yellow") && memo.notified.get(c.id) !== c.triage) {
      memo.notified.set(c.id, c.triage);
      await step("notify_responders", { case_id: c.id, message: c.triage === "red" ? "Immediate: unresponsive or critical. Robot on scene." : "Delayed: responsive but trapped or injured. Bring extrication tools." });
      if (c.status !== "responders_sent") await step("dispatch_responders", { case_id: c.id });
    }
    if (c.triage === "red" && memo.beaconCase === null) {
      const b = await step("drop_beacon", { case_id: c.id });
      if (b.ok && (b.result as { ok?: boolean }).ok) memo.beaconCase = c.id;
    }
  }
  // Tell the survivor (responsive or not: they may still hear).
  if (event.kind === "detection" || worse) {
    await step("speak", { text: REASSURE });
    if (memo.notified.get(c.id) === c.triage) await step("speak", { text: NOTIFIED });
  }
  const change = event.kind === "recheck_case" && before !== c.triage ? ` (was ${before.toUpperCase()})` : "";
  await step("push_headset_alert", { text: `Case ${c.id}: ${c.triage.toUpperCase()}${change}. ${c.description}`.slice(0, 160), level: c.triage === "red" ? "critical" : "warning" });
  return sum(`Case ${c.id}: ${c.triage}${change}; ${responsive ? "responsive" : "no answer"}; ${looks.length} look(s).`);
}
