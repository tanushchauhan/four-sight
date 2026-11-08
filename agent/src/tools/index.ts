import { PAN_LIMIT, TILT_MAX, TILT_MIN } from "../adapters/robot.js";
import { ALERT_LEVELS, HAZARD_KINDS, TRIAGE_COLORS, type AlertLevel, type HazardKind, type SurvivorCase, type Triage } from "../types.js";
import { unsupportedMovement } from "../llm/vision.js";
import { ToolRegistry, type Tool, type ToolContext } from "./registry.js";

const round = (v: number, d = 2) => Number(v.toFixed(d));

async function findCase(ctx: ToolContext, id: number): Promise<SurvivorCase> {
  const c = (await ctx.state.getCases()).find((x) => x.id === id);
  if (!c) throw new Error(`no case ${id}`);
  return c;
}

function caseView(c: SurvivorCase, now = Date.now()) {
  return {
    case_id: c.id,
    x: round(c.x, 1),
    y: round(c.y, 1),
    triage: c.triage,
    status: c.status,
    description: c.description,
    detector_confidence: round(c.confidence),
    waiting_min: round((now - c.createdAt) / 60_000, 1),
  };
}

// ---------------- Read ----------------

const getRobotState: Tool = {
  name: "get_robot_state",
  description: "Robot pose (x, y meters; heading degrees, 0 = +x, CCW), camera pan/tilt, battery 0-1, and mode.",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => ctx.robot.getState(),
};

const getSensorReadings: Tool = {
  name: "get_sensor_readings",
  description:
    "Front ultrasonic distance (cm, null = clear), downward drop sensor (cm; much more than ~5 means a hole or edge ahead), and heat: a relative 0-1 body-heat cue " +
    "(higher = warmer body nearby; null = no heat sensor). Heat is supporting evidence for a person or for triage only, never proof of life or death on its own.",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => ctx.robot.getSensors(),
};

export const getCoverage: Tool = {
  name: "get_coverage",
  description: "Search progress on the 1 m grid: percent searched, the nearest unsearched cell centers to the robot, and known hazards.",
  parameters: { type: "object", properties: { limit: { type: "integer", minimum: 1, maximum: 20, description: "How many unsearched cells to list (default 5)." } } },
  run: async (a, ctx) => {
    const { area } = ctx.state;
    const cells = await ctx.state.getCoverage();
    const searched = new Set(cells.map((c) => `${c.x},${c.y}`));
    const robot = await ctx.robot.getState();
    const hazards = await ctx.state.getHazards();
    const unsearched: { x: number; y: number; dist: number }[] = [];
    let total = 0;
    for (let x = Math.floor(area.minX); x < area.maxX; x++) {
      for (let y = Math.floor(area.minY); y < area.maxY; y++) {
        total++;
        if (searched.has(`${x},${y}`)) continue;
        const cx = x + 0.5;
        const cy = y + 0.5;
        // Skip cells that sit on a known hazard.
        if (hazards.some((h) => Math.hypot(h.x - cx, h.y - cy) < 0.6)) continue;
        unsearched.push({ x: cx, y: cy, dist: round(Math.hypot(cx - robot.x, cy - robot.y), 1) });
      }
    }
    unsearched.sort((p, q) => p.dist - q.dist);
    return {
      percent_searched: total ? Math.round(((total - (total - searched.size)) / total) * 100) : 0,
      searched_cells: searched.size,
      total_cells: total,
      nearest_unsearched: unsearched.slice(0, (a.limit as number) ?? 5),
      unsearched_reachable: unsearched.length,
      /** Computed from the grid, not claimed by a model. */
      area_complete: unsearched.length === 0,
      hazards: hazards.map((h) => ({ x: h.x, y: h.y, kind: h.kind, note: h.note })),
      area,
    };
  },
};

const getCases: Tool = {
  name: "get_cases",
  description: "All survivor cases with triage, status and minutes waiting, plus the takeover queue.",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => {
    const cases = await ctx.state.getCases();
    const requests = await ctx.state.getTakeoverRequests();
    return {
      cases: cases.map((c) => caseView(c)),
      takeover_queue: requests
        .filter((r) => r.status === "waiting")
        .sort((p, q) => q.priority - p.priority)
        .map((r) => ({ request_id: r.id, case_id: r.caseId, priority: r.priority, reason: r.reason })),
    };
  },
};

const getResponders: Tool = {
  name: "get_responders",
  description: "Responder teams with position and status (available / en_route / on_scene).",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => ctx.state.getResponderTeams(),
};

// ---------------- Perception ----------------

const pointCamera: Tool = {
  name: "point_camera",
  description: `Aim the pan-tilt camera relative to the robot body. pan ${-PAN_LIMIT}..${PAN_LIMIT} (positive = left), tilt ${TILT_MIN}..${TILT_MAX} (positive = up).`,
  parameters: {
    type: "object",
    properties: {
      pan: { type: "number", minimum: -PAN_LIMIT, maximum: PAN_LIMIT },
      tilt: { type: "number", minimum: TILT_MIN, maximum: TILT_MAX },
    },
    required: ["pan"],
  },
  run: async (a, ctx) => ctx.robot.pointCamera(a.pan as number, (a.tilt as number) ?? 0),
};

const takeSnapshot: Tool = {
  name: "take_snapshot",
  description: "Capture a camera frame. Returns a snapshot_id to pass to analyze_image.",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => {
    const snap = await ctx.robot.takeSnapshot();
    ctx.snapshots.set(snap.id, snap);
    return { snapshot_id: snap.id, x: snap.x, y: snap.y, heading: snap.heading, pan: snap.pan, tilt: snap.tilt };
  },
};

const analyzeImage: Tool = {
  name: "analyze_image",
  description:
    "Run the Nemotron vision model on a snapshot. Returns person_visible (yes/no/unsure), confidence, description, injuries, hazards, suggested triage and how to get a better view.",
  parameters: {
    type: "object",
    properties: {
      snapshot_id: { type: "string" },
      question: { type: "string", description: "Optional extra question about the frame." },
    },
    required: ["snapshot_id"],
  },
  run: async (a, ctx) => {
    const snap = ctx.snapshots.get(a.snapshot_id as string);
    if (!snap) throw new Error(`unknown snapshot_id ${a.snapshot_id}. Call take_snapshot first.`);
    // Look budget per agent run, enforced in code: ambiguous or empty scenes otherwise loop for minutes.
    const looks = ctx.trace.slice(ctx.runStart ?? 0).filter((t) => t.agent === ctx.agent && t.name === "analyze_image" && t.ok).length;
    if (looks >= MAX_LOOKS) {
      throw new Error(`Look budget used (${MAX_LOOKS} looks). Decide now with what you have: set_triage (use "unknown" if you could not confirm a person).`);
    }
    const r = await ctx.vision.analyze(snap, a.question as string | undefined);
    await instantAlert(ctx, r);
    return r;
  },
};

export const MAX_LOOKS = 3;
const INSTANT_CONFIDENCE = 0.75;

/**
 * Fast path, in code: the moment vision clearly sees a real person, tell the rescuer and bump the
 * case's takeover request, without waiting for the agents to finish triage (~10 s vs ~26-53 s).
 * Fires once per case.
 */
async function instantAlert(ctx: ToolContext, r: { personVisible: string; confidence: number; description: string }): Promise<void> {
  const ev = ctx.event as { caseId?: number };
  if (r.personVisible !== "yes" || r.confidence < INSTANT_CONFIDENCE || !ev.caseId) return;
  if (ctx.trace.some((t) => t.name === "instant_alert" && (t.args as { case_id?: number }).case_id === ev.caseId)) return;
  const c = (await ctx.state.getCases()).find((x) => x.id === ev.caseId);
  if (!c) return;
  // Robots without localization (prada) only have placeholder coordinates: don't read them out.
  const where = ctx.robot.capabilities?.move === false ? "in front of the robot" : `at (${round(c.x, 1)}, ${round(c.y, 1)})`;
  await ctx.state.pushAlert(`Person seen, case ${c.id} ${where}: ${r.description.slice(0, 120)}`, "warning");
  const req = (await ctx.state.getTakeoverRequests()).find((q) => q.caseId === c.id && q.status === "waiting");
  if (req && req.priority < 80) await ctx.state.setPriority(req.id, 80, "person confirmed by camera; triage in progress");
  ctx.trace.push({ agent: "system", name: "instant_alert", args: { case_id: c.id }, ok: true, result: { alerted: true, request_id: req?.id ?? null }, ms: 0, at: Date.now() });
}

/** Simulated robots have no real clock to wait on; neither do tests. */
function waitMs(ctx: ToolContext, seconds: number): number {
  const name = ctx.robot.constructor?.name;
  if (process.env.NODE_ENV === "test" || name === "SimRobot" || name === "SceneRobot") return 0;
  return seconds * 1000;
}

const checkMovement: Tool = {
  name: "check_movement",
  description:
    "Take two camera frames a few seconds apart and ask vision whether the person moved between them (breathing, limbs, head, eyes). " +
    "Use on a visible person who does not answer, to tell unresponsive-but-breathing (red) from no signs of life. " +
    "Keep the camera and robot still. Returns movement yes/no/unsure. A two-frame check is weak evidence: never conclude death from this alone.",
  parameters: { type: "object", properties: { seconds: { type: "number", minimum: 1, maximum: 5, description: "Gap between the two frames (default 2)." } } },
  run: async (a, ctx) => {
    const seconds = (a.seconds as number) ?? 2;
    const first = await ctx.robot.takeSnapshot();
    ctx.snapshots.set(first.id, first);
    const ms = waitMs(ctx, seconds);
    if (ms > 0) await new Promise((r) => setTimeout(r, ms));
    const second = await ctx.robot.takeSnapshot();
    ctx.snapshots.set(second.id, second);
    const r = ctx.vision.compareFrames ? await ctx.vision.compareFrames(first, second) : unsupportedMovement();
    return { movement: r.movement, detail: r.detail, confidence: r.confidence, snapshot_ids: [first.id, second.id] };
  },
};

const listen: Tool = {
  name: "listen",
  description: "Record from the robot microphone and transcribe. Use after speak to hear a survivor's answer. transcript null = silence.",
  parameters: { type: "object", properties: { seconds: { type: "number", minimum: 1, maximum: 15 } } },
  run: async (a, ctx) => ctx.robot.listen((a.seconds as number) ?? 5),
};

// ---------------- Movement ----------------

const move: Tool = {
  name: "move",
  description: "Walk straight forward (positive) or backward (negative), meters. Stops early at obstacles, drops or low battery and says why.",
  parameters: { type: "object", properties: { meters: { type: "number", minimum: -3, maximum: 3 } }, required: ["meters"] },
  run: async (a, ctx) => ctx.robot.move(a.meters as number),
};

const turn: Tool = {
  name: "turn",
  description: "Turn the body in place, degrees. Positive = left (counter-clockwise).",
  parameters: { type: "object", properties: { degrees: { type: "number", minimum: -180, maximum: 180 } }, required: ["degrees"] },
  run: async (a, ctx) => ctx.robot.turn(a.degrees as number),
};

const navigateTo: Tool = {
  name: "navigate_to",
  description: "Turn toward a point and walk to it in a straight line (meters, map frame). Stops at obstacles or drops; then mark the hazard and pick a detour.",
  parameters: { type: "object", properties: { x: { type: "number" }, y: { type: "number" } }, required: ["x", "y"] },
  run: async (a, ctx) => ctx.robot.navigateTo(a.x as number, a.y as number),
};

const sweep360: Tool = {
  name: "sweep_360",
  description:
    "Stop and scan all around with the on-board person detector. Possible people become survivor cases (returned with case_id). Detector confidence below 0.7 means you should take a second look before triaging.",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => {
    const sweep = await ctx.robot.sweep360();
    const cases = await ctx.state.getCases();
    const found = [];
    for (const d of sweep.detections) {
      const existing = cases.find((c) => c.status !== "resolved" && Math.hypot(c.x - d.x, c.y - d.y) < 1.5);
      const c = existing ?? (await ctx.state.reportDetection(ctx.robotId, d.x, d.y, d.confidence, `sweep://${Date.now()}`));
      found.push({ ...d, case_id: c?.id ?? null, new_case: !existing });
    }
    return { detections: found, notes: sweep.notes, robot: sweep.state };
  },
};

const returnToStart: Tool = {
  name: "return_to_start",
  description: "Walk back to the entry point (use for low battery or lost link).",
  parameters: { type: "object", properties: {} },
  run: async (_a, ctx) => ctx.robot.returnToStart(),
};

// ---------------- Survivor ----------------

const speak: Tool = {
  name: "speak",
  description: "Say something through the robot's speaker. Keep it short and calm. Tell survivors help is coming; ask one question at a time.",
  parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
  run: async (a, ctx) => ctx.robot.speak(a.text as string),
};

const setTriage: Tool = {
  name: "set_triage",
  description:
    "Write the provisional triage card for a case (shown in the headset). red = immediate, yellow = delayed/trapped, green = minor, black = deceased, unknown = cannot tell. Base it on what you saw and heard; mention uncertainty.",
  parameters: {
    type: "object",
    properties: {
      case_id: { type: "integer", minimum: 1 },
      color: { type: "string", enum: TRIAGE_COLORS },
      description: { type: "string", description: "What was observed and why this color, 1-3 sentences." },
    },
    required: ["case_id", "color", "description"],
  },
  run: async (a, ctx) => {
    await findCase(ctx, a.case_id as number);
    // Guardrail for the triage agent: a visible person must be spoken to and listened to first.
    if (ctx.agent === "triage") {
      const mine = ctx.trace.slice(ctx.runStart ?? 0).filter((t) => t.agent === "triage");
      const sawPerson = mine.some((t) => t.name === "analyze_image" && (t.result as { personVisible?: string })?.personVisible === "yes");
      const listened = mine.some((t) => t.name === "listen" && t.ok);
      if (sawPerson && !listened) {
        throw new Error("Talk to the survivor before triaging: speak a short calm question, then listen. Responsiveness changes the color.");
      }
    }
    await ctx.state.setTriage(a.case_id as number, a.color as Triage, a.description as string);
    if (a.color === "red") await ctx.state.pushAlert(`Case ${a.case_id}: RED. ${a.description}`, "critical");
    return { ok: true };
  },
};

const dropBeacon: Tool = {
  name: "drop_beacon",
  description: "Release the locator beacon next to a survivor so responders can find them. The robot carries only one.",
  parameters: { type: "object", properties: { case_id: { type: "integer", minimum: 1 } }, required: ["case_id"] },
  confirm: async (a) => `drop the beacon at case ${a.case_id}`,
  run: async (a, ctx) => {
    await findCase(ctx, a.case_id as number);
    return ctx.robot.dropBeacon();
  },
};

// ---------------- Coordination ----------------

const requestTakeover: Tool = {
  name: "request_takeover",
  description:
    "Ask the rescuer to take manual control (puts a request in the headset queue). Use for a confirmed person, a red case, or when stuck. priority 0-100, higher = sooner. case_id 0 if not about a case.",
  parameters: {
    type: "object",
    properties: {
      case_id: { type: "integer", minimum: 0 },
      priority: { type: "integer", minimum: 0, maximum: 100 },
      reason: { type: "string" },
    },
    required: ["case_id", "priority", "reason"],
  },
  run: async (a, ctx) => {
    await ctx.state.requestTakeover(ctx.robotId, a.case_id as number, a.priority as number, a.reason as string);
    await ctx.robot.setMode("waiting");
    await ctx.state.pushAlert(`Takeover requested (priority ${a.priority}): ${a.reason}`, (a.priority as number) >= 80 ? "critical" : "warning");
    return { ok: true, robot_mode: "waiting" };
  },
};

const setPriority: Tool = {
  name: "set_priority",
  description: "Re-rank a takeover request (0-100, higher = sooner) with a one-line reason.",
  parameters: {
    type: "object",
    properties: { request_id: { type: "integer", minimum: 1 }, priority: { type: "integer", minimum: 0, maximum: 100 }, reason: { type: "string" } },
    required: ["request_id", "priority", "reason"],
  },
  run: async (a, ctx) => {
    await ctx.state.setPriority(a.request_id as number, a.priority as number, a.reason as string);
    return { ok: true };
  },
};

const markHazard: Tool = {
  name: "mark_hazard",
  description: "Record a hazard on the map (drop, obstacle, heat, other). Defaults to 0.3 m ahead of the robot.",
  parameters: {
    type: "object",
    properties: { kind: { type: "string", enum: HAZARD_KINDS }, note: { type: "string" }, x: { type: "number" }, y: { type: "number" } },
    required: ["kind", "note"],
  },
  run: async (a, ctx) => {
    let { x, y } = a as { x?: number; y?: number };
    if (x === undefined || y === undefined) {
      const s = await ctx.robot.getState();
      const rad = (s.heading * Math.PI) / 180;
      x = round(s.x + Math.cos(rad) * 0.3, 1);
      y = round(s.y + Math.sin(rad) * 0.3, 1);
    }
    await ctx.state.markHazard(x, y, a.kind as HazardKind, a.note as string);
    return { ok: true, x, y };
  },
};

const dispatchResponders: Tool = {
  name: "dispatch_responders",
  description: "Send a responder team to a case. If team_id is omitted, the nearest available team is chosen.",
  parameters: { type: "object", properties: { case_id: { type: "integer", minimum: 1 }, team_id: { type: "integer", minimum: 1 } }, required: ["case_id"] },
  confirm: async (a, ctx) => {
    const c = await findCase(ctx, a.case_id as number);
    const team = await pickTeam(ctx, c, a.team_id as number | undefined);
    a.team_id = team.id;
    return `send ${team.name} to case ${c.id} (${c.triage}) at (${round(c.x, 1)}, ${round(c.y, 1)})`;
  },
  run: async (a, ctx) => {
    const c = await findCase(ctx, a.case_id as number);
    const team = await pickTeam(ctx, c, a.team_id as number | undefined);
    await ctx.state.dispatchResponders(c.id, team.id);
    return { ok: true, team: team.name };
  },
};

async function pickTeam(ctx: ToolContext, c: SurvivorCase, teamId?: number) {
  const teams = await ctx.state.getResponderTeams();
  if (teamId) {
    const t = teams.find((x) => x.id === teamId);
    if (!t) throw new Error(`no team ${teamId}`);
    return t;
  }
  const free = teams.filter((t) => t.status === "available").sort((p, q) => Math.hypot(p.x - c.x, p.y - c.y) - Math.hypot(q.x - c.x, q.y - c.y));
  if (!free[0]) throw new Error("no responder team is available");
  return free[0];
}

const notifyResponders: Tool = {
  name: "notify_responders",
  description: "Text the responder phones (SMS) about a case: location, triage, what was seen and heard. Add a short message with anything they need to know.",
  parameters: { type: "object", properties: { case_id: { type: "integer", minimum: 1 }, message: { type: "string" } }, required: ["case_id", "message"] },
  run: async (a, ctx) => {
    const c = await findCase(ctx, a.case_id as number);
    const body = [
      `RESCUE ROBOT: case ${c.id}, triage ${c.triage.toUpperCase()} (provisional, AI-assessed)`,
      ctx.robot.capabilities?.move === false
        ? "Location: where the rescue robot is now (no map position; follow the robot / beacon)"
        : `Location: x=${round(c.x, 1)} m, y=${round(c.y, 1)} m from entry`,
      c.description ? `Seen: ${c.description}` : null,
      `Note: ${a.message}`,
    ]
      .filter(Boolean)
      .join("\n");
    const results = await ctx.notifier.send(ctx.responderPhones, body);
    ctx.lastNotify = results;
    return { sent: results.filter((r) => r.ok).length, failed: results.filter((r) => !r.ok) };
  },
};

// ---------------- Human ----------------

const pushHeadsetAlert: Tool = {
  name: "push_headset_alert",
  description: "Show (and read aloud) a short message in the rescuer's headset. Use for sitreps and anything the rescuer must know now.",
  parameters: { type: "object", properties: { text: { type: "string" }, level: { type: "string", enum: ALERT_LEVELS } }, required: ["text"] },
  run: async (a, ctx) => {
    await ctx.state.pushAlert(a.text as string, ((a.level as AlertLevel) ?? "info") as AlertLevel);
    return { ok: true };
  },
};

export const ALL_TOOLS: Tool[] = [
  getRobotState,
  getSensorReadings,
  getCoverage,
  getCases,
  getResponders,
  pointCamera,
  takeSnapshot,
  analyzeImage,
  checkMovement,
  listen,
  move,
  turn,
  navigateTo,
  sweep360,
  returnToStart,
  speak,
  setTriage,
  dropBeacon,
  requestTakeover,
  setPriority,
  markHazard,
  dispatchResponders,
  notifyResponders,
  pushHeadsetAlert,
];

export function buildRegistry(tools: Tool[] = ALL_TOOLS): ToolRegistry {
  return new ToolRegistry(tools);
}
