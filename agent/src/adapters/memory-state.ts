import type {
  AgentLogEntry,
  Alert,
  AlertLevel,
  Confirmation,
  CoverageCell,
  Hazard,
  HazardKind,
  ResponderTeam,
  RobotState,
  SearchArea,
  SurvivorCase,
  TakeoverRequest,
  Triage,
} from "../types.js";
import { StateError, type StateStore } from "./state.js";

export interface MemoryStateOptions {
  area?: SearchArea;
  /** How unanswered confirmations resolve: approve/deny right away, or never (they time out). */
  autoConfirm?: "approve" | "deny" | "none";
  teams?: ResponderTeam[];
}

const DUPLICATE_RADIUS_M = 1.5;

/** In-process implementation of the SpacetimeDB module's rules. Same semantics, no network. */
export class MemoryState implements StateStore {
  readonly area: SearchArea;
  private autoConfirm: "approve" | "deny" | "none";
  readonly robots = new Map<number, RobotState>();
  readonly coverage = new Map<string, CoverageCell>();
  readonly cases: SurvivorCase[] = [];
  readonly requests: TakeoverRequest[] = [];
  readonly teams: ResponderTeam[];
  readonly hazards: Hazard[] = [];
  readonly alerts: Alert[] = [];
  readonly confirmations: Confirmation[] = [];
  readonly agentLog: AgentLogEntry[] = [];
  private ids = { case: 0, request: 0, hazard: 0, alert: 0, confirmation: 0 };

  constructor(opts: MemoryStateOptions = {}) {
    this.area = opts.area ?? { minX: 0, minY: 0, maxX: 8, maxY: 6 };
    this.autoConfirm = opts.autoConfirm ?? "approve";
    this.teams = opts.teams ?? [
      { id: 1, name: "Engine 12", x: -5, y: 0, status: "available" },
      { id: 2, name: "Medic 4", x: -8, y: 3, status: "available" },
    ];
  }

  setAutoConfirm(mode: "approve" | "deny" | "none"): void {
    this.autoConfirm = mode;
  }

  /** Headset side: approve or deny a pending confirmation. */
  resolveConfirmation(id: number, approved: boolean): void {
    const c = this.confirmations.find((x) => x.id === id);
    if (!c) throw new StateError(`no confirmation ${id}`);
    if (c.status === "pending") c.status = approved ? "approved" : "denied";
  }

  async robotCheckin(s: RobotState, sightRadius = 1): Promise<void> {
    this.robots.set(s.id, { ...s, lastSeen: Date.now() });
    const r = Math.ceil(sightRadius);
    for (let dx = -r; dx <= r; dx++) {
      for (let dy = -r; dy <= r; dy++) {
        const cx = Math.floor(s.x) + dx;
        const cy = Math.floor(s.y) + dy;
        if (cx < this.area.minX || cy < this.area.minY || cx >= this.area.maxX || cy >= this.area.maxY) continue;
        // Cell center within sight radius
        if (Math.hypot(cx + 0.5 - s.x, cy + 0.5 - s.y) > sightRadius + 0.5) continue;
        const key = `${cx},${cy}`;
        if (!this.coverage.has(key)) this.coverage.set(key, { x: cx, y: cy, searchedBy: s.id, searchedAt: Date.now() });
      }
    }
  }

  async getRobots(): Promise<RobotState[]> {
    return [...this.robots.values()];
  }

  async getCoverage(): Promise<CoverageCell[]> {
    return [...this.coverage.values()];
  }

  async reportDetection(robotId: number, x: number, y: number, confidence: number, snapshotUrl: string): Promise<SurvivorCase | null> {
    const dup = this.cases.find((c) => c.status !== "resolved" && Math.hypot(c.x - x, c.y - y) < DUPLICATE_RADIUS_M);
    if (dup) return null;
    const now = Date.now();
    const c: SurvivorCase = {
      id: ++this.ids.case,
      robotId,
      x,
      y,
      confidence,
      snapshotUrl,
      triage: "unknown",
      description: "",
      status: "found",
      responderTeamId: 0,
      createdAt: now,
      updatedAt: now,
    };
    this.cases.push(c);
    this.requests.push({ id: ++this.ids.request, caseId: c.id, robotId, priority: 50, reason: "new detection", status: "waiting", createdAt: now });
    const robot = this.robots.get(robotId);
    if (robot) robot.mode = "waiting";
    return { ...c };
  }

  async getCases(): Promise<SurvivorCase[]> {
    return this.cases.map((c) => ({ ...c }));
  }

  private findCase(id: number): SurvivorCase {
    const c = this.cases.find((x) => x.id === id);
    if (!c) throw new StateError(`no case ${id}`);
    return c;
  }

  async setTriage(caseId: number, color: Triage, description: string): Promise<void> {
    const c = this.findCase(caseId);
    c.triage = color;
    c.description = description;
    c.updatedAt = Date.now();
  }

  async requestTakeover(robotId: number, caseId: number, priority: number, reason: string): Promise<void> {
    if (caseId) this.findCase(caseId);
    // Re-use the open request for this case instead of stacking duplicates.
    const open = this.requests.find((r) => r.caseId === caseId && caseId !== 0 && r.status === "waiting");
    if (open) {
      open.priority = Math.max(open.priority, priority);
      open.reason = reason;
      return;
    }
    this.requests.push({ id: ++this.ids.request, caseId, robotId, priority, reason, status: "waiting", createdAt: Date.now() });
  }

  async setPriority(requestId: number, priority: number, reason: string): Promise<void> {
    const r = this.requests.find((x) => x.id === requestId);
    if (!r) throw new StateError(`no takeover request ${requestId}`);
    r.priority = priority;
    r.reason = reason;
  }

  async getTakeoverRequests(): Promise<TakeoverRequest[]> {
    return this.requests.map((r) => ({ ...r }));
  }

  async getResponderTeams(): Promise<ResponderTeam[]> {
    return this.teams.map((t) => ({ ...t }));
  }

  async dispatchResponders(caseId: number, teamId: number): Promise<void> {
    const c = this.findCase(caseId);
    const t = this.teams.find((x) => x.id === teamId);
    if (!t) throw new StateError(`no responder team ${teamId}`);
    if (t.status !== "available") throw new StateError(`team ${t.name} is ${t.status}`);
    t.status = "en_route";
    c.status = "responders_sent";
    c.responderTeamId = teamId;
    c.updatedAt = Date.now();
  }

  async markHazard(x: number, y: number, kind: HazardKind, note: string): Promise<void> {
    this.hazards.push({ id: ++this.ids.hazard, x, y, kind, note, createdAt: Date.now() });
  }

  async getHazards(): Promise<Hazard[]> {
    return [...this.hazards];
  }

  async pushAlert(text: string, level: AlertLevel): Promise<void> {
    this.alerts.push({ id: ++this.ids.alert, text, level, createdAt: Date.now() });
  }

  async getAlerts(): Promise<Alert[]> {
    return [...this.alerts];
  }

  async requestConfirmation(action: string, detailsJson: string): Promise<number> {
    const c: Confirmation = { id: ++this.ids.confirmation, action, detailsJson, status: "pending", createdAt: Date.now() };
    this.confirmations.push(c);
    if (this.autoConfirm !== "none") c.status = this.autoConfirm === "approve" ? "approved" : "denied";
    return c.id;
  }

  async getConfirmation(id: number): Promise<Confirmation | null> {
    const c = this.confirmations.find((x) => x.id === id);
    return c ? { ...c } : null;
  }

  async logAgentAction(entry: AgentLogEntry): Promise<void> {
    this.agentLog.push(entry);
  }
}
