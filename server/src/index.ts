// SpacetimeDB module for the HackUTD 2026 rescue robot.
//
// Shared state and coordination rules: robots, coverage, survivor cases, the
// takeover queue, responder teams, hazards, alerts, agent confirmations and the
// agent log. Video and controls never go through here (Pi <-> headset direct).
//
// Names matter: clients call reducers over HTTP by name with positional JSON
// args, and read tables with SQL. Keep docs/spacetime.md in sync with this file.

import { schema, table, t, SenderError, ScheduleAt } from 'spacetimedb/server';
import type { Identity } from 'spacetimedb';
import type { ReducerCtx } from 'spacetimedb/server';

// ---------------------------------------------------------------------------
// Enums (stored as strings so SQL filters and JSON stay simple)
// ---------------------------------------------------------------------------

const ROBOT_MODES = ['searching', 'waiting', 'manual', 'returning', 'offline'] as const;
const TRIAGE_COLORS = ['unknown', 'red', 'yellow', 'green', 'black'] as const;
const HAZARD_KINDS = ['drop', 'obstacle', 'heat', 'other'] as const;
const ALERT_LEVELS = ['info', 'warning', 'critical'] as const;
// Status columns are only written by reducers, so they aren't validated here:
//   survivor_case.status    found | viewing | responders_sent | resolved
//   takeover_request.status waiting | claimed | done
//   responder_team.status   available | en_route | on_scene
//   confirmation.status     pending | approved | denied | expired

function checkEnum(field: string, value: string, allowed: readonly string[]): void {
  if (!allowed.includes(value)) {
    throw new SenderError(
      `invalid ${field} '${value}'; expected one of: ${allowed.join(', ')}`
    );
  }
}

function checkFinite(field: string, value: number): void {
  if (!Number.isFinite(value)) {
    throw new SenderError(`invalid ${field}: must be a finite number`);
  }
}

// ---------------------------------------------------------------------------
// Tuning
// ---------------------------------------------------------------------------

const WATCHDOG_INTERVAL_MICROS = 1_000_000n; // 1 s
const ROBOT_OFFLINE_AFTER_MICROS = 5_000_000n; // 5 s without a checkin
const CONFIRMATION_EXPIRE_AFTER_MICROS = 60_000_000n; // 60 s
const DUPLICATE_CASE_RADIUS_M = 1.5;
const DETECTION_PRIORITY = 50;

// ---------------------------------------------------------------------------
// Tables (all public so the Pi, headset and agent can read them with SQL)
// ---------------------------------------------------------------------------

const robot = table(
  { public: true },
  {
    id: t.u32().primaryKey(),
    name: t.string(),
    mode: t.string(), // searching | waiting | manual | returning | offline
    x: t.f32(),
    y: t.f32(),
    heading: t.f32(), // degrees
    battery: t.f32(), // 0..1
    operator_identity: t.identity().optional(),
    last_seen: t.timestamp(),
    simulated: t.bool(),
  }
);

const coverage_cell = table(
  {
    public: true,
    indexes: [
      { accessor: 'coverage_xy', algorithm: 'btree', columns: ['x', 'y'] as const },
    ],
  },
  {
    id: t.u64().primaryKey().autoInc(),
    x: t.i32(), // 1 m grid: floor(robot x)
    y: t.i32(),
    searched_by: t.u32(),
    searched_at: t.timestamp(),
  }
);

const survivor_case = table(
  { public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    robot_id: t.u32(),
    x: t.f32(),
    y: t.f32(),
    confidence: t.f32(),
    snapshot_url: t.string(),
    triage: t.string(), // unknown | red | yellow | green | black
    description: t.string(),
    status: t.string(), // found | viewing | responders_sent | resolved
    responder_team_id: t.u32(), // 0 = none
    created_at: t.timestamp(),
    updated_at: t.timestamp(),
  }
);

const takeover_request = table(
  { public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    case_id: t.u64(), // 0 = none
    robot_id: t.u32(),
    priority: t.u32(), // higher = more urgent
    reason: t.string(),
    status: t.string(), // waiting | claimed | done
    operator_identity: t.identity().optional(),
    created_at: t.timestamp(),
  }
);

const responder_team = table(
  { public: true },
  {
    id: t.u32().primaryKey(),
    name: t.string(),
    x: t.f32(),
    y: t.f32(),
    status: t.string(), // available | en_route | on_scene
  }
);

const hazard = table(
  { public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    x: t.f32(),
    y: t.f32(),
    kind: t.string(), // drop | obstacle | heat | other
    note: t.string(),
    created_at: t.timestamp(),
  }
);

const alert = table(
  { public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    text: t.string(),
    level: t.string(), // info | warning | critical
    created_at: t.timestamp(),
  }
);

const confirmation = table(
  { public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    action: t.string(),
    details_json: t.string(),
    status: t.string(), // pending | approved | denied | expired
    created_at: t.timestamp(),
    resolved_at: t.timestamp().optional(),
  }
);

const agent_log = table(
  { public: true },
  {
    id: t.u64().primaryKey().autoInc(),
    time: t.timestamp(),
    event: t.string(),
    tool: t.string(),
    args_json: t.string(),
    result_json: t.string(),
  }
);

// Schedule table that drives the watchdog reducer. One row, repeating interval.
const watchdog_schedule = table(
  {},
  {
    scheduled_id: t.u64().primaryKey().autoInc(),
    scheduled_at: t.scheduleAt(),
  }
);

// The local service identity owns writes. Evidence and runtime snapshots are private;
// the dashboard receives only data returned by the authenticated agent.
const service_owner = table({}, { id: t.u32().primaryKey(), identity: t.identity() });
const damage_incident = table({}, {
  id: t.string().primaryKey(), survey: t.string(), location: t.string(),
  image_sha256: t.string(), record_json: t.string(),
  recorded_at: t.timestamp(), updated_at: t.timestamp(),
});
const damage_photo = table({}, {
  id: t.string().primaryKey(), image_base64: t.string(), mime_type: t.string(),
});
const shared_state = table({}, {
  key: t.string().primaryKey(), value_json: t.string(), updated_at: t.timestamp(),
});
const app_event = table({}, {
  id: t.string().primaryKey(), value_json: t.string(), created_at: t.timestamp(),
});
const operator_update = table({}, {
  id: t.u64().primaryKey().autoInc(), value_json: t.string(), created_at: t.timestamp(),
});

const spacetimedb = schema({
  robot,
  coverage_cell,
  survivor_case,
  takeover_request,
  responder_team,
  hazard,
  alert,
  confirmation,
  agent_log,
  watchdog_schedule, service_owner, damage_incident, damage_photo, shared_state, operator_update, app_event,
});
export default spacetimedb;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function sameIdentity(a: Identity | undefined, b: Identity): boolean {
  return a !== undefined && a.isEqual(b);
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

export const init = spacetimedb.init(ctx => {
  ctx.db.service_owner.insert({ id: 1, identity: ctx.sender });
  // The one physical robot. Starts offline until the Pi checks in.
  if (ctx.db.robot.id.find(1) === null) {
    ctx.db.robot.insert({
      id: 1,
      name: 'PiCrawler',
      mode: 'offline',
      x: 0,
      y: 0,
      heading: 0,
      battery: 1,
      operator_identity: undefined,
      last_seen: ctx.timestamp,
      simulated: false,
    });
  }

  // Two responder teams staged near the entrance.
  const teams = [
    { id: 1, name: 'Team Alpha', x: -2, y: -2 },
    { id: 2, name: 'Team Bravo', x: 12, y: -2 },
  ];
  for (const team of teams) {
    if (ctx.db.responder_team.id.find(team.id) === null) {
      ctx.db.responder_team.insert({ ...team, status: 'available' });
    }
  }

  if (ctx.db.watchdog_schedule.count() === 0n) {
    ctx.db.watchdog_schedule.insert({
      scheduled_id: 0n,
      scheduled_at: ScheduleAt.interval(WATCHDOG_INTERVAL_MICROS),
    });
  }
});

// ---------------------------------------------------------------------------
// Robot (Pi)
// ---------------------------------------------------------------------------

export const robot_checkin = spacetimedb.reducer(
  {
    robot_id: t.u32(),
    x: t.f32(),
    y: t.f32(),
    heading: t.f32(),
    battery: t.f32(),
    mode: t.string(),
  },
  (ctx, { robot_id, x, y, heading, battery, mode }) => {
    requireService(ctx);
    checkEnum('mode', mode, ROBOT_MODES);
    checkFinite('x', x);
    checkFinite('y', y);
    checkFinite('heading', heading);
    checkFinite('battery', battery);
    const clampedBattery = Math.min(1, Math.max(0, battery));

    const existing = ctx.db.robot.id.find(robot_id);
    if (existing === null) {
      ctx.db.robot.insert({
        id: robot_id,
        name: `Robot ${robot_id}`,
        mode,
        x,
        y,
        heading,
        battery: clampedBattery,
        operator_identity: undefined,
        last_seen: ctx.timestamp,
        simulated: false,
      });
    } else {
      // waiting and manual are owned by the server (detections and claims).
      // The Pi reports them back but can't leave them; release_robot does.
      const serverOwned = existing.mode === 'waiting' || existing.mode === 'manual';
      ctx.db.robot.id.update({
        ...existing,
        mode: serverOwned ? existing.mode : mode,
        x,
        y,
        heading,
        battery: clampedBattery,
        last_seen: ctx.timestamp,
      });
    }

    // Mark the 1 m cell under the robot as searched (one row per x,y).
    const cx = Math.floor(x);
    const cy = Math.floor(y);
    const cell = ctx.db.coverage_cell.coverage_xy.filter([cx, cy]).next();
    if (cell.done) {
      ctx.db.coverage_cell.insert({
        id: 0n,
        x: cx,
        y: cy,
        searched_by: robot_id,
        searched_at: ctx.timestamp,
      });
    } else {
      ctx.db.coverage_cell.id.update({
        ...cell.value,
        searched_by: robot_id,
        searched_at: ctx.timestamp,
      });
    }
  }
);

export const report_detection = spacetimedb.reducer(
  {
    robot_id: t.u32(),
    x: t.f32(),
    y: t.f32(),
    confidence: t.f32(),
    snapshot_url: t.string(),
  },
  (ctx, { robot_id, x, y, confidence, snapshot_url }) => {
    requireService(ctx);
    checkFinite('x', x);
    checkFinite('y', y);
    checkFinite('confidence', confidence);
    const bot = ctx.db.robot.id.find(robot_id);
    if (bot === null) {
      throw new SenderError(`unknown robot ${robot_id}; call robot_checkin first`);
    }

    // Ignore duplicates: any unresolved case within 1.5 m.
    for (const c of ctx.db.survivor_case.iter()) {
      if (c.status === 'resolved') continue;
      if (Math.hypot(c.x - x, c.y - y) <= DUPLICATE_CASE_RADIUS_M) {
        return;
      }
    }

    const created = ctx.db.survivor_case.insert({
      id: 0n,
      robot_id,
      x,
      y,
      confidence,
      snapshot_url,
      triage: 'unknown',
      description: '',
      status: 'found',
      responder_team_id: 0,
      created_at: ctx.timestamp,
      updated_at: ctx.timestamp,
    });

    ctx.db.takeover_request.insert({
      id: 0n,
      case_id: created.id,
      robot_id,
      priority: DETECTION_PRIORITY,
      reason: `Possible survivor (confidence ${Math.round(confidence * 100)}%)`,
      status: 'waiting',
      operator_identity: undefined,
      created_at: ctx.timestamp,
    });

    // Stop and wait for an operator, unless someone is already driving it.
    if (bot.mode !== 'manual') {
      ctx.db.robot.id.update({ ...bot, mode: 'waiting' });
    }
  }
);

// ---------------------------------------------------------------------------
// Operator (headset)
// ---------------------------------------------------------------------------

export const claim_robot = spacetimedb.reducer(ctx => {
  requireService(ctx);
  // One robot per operator.
  for (const r of ctx.db.robot.iter()) {
    if (sameIdentity(r.operator_identity, ctx.sender) && r.mode === 'manual') {
      throw new SenderError(`you already control robot ${r.id}`);
    }
  }

  // Highest priority first, oldest first on ties. Skip robots that are offline
  // or already being driven by someone. Reducers are transactions, so two
  // operators can never claim the same request.
  let best: ReturnType<typeof ctx.db.takeover_request.id.find> = null;
  for (const req of ctx.db.takeover_request.iter()) {
    if (req.status !== 'waiting') continue;
    const bot = ctx.db.robot.id.find(req.robot_id);
    if (bot === null || bot.mode === 'manual' || bot.mode === 'offline') continue;
    if (
      best === null ||
      req.priority > best.priority ||
      (req.priority === best.priority && req.id < best.id)
    ) {
      best = req;
    }
  }
  if (best === null) {
    throw new SenderError('no waiting takeover requests');
  }

  ctx.db.takeover_request.id.update({
    ...best,
    status: 'claimed',
    operator_identity: ctx.sender,
  });

  const bot = ctx.db.robot.id.find(best.robot_id)!;
  ctx.db.robot.id.update({ ...bot, mode: 'manual', operator_identity: ctx.sender });

  if (best.case_id !== 0n) {
    const c = ctx.db.survivor_case.id.find(best.case_id);
    if (c !== null && c.status === 'found') {
      ctx.db.survivor_case.id.update({ ...c, status: 'viewing', updated_at: ctx.timestamp });
    }
  }
});

export const release_robot = spacetimedb.reducer(
  { robot_id: t.u32() },
  (ctx, { robot_id }) => {
    requireService(ctx);
    const bot = ctx.db.robot.id.find(robot_id);
    if (bot === null) {
      throw new SenderError(`unknown robot ${robot_id}`);
    }
    ctx.db.robot.id.update({ ...bot, mode: 'searching', operator_identity: undefined });

    const claimed = [...ctx.db.takeover_request.iter()].filter(
      r => r.robot_id === robot_id && r.status === 'claimed'
    );
    for (const req of claimed) {
      ctx.db.takeover_request.id.update({ ...req, status: 'done' });
    }
  }
);

export const resolve_confirmation = spacetimedb.reducer(
  { confirmation_id: t.u64(), approved: t.bool() },
  (ctx, { confirmation_id, approved }) => {
    requireService(ctx);
    const row = ctx.db.confirmation.id.find(confirmation_id);
    if (row === null) {
      throw new SenderError(`unknown confirmation ${confirmation_id}`);
    }
    if (row.status !== 'pending') {
      throw new SenderError(`confirmation ${confirmation_id} is already ${row.status}`);
    }
    ctx.db.confirmation.id.update({
      ...row,
      status: approved ? 'approved' : 'denied',
      resolved_at: ctx.timestamp,
    });
  }
);

// ---------------------------------------------------------------------------
// Agent (Nemotron)
// ---------------------------------------------------------------------------

export const set_triage = spacetimedb.reducer(
  { case_id: t.u64(), color: t.string(), description: t.string() },
  (ctx, { case_id, color, description }) => {
    requireService(ctx);
    checkEnum('color', color, TRIAGE_COLORS);
    const c = ctx.db.survivor_case.id.find(case_id);
    if (c === null) {
      throw new SenderError(`unknown case ${case_id}`);
    }
    ctx.db.survivor_case.id.update({
      ...c,
      triage: color,
      description,
      updated_at: ctx.timestamp,
    });
  }
);

export const set_priority = spacetimedb.reducer(
  { request_id: t.u64(), priority: t.u32(), reason: t.string() },
  (ctx, { request_id, priority, reason }) => {
    requireService(ctx);
    const req = ctx.db.takeover_request.id.find(request_id);
    if (req === null) {
      throw new SenderError(`unknown takeover request ${request_id}`);
    }
    ctx.db.takeover_request.id.update({ ...req, priority, reason });
  }
);

export const request_takeover = spacetimedb.reducer(
  { robot_id: t.u32(), case_id: t.u64(), priority: t.u32(), reason: t.string() },
  (ctx, { robot_id, case_id, priority, reason }) => {
    requireService(ctx);
    if (ctx.db.robot.id.find(robot_id) === null) {
      throw new SenderError(`unknown robot ${robot_id}`);
    }
    if (case_id !== 0n && ctx.db.survivor_case.id.find(case_id) === null) {
      throw new SenderError(`unknown case ${case_id}`);
    }
    ctx.db.takeover_request.insert({
      id: 0n,
      case_id,
      robot_id,
      priority,
      reason,
      status: 'waiting',
      operator_identity: undefined,
      created_at: ctx.timestamp,
    });
  }
);

export const dispatch_responders = spacetimedb.reducer(
  { case_id: t.u64(), team_id: t.u32() },
  (ctx, { case_id, team_id }) => {
    requireService(ctx);
    const c = ctx.db.survivor_case.id.find(case_id);
    if (c === null) {
      throw new SenderError(`unknown case ${case_id}`);
    }
    if (c.status === 'resolved') {
      throw new SenderError(`case ${case_id} is already resolved`);
    }
    const team = ctx.db.responder_team.id.find(team_id);
    if (team === null) {
      throw new SenderError(`unknown responder team ${team_id}`);
    }
    if (team.status !== 'available') {
      throw new SenderError(`team ${team_id} is ${team.status}, not available`);
    }
    ctx.db.responder_team.id.update({ ...team, status: 'en_route' });
    ctx.db.survivor_case.id.update({
      ...c,
      status: 'responders_sent',
      responder_team_id: team_id,
      updated_at: ctx.timestamp,
    });
  }
);

export const mark_hazard = spacetimedb.reducer(
  { x: t.f32(), y: t.f32(), kind: t.string(), note: t.string() },
  (ctx, { x, y, kind, note }) => {
    requireService(ctx);
    checkEnum('kind', kind, HAZARD_KINDS);
    checkFinite('x', x);
    checkFinite('y', y);
    ctx.db.hazard.insert({ id: 0n, x, y, kind, note, created_at: ctx.timestamp });
  }
);

export const push_alert = spacetimedb.reducer(
  { text: t.string(), level: t.string() },
  (ctx, { text, level }) => {
    requireService(ctx);
    checkEnum('level', level, ALERT_LEVELS);
    ctx.db.alert.insert({ id: 0n, text, level, created_at: ctx.timestamp });
  }
);

export const request_confirmation = spacetimedb.reducer(
  { action: t.string(), details_json: t.string() },
  (ctx, { action, details_json }) => {
    requireService(ctx);
    if (action.length === 0) {
      throw new SenderError('action must not be empty');
    }
    ctx.db.confirmation.insert({
      id: 0n,
      action,
      details_json,
      status: 'pending',
      created_at: ctx.timestamp,
      resolved_at: undefined,
    });
  }
);

export const log_agent_action = spacetimedb.reducer(
  { event: t.string(), tool: t.string(), args_json: t.string(), result_json: t.string() },
  (ctx, { event, tool, args_json, result_json }) => {
    requireService(ctx);
    ctx.db.agent_log.insert({
      id: 0n,
      time: ctx.timestamp,
      event,
      tool,
      args_json,
      result_json,
    });
  }
);

// Durable property evidence: the photo and its observations commit together.
function requireService(ctx: ReducerCtx<typeof spacetimedb.schemaType>): void {
  const owner = ctx.db.service_owner.id.find(1);
  if (!owner || !owner.identity.isEqual(ctx.sender)) throw new SenderError('Service identity required');
}
function checkedRecord(record_json: string) {
  if (record_json.length > 50000) throw new SenderError('Incident record too large');
  const r = JSON.parse(record_json);
  if (!r || !/^[0-9a-f-]{36}$/.test(r.id) || typeof r.survey !== 'string' || !r.survey.trim()
      || typeof r.location !== 'string' || !/^[a-f0-9]{64}$/.test(r.imageSha256)
      || !['water', 'fire', 'structural', 'debris', 'electrical', 'other'].includes(r.category)
      || !['low', 'moderate', 'high', 'unknown'].includes(r.severity)
      || typeof r.title !== 'string' || !r.title.trim() || r.incident !== true
      || !Number.isFinite(r.confidence) || r.confidence < 0 || r.confidence > 1
      || !Array.isArray(r.observations) || r.observations.length === 0 || !Array.isArray(r.uncertainties)
      || !Number.isFinite(r.capturedAt) || !Number.isFinite(r.recordedAt)) throw new SenderError('Invalid incident record');
  return r;
}
export const record_damage = spacetimedb.reducer(
  { record_json: t.string(), image_base64: t.string() },
  (ctx, { record_json, image_base64 }) => {
    requireService(ctx);
    const r = checkedRecord(record_json);
    if (!image_base64.startsWith('/9j/') || image_base64.length > 5600000) throw new SenderError('Expected a JPEG under 4 MB');
    const existing = ctx.db.damage_incident.id.find(r.id);
    if (existing) {
      if (existing.image_sha256 !== r.imageSha256) throw new SenderError('Incident ID already has different evidence');
      return; // Retry/import is idempotent; never overwrite a later human review.
    }
    for (const old of ctx.db.damage_incident.iter()) {
      if (old.survey === r.survey && old.location === r.location && old.image_sha256 === r.imageSha256) return;
    }
    ctx.db.damage_photo.insert({ id: r.id, image_base64, mime_type: 'image/jpeg' });
    ctx.db.damage_incident.insert({ id: r.id, survey: r.survey, location: r.location,
      image_sha256: r.imageSha256, record_json, recorded_at: ctx.timestamp, updated_at: ctx.timestamp });
  }
);
export const review_damage = spacetimedb.reducer(
  { incident_id: t.string(), note: t.string() },
  (ctx, { incident_id, note }) => {
    requireService(ctx);
    if (note.length > 3000) throw new SenderError('Review note too long');
    const row = ctx.db.damage_incident.id.find(incident_id);
    if (!row) throw new SenderError('Incident not found');
    const r = JSON.parse(row.record_json);
    r.status = 'reviewed'; r.reviewNote = note;
    r.reviewedAt = Number(ctx.timestamp.microsSinceUnixEpoch / 1000n);
    ctx.db.damage_incident.id.update({ ...row, record_json: JSON.stringify(r), updated_at: ctx.timestamp });
  }
);
export const save_shared_state = spacetimedb.reducer(
  { key: t.string(), value_json: t.string() },
  (ctx, { key, value_json }) => {
    requireService(ctx);
    if (!['dashboard', 'damage_survey'].includes(key) || value_json.length > 100000) throw new SenderError('Invalid shared state');
    JSON.parse(value_json);
    const value = { key, value_json, updated_at: ctx.timestamp };
    if (ctx.db.shared_state.key.find(key)) ctx.db.shared_state.key.update(value);
    else ctx.db.shared_state.insert(value);
  }
);
export const import_app_event = spacetimedb.reducer(
  { id: t.string(), value_json: t.string() },
  (ctx, { id, value_json }) => {
    requireService(ctx);
    if (!/^[a-f0-9]{64}$/.test(id) || value_json.length > 100000) throw new SenderError('Invalid audit event');
    JSON.parse(value_json);
    if (!ctx.db.app_event.id.find(id)) ctx.db.app_event.insert({ id, value_json, created_at: ctx.timestamp });
  }
);
export const append_operator_update = spacetimedb.reducer(
  { value_json: t.string() },
  (ctx, { value_json }) => {
    requireService(ctx);
    if (value_json.length > 2000) throw new SenderError('Operator update too long');
    JSON.parse(value_json);
    ctx.db.operator_update.insert({ id: 0n, value_json, created_at: ctx.timestamp });
  }
);

// ---------------------------------------------------------------------------
// Watchdog (scheduled, every ~1 s)
// ---------------------------------------------------------------------------

export const watchdog = spacetimedb.reducer(
  { onSchedule: watchdog_schedule },
  { arg: watchdog_schedule.rowType },
  ctx => {
    if (!ctx.senderAuth.isInternal) {
      throw new SenderError('watchdog is called by the scheduler only');
    }
    const now = ctx.timestamp.microsSinceUnixEpoch;

    const stale = [...ctx.db.robot.iter()].filter(
      r =>
        r.mode !== 'offline' &&
        now - r.last_seen.microsSinceUnixEpoch > ROBOT_OFFLINE_AFTER_MICROS
    );
    for (const r of stale) {
      ctx.db.robot.id.update({ ...r, mode: 'offline' });
      ctx.db.alert.insert({
        id: 0n,
        text: `${r.name} went offline`,
        level: 'warning',
        created_at: ctx.timestamp,
      });
    }

    const expired = [...ctx.db.confirmation.iter()].filter(
      c =>
        c.status === 'pending' &&
        now - c.created_at.microsSinceUnixEpoch > CONFIRMATION_EXPIRE_AFTER_MICROS
    );
    for (const c of expired) {
      ctx.db.confirmation.id.update({ ...c, status: 'expired', resolved_at: ctx.timestamp });
    }
  }
);
