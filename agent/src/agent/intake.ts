import type { IncomingMessage, ServerResponse } from "node:http";
import type { StateStore } from "../adapters/state.js";
import type { AgentEvent } from "./events.js";

/**
 * Detection intake: the visor's on-phone YOLO detector says "person" → the agent opens a case and
 * runs the detection workflow (frames from the robot camera, Nemotron VL, talk, triage, alerts).
 *
 *   POST /api/detections   Authorization: Bearer <AGENT_TOKEN>
 *   {"source":"visor","confidence":0.83,"count":1,"box":[x,y,w,h]|null,"frame_time":1762600000000}
 *   → 202 {"ok":true,"case_id":3,"new_case":true}
 *   → 200 {"ok":true,"ignored":"cooldown"|"active_case"|"low_confidence","case_id":n|null}
 *   GET /api/status → {"ok":true,"active_case":n|null,"last_sighting_ms_ago":n|null}
 *
 * prada has no localization, so one sighting episode = one case: while a case is open and the person
 * keeps being seen, sightings are folded into it. Every `recheckMs` while still seen, the agent
 * re-checks it (the scene may change: deterioration, smoke). A case closes when YOLO has not seen
 * anyone for `closeAfterMs`, or the workflow judged it a false alarm.
 */
export interface IntakeOptions {
  token: string;
  admit?: (sighting: Sighting) => Promise<boolean>;
  robotId: number;
  minConfidence?: number; // default 0.5
  cooldownMs?: number; // default 30 s after a case closes as a false alarm
  closeAfterMs?: number; // default 120 s without sightings
  recheckMs?: number; // default 45 s
  maxRechecks?: number; // default 4
  now?: () => number;
}

export interface Sighting {
  sessionId?: string;
  visor?: Record<string, unknown>;
  source: string;
  confidence: number;
  count: number;
  box: number[] | null;
  frameTime: number | null;
}

export class DetectionIntake {
  private sightingChain: Promise<void> = Promise.resolve();
  private latestVisor: Record<string, unknown> | null = null;
  private active: { sessionId?: string; caseId: number; openedAt: number; lastSeen: number; lastRecheck: number; rechecks: number; maxCount: number } | null = null;
  private cooldownUntil = 0;
  private seq = 0;
  private timer: NodeJS.Timeout | null = null;

  constructor(
    private readonly state: StateStore,
    private readonly push: (e: AgentEvent) => void,
    private readonly o: IntakeOptions,
  ) {}

  private now(): number {
    return this.o.now?.() ?? Date.now();
  }

  /** Validate and act on one sighting. */
  async sight(s: Sighting): Promise<{ status: number; body: Record<string, unknown> }> {
    const result = this.sightingChain.then(() => this.applySighting(s));
    this.sightingChain = result.then(() => {}, () => {});
    return result;
  }

  private async applySighting(s: Sighting): Promise<{ status: number; body: Record<string, unknown> }> {
    this.latestVisor = s.visor ?? null;
    const now = this.now();
    await this.expire(now);
    if (s.count < 1 || s.confidence < (this.o.minConfidence ?? 0.5)) return { status: 200, body: { ok: true, ignored: "low_confidence", case_id: this.active?.caseId ?? null } };
    if (this.o.admit && !(await this.o.admit(s))) return { status: 200, body: { ok: true, ignored: "mission_disabled", case_id: null } };
    if (s.sessionId && this.active?.sessionId !== s.sessionId) {
      this.active = null;
      this.cooldownUntil = 0;
    }
    if (this.active) {
      this.active.lastSeen = now;
      // A second person appearing is worth a fresh look at the open case.
      if (!this.o.admit && s.count > this.active.maxCount) {
        this.active.maxCount = s.count;
        this.push({ kind: "recheck_case", caseId: this.active.caseId, reason: `The visor now sees ${s.count} people.` });
      }
      return { status: 200, body: { ok: true, ignored: "active_case", case_id: this.active.caseId } };
    }
    if (now < this.cooldownUntil) return { status: 200, body: { ok: true, ignored: "cooldown", case_id: null } };
    // Placeholder position: prada has no localization; unique so the store's 1.5 m de-duplication never merges cases.
    const x = ++this.seq * 3;
    const c = await this.state.reportDetection(this.o.robotId, x, 0, s.confidence, `visor://${s.source}/${s.frameTime ?? now}`);
    if (!c) return { status: 200, body: { ok: true, ignored: "active_case", case_id: null } };
    this.active = { sessionId: s.sessionId, caseId: c.id, openedAt: now, lastSeen: now, lastRecheck: now, rechecks: 0, maxCount: s.count };
    this.push({ kind: "detection", caseId: c.id, sessionId: s.sessionId, visor: s.visor });
    return { status: 202, body: { ok: true, case_id: c.id, new_case: true } };
  }

  /** Called on a timer: re-check while the person is still in view; close the case once they are gone. */
  async tick(): Promise<void> {
    const now = this.now();
    await this.expire(now);
    const a = this.active;
    if (!a || this.o.admit) return;
    const stillSeen = now - a.lastSeen < (this.o.recheckMs ?? 45_000);
    if (stillSeen && now - a.lastRecheck >= (this.o.recheckMs ?? 45_000) && a.rechecks < (this.o.maxRechecks ?? 4)) {
      a.lastRecheck = now;
      a.rechecks++;
      this.push({ kind: "recheck_case", caseId: a.caseId, reason: "Time has passed and the person is still in view." });
    }
  }

  private async expire(now: number): Promise<void> {
    const a = this.active;
    if (!a) return;
    const c = (await this.state.getCases()).find((x) => x.id === a.caseId);
    // The workflow closed it as a false alarm (no person after its looks): stop folding sightings into it.
    const falseAlarm = c && c.triage === "unknown" && /No person seen/.test(c.description);
    if (falseAlarm || now - a.lastSeen >= (this.o.closeAfterMs ?? 120_000)) {
      this.active = null;
      if (falseAlarm) this.cooldownUntil = now + (this.o.cooldownMs ?? 30_000);
    }
  }

  status(): Record<string, unknown> {
    return { ok: true, active_case: this.active?.caseId ?? null, last_sighting_ms_ago: this.active ? this.now() - this.active.lastSeen : null, visor: this.latestVisor };
  }

  start(intervalMs = 5000): void {
    this.timer ??= setInterval(() => void this.tick().catch(() => {}), intervalMs);
    this.timer.unref?.();
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  /** HTTP route for the operator feed server's `routes` hook. */
  routes = async (req: IncomingMessage, res: ServerResponse, url: URL): Promise<boolean> => {
    const json = (status: number, body: unknown) => {
      res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", "Access-Control-Allow-Origin": "*" });
      res.end(JSON.stringify(body));
    };
    if (url.pathname === "/api/status" && req.method === "GET") {
      json(200, this.status());
      return true;
    }
    if (url.pathname !== "/api/detections") return false;
    if (req.method !== "POST") {
      json(405, { ok: false, error: "use POST" });
      return true;
    }
    let body: Record<string, unknown>;
    try {
      const chunks: Buffer[] = [];
      let size = 0;
      for await (const c of req) {
        size += (c as Buffer).length;
        if (size > 16384) throw new Error("body too large");
        chunks.push(c as Buffer);
      }
      body = JSON.parse(Buffer.concat(chunks).toString("utf8")) as Record<string, unknown>;
    } catch (e) {
      json(400, { ok: false, error: (e as Error).message });
      return true;
    }
    const conf = Number(body.confidence);
    const count = Number(body.count ?? 1);
    if (!Number.isFinite(conf) || conf < 0 || conf > 1 || !Number.isInteger(count) || count < 0 || count > 50) {
      json(400, { ok: false, error: "confidence must be 0..1 and count an integer 0..50" });
      return true;
    }
    const box = Array.isArray(body.box) && body.box.length === 4 && body.box.every((v) => typeof v === "number") ? (body.box as number[]) : null;
    const survivors = Array.isArray(body.survivors) ? body.survivors.slice(0, 12).filter((s: any) =>
      s && typeof s.confidence === "number" && s.confidence >= 0 && s.confidence <= 1 &&
      Array.isArray(s.box) && s.box.length === 4 && s.box.every((v: unknown) => typeof v === "number" && Number.isFinite(v) && v >= 0 && v <= 1) &&
      typeof s.maskCoverage === "number" && s.maskCoverage >= 0 && s.maskCoverage <= 1
    ).map((s: any) => ({ confidence: s.confidence, box: s.box, mask_coverage: s.maskCoverage })) : [];
    const finite = (value: unknown, min: number, max: number) => typeof value === "number" && Number.isFinite(value) && value >= min && value <= max ? value : null;
    const visor = { survivors, count, confidence: conf, received_at: this.now(),
      motion: ["moving", "stationary", "uncertain"].includes(String(body.motion)) ? body.motion : "uncertain",
      distance_estimate_m: finite(body.distance_estimate_m, 0, 100),
      bearing_estimate_deg: finite(body.bearing_estimate_deg, -180, 180),
      limitations: "Monocular range and image motion; camera movement can mimic survivor movement. Not medical triage." };
    const r = await this.sight({ source: String(body.source ?? "visor").slice(0, 20), confidence: conf, count, box, visor, frameTime: Number.isFinite(Number(body.frame_time)) ? Number(body.frame_time) : null });
    json(r.status, r.body);
    return true;
  };
}
