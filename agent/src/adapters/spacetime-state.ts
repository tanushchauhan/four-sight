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

/**
 * SpacetimeDB over its HTTP API (docs/spacetime.md):
 *   POST {url}/v1/database/{db}/call/{reducer}   body: JSON array of positional args
 *   POST {url}/v1/database/{db}/sql              body: SQL text
 *
 * Verified against the local SpacetimeDB module. The SQL response decoder below is
 * deliberately tolerant of the SATS-JSON shapes (options, timestamps, identities)
 * because the exact encoding varies between SpacetimeDB versions.
 */
export class SpacetimeState implements StateStore {
  constructor(
    private readonly baseUrl: string,
    private readonly db: string,
    private readonly token: string,
    readonly area: SearchArea,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {}

  private headers(contentType: string): Record<string, string> {
    const h: Record<string, string> = { "Content-Type": contentType };
    if (this.token) h.Authorization = `Bearer ${this.token}`;
    return h;
  }

  async call(reducer: string, args: unknown[]): Promise<void> {
    const res = await this.fetchImpl(`${this.baseUrl}/v1/database/${encodeURIComponent(this.db)}/call/${reducer}`, {
      method: "POST",
      headers: this.headers("application/json"),
      body: JSON.stringify(args),
      signal: AbortSignal.timeout(10000),
    });
    if (!res.ok) throw new StateError(`reducer ${reducer} failed: ${res.status} ${await res.text()}`);
  }

  async sql(query: string): Promise<Record<string, unknown>[]> {
    const res = await this.fetchImpl(`${this.baseUrl}/v1/database/${encodeURIComponent(this.db)}/sql`, {
      method: "POST",
      headers: this.headers("text/plain"),
      body: query,
      signal: AbortSignal.timeout(10000),
    });
    if (!res.ok) throw new StateError(`sql failed: ${res.status} ${await res.text()}`);
    return decodeSqlResponse(await res.json());
  }

  async saveSharedState(key: string, value: unknown): Promise<void> {
    await this.call('save_shared_state', [key, JSON.stringify(value)]);
  }
  async getSharedState(key: string): Promise<any | null> {
    if (!['dashboard', 'damage_survey'].includes(key)) throw new StateError('Unknown shared state key');
    const rows = await this.sql(`SELECT value_json FROM shared_state WHERE key = '${key}'`);
    return rows[0] ? JSON.parse(String(rows[0].value_json)) : null;
  }
  async resolveConfirmation(id: number, approved: boolean): Promise<void> {
    await this.call('resolve_confirmation', [id, approved]);
  }

  async robotCheckin(s: RobotState): Promise<void> {
    await this.call("robot_checkin", [s.id, s.x, s.y, s.heading, s.battery, s.mode]);
  }

  async getRobots(): Promise<RobotState[]> {
    const rows = await this.sql("SELECT * FROM robot");
    return rows.map((r) => ({
      id: n(r.id),
      name: s(r.name),
      mode: s(r.mode) as RobotState["mode"],
      x: n(r.x),
      y: n(r.y),
      heading: n(r.heading),
      battery: n(r.battery),
      pan: 0,
      tilt: 0,
      lastSeen: ts(r.last_seen),
    }));
  }

  async getCoverage(): Promise<CoverageCell[]> {
    const rows = await this.sql("SELECT * FROM coverage_cell");
    return rows.map((r) => ({ x: n(r.x), y: n(r.y), searchedBy: n(r.searched_by), searchedAt: ts(r.searched_at) }));
  }

  async reportDetection(robotId: number, x: number, y: number, confidence: number, snapshotUrl: string): Promise<SurvivorCase | null> {
    await this.call("report_detection", [robotId, x, y, confidence, snapshotUrl]);
    // Reducers return nothing; find the case it created (none if it was a duplicate).
    const cases = await this.getCases();
    return cases.filter((c) => c.snapshotUrl === snapshotUrl && c.robotId === robotId).sort((a, b) => b.id - a.id)[0] ?? null;
  }

  async getCases(): Promise<SurvivorCase[]> {
    const rows = await this.sql("SELECT * FROM survivor_case");
    return rows.map((r) => ({
      id: n(r.id),
      robotId: n(r.robot_id),
      x: n(r.x),
      y: n(r.y),
      confidence: n(r.confidence),
      snapshotUrl: s(r.snapshot_url),
      triage: s(r.triage) as Triage,
      description: s(r.description),
      status: s(r.status) as SurvivorCase["status"],
      responderTeamId: n(r.responder_team_id),
      createdAt: ts(r.created_at),
      updatedAt: ts(r.updated_at),
    }));
  }

  async setTriage(caseId: number, color: Triage, description: string): Promise<void> {
    await this.call("set_triage", [caseId, color, description]);
  }

  async requestTakeover(robotId: number, caseId: number, priority: number, reason: string): Promise<void> {
    await this.call("request_takeover", [robotId, caseId, priority, reason]);
  }

  async setPriority(requestId: number, priority: number, reason: string): Promise<void> {
    await this.call("set_priority", [requestId, priority, reason]);
  }

  async getTakeoverRequests(): Promise<TakeoverRequest[]> {
    const rows = await this.sql("SELECT * FROM takeover_request");
    return rows.map((r) => ({
      id: n(r.id),
      caseId: n(r.case_id),
      robotId: n(r.robot_id),
      priority: n(r.priority),
      reason: s(r.reason),
      status: s(r.status) as TakeoverRequest["status"],
      createdAt: ts(r.created_at),
    }));
  }

  async getResponderTeams(): Promise<ResponderTeam[]> {
    const rows = await this.sql("SELECT * FROM responder_team");
    return rows.map((r) => ({ id: n(r.id), name: s(r.name), x: n(r.x), y: n(r.y), status: s(r.status) as ResponderTeam["status"] }));
  }

  async dispatchResponders(caseId: number, teamId: number): Promise<void> {
    await this.call("dispatch_responders", [caseId, teamId]);
  }

  async markHazard(x: number, y: number, kind: HazardKind, note: string): Promise<void> {
    await this.call("mark_hazard", [x, y, kind, note]);
  }

  async getHazards(): Promise<Hazard[]> {
    const rows = await this.sql("SELECT * FROM hazard");
    return rows.map((r) => ({ id: n(r.id), x: n(r.x), y: n(r.y), kind: s(r.kind) as HazardKind, note: s(r.note), createdAt: ts(r.created_at) }));
  }

  async pushAlert(text: string, level: AlertLevel): Promise<void> {
    await this.call("push_alert", [text, level]);
  }

  async getAlerts(): Promise<Alert[]> {
    const rows = await this.sql("SELECT * FROM alert");
    return rows.map((r) => ({ id: n(r.id), text: s(r.text), level: s(r.level) as AlertLevel, createdAt: ts(r.created_at) }));
  }

  async requestConfirmation(action: string, detailsJson: string): Promise<number> {
    await this.call("request_confirmation", [action, detailsJson]);
    const rows = await this.sql("SELECT * FROM confirmation WHERE status = 'pending'");
    const mine = rows.filter((r) => s(r.action) === action && s(r.details_json) === detailsJson).map((r) => n(r.id));
    if (mine.length === 0) throw new StateError("confirmation row not found after request_confirmation");
    return Math.max(...mine);
  }

  async getConfirmation(id: number): Promise<Confirmation | null> {
    const rows = await this.sql(`SELECT * FROM confirmation WHERE id = ${Math.trunc(id)}`);
    const r = rows[0];
    if (!r) return null;
    return { id: n(r.id), action: s(r.action), detailsJson: s(r.details_json), status: s(r.status) as Confirmation["status"], createdAt: ts(r.created_at) };
  }

  async logAgentAction(e: AgentLogEntry): Promise<void> {
    await this.call("log_agent_action", [e.event, e.tool, truncate(JSON.stringify(e.args)), truncate(JSON.stringify(e.result))]);
  }
}

function truncate(text: string | undefined, max = 4000): string {
  const t = text ?? "null";
  return t.length > max ? `${t.slice(0, max)}…` : t;
}

// ---------- SATS-JSON decoding (tolerant) ----------

/** Unwrap option/timestamp/identity wrappers into plain JS values. */
export function decodeValue(v: unknown): unknown {
  if (v === null || typeof v !== "object") return v;
  if (Array.isArray(v)) return v.map(decodeValue);
  const o = v as Record<string, unknown>;
  const keys = Object.keys(o);
  if (keys.length === 1) {
    const k = keys[0]!;
    if (k === "some") return decodeValue(o.some);
    if (k === "none") return null;
    if (k === "__timestamp_micros_since_unix_epoch__") return Number(o[k]) / 1000;
    if (k === "__identity__" || k === "__connection_id__") return String(o[k]);
    // Sum-type enum encoded as {"variant": []}
    if (Array.isArray(o[k]) && (o[k] as unknown[]).length === 0) return k;
  }
  return o;
}

function columnName(el: unknown, i: number): string {
  const name = (el as { name?: unknown })?.name;
  if (typeof name === "string") return name;
  if (name && typeof name === "object" && "some" in name) return String((name as { some: unknown }).some);
  return `col${i}`;
}

/** Turn the /sql response ([{schema:{elements}, rows}]) into objects keyed by column name. */
export function decodeSqlResponse(body: unknown): Record<string, unknown>[] {
  const statements = Array.isArray(body) ? body : [body];
  const out: Record<string, unknown>[] = [];
  for (const st of statements as { schema?: { elements?: unknown[] }; rows?: unknown[][] }[]) {
    const cols = (st?.schema?.elements ?? []).map(columnName);
    for (const row of st?.rows ?? []) {
      const obj: Record<string, unknown> = {};
      row.forEach((val, i) => (obj[cols[i] ?? `col${i}`] = decodeValue(val)));
      out.push(obj);
    }
  }
  return out;
}

function n(v: unknown): number {
  const x = typeof v === "bigint" ? Number(v) : Number(v);
  return Number.isFinite(x) ? x : 0;
}

function s(v: unknown): string {
  return v === null || v === undefined ? "" : String(v);
}

function ts(v: unknown): number {
  if (typeof v === "number") return v > 1e14 ? v / 1000 : v; // micros → ms
  if (Array.isArray(v) && typeof v[0] === "number") return v[0] / 1000;
  if (typeof v === "string") return Date.parse(v) || 0;
  return 0;
}
