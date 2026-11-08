# SpacetimeDB module reference

The module lives in `server/src/index.ts`, and `server/README.md` covers setup and publishing. This page is the contract the Pi, the headset and the Nemotron agent build against. If you change a name or an argument order, update this page in the same change.

- One physical robot (PiCrawler, `robot_id = 1`). Tables are keyed by `robot_id` so simulated robots can be added later.
- Original rescue tables are public; evidence, photos, shared snapshots, and history are private and read by the publishing service identity.
- Enum-like columns are plain strings. Reducers check them and throw on a bad value.
- Coordinates are metres in the arena frame. Coverage uses a 1 m grid: cell = `(floor(x), floor(y))`.

## Who uses what

| Client | Talks over | Calls | Reads |
| --- | --- | --- | --- |
| Pi (`robot/`) | HTTP | `robot_checkin` (a few times a second), `report_detection`, `mark_hazard` | `robot` (its own mode: `waiting` / `manual` / `searching`) |
| Headset (`headset/`) | HTTP | `claim_robot`, `release_robot`, `resolve_confirmation` | `takeover_request`, `survivor_case` (triage card), `alert`, `confirmation` (pending), `hazard`, `responder_team` |
| Agent (`agent/`) | HTTP (reducer calls + SQL) | `set_triage`, `set_priority`, `request_takeover`, `dispatch_responders`, `mark_hazard`, `push_alert`, `request_confirmation`, `log_agent_action` | everything |
| Scheduler | internal | `watchdog` every 1 s | |

## HTTP API

Active local base URL: `http://127.0.0.1:3000`. Database name: `rescue-robot`. The robot and headset use the agent/dashboard bridge; the database itself listens only on this Mac.

```
POST /v1/identity                          -> {"identity": "...", "token": "..."}
POST /v1/database/<db>/call/<reducer>      body: JSON array of positional args
POST /v1/database/<db>/sql                 body: SQL text
Header: Authorization: Bearer <token>
```

The publishing service token in the ignored `agent/.env` identifies the authorized
writer. `scripts/local-stack.py configure` copies it from the project-local CLI
configuration. Do not create a random client identity and expect it to have write
access; external reducers enforce the service identity. Browsers, headsets and the
Pi never receive this token. The role table above describes the reducer contracts;
the active deployment mediates them through the local agent/dashboard.

A reducer that throws comes back as an HTTP error (4xx/5xx) with the message in the body. A successful call returns an empty 200. Reducers don't return values, so to get a new row's id, read it back with SQL (for example, the highest `id` in `confirmation` with `status = 'pending'`).

### Value encoding (unverified, check against a live database)

- Args: `u32`/`u64` as JSON numbers, `f32` as numbers, `bool` as `true`/`false`, strings as strings.
- SQL response: `[{"schema": {...}, "rows": [[...], ...]}]`. Each row is an array in column order. Read column names from `schema.elements[i].name`.
- In rows, `u64` may arrive as a number or a string, timestamps as `{"__timestamp_micros_since_unix_epoch__": n}`, identities as `{"__identity__": "0x..."}`, and optional columns as `{"some": v}` or `{"none": []}`. Parse defensively.

## Tables

### `robot`
| Column | Type | Notes |
| --- | --- | --- |
| `id` | u32, pk | 1 = the PiCrawler (seeded by `init`, starts `offline`) |
| `name` | string | |
| `mode` | string | `searching` / `waiting` / `manual` / `returning` / `offline` |
| `x`, `y` | f32 | metres |
| `heading` | f32 | degrees |
| `battery` | f32 | 0 to 1 (clamped) |
| `operator_identity` | Identity, optional | set by `claim_robot`, cleared by `release_robot` |
| `last_seen` | timestamp | last `robot_checkin` |
| `simulated` | bool | false for the real robot |

### `coverage_cell`
| Column | Type | Notes |
| --- | --- | --- |
| `id` | u64, pk, autoinc | |
| `x`, `y` | i32 | 1 m grid cell; one row per (x, y), updated in place |
| `searched_by` | u32 | robot id |
| `searched_at` | timestamp | |

### `survivor_case`
(Not `case`, which is an SQL reserved word.)
| Column | Type | Notes |
| --- | --- | --- |
| `id` | u64, pk, autoinc | |
| `robot_id` | u32 | robot that found it |
| `x`, `y` | f32 | |
| `confidence` | f32 | detector confidence, 0 to 1 |
| `snapshot_url` | string | served by the Pi |
| `triage` | string | `unknown` / `red` / `yellow` / `green` / `black` |
| `description` | string | from the agent |
| `status` | string | `found` / `viewing` / `responders_sent` / `resolved` |
| `responder_team_id` | u32 | 0 = none |
| `created_at`, `updated_at` | timestamp | |

No reducer sets `resolved` yet. It exists so the duplicate check ("open case" means status is not `resolved`) has an end state.

### `takeover_request`
| Column | Type | Notes |
| --- | --- | --- |
| `id` | u64, pk, autoinc | |
| `case_id` | u64 | 0 = not tied to a case |
| `robot_id` | u32 | |
| `priority` | u32 | higher = more urgent; detections start at 50 |
| `reason` | string | |
| `status` | string | `waiting` / `claimed` / `done` |
| `operator_identity` | Identity, optional | who claimed it |
| `created_at` | timestamp | |

### `responder_team`
| Column | Type | Notes |
| --- | --- | --- |
| `id` | u32, pk | seeded: 1 Team Alpha (-2, -2), 2 Team Bravo (12, -2) |
| `name` | string | |
| `x`, `y` | f32 | |
| `status` | string | `available` / `en_route` / `on_scene` |

No reducer moves a team to `on_scene` or back to `available` yet.

### `hazard`
`id` u64 pk autoinc, `x` f32, `y` f32, `kind` (`drop` / `obstacle` / `heat` / `other`), `note` string, `created_at` timestamp.

### `alert`
`id` u64 pk autoinc, `text` string, `level` (`info` / `warning` / `critical`), `created_at` timestamp. The headset shows these. The watchdog also writes a `warning` when a robot goes offline.

### `confirmation`
`id` u64 pk autoinc, `action` string, `details_json` string, `status` (`pending` / `approved` / `denied` / `expired`), `created_at` timestamp, `resolved_at` optional timestamp.

The agent creates a row and polls it. The headset shows pending rows and answers with `resolve_confirmation`. Rows still pending after 60 s become `expired`.

### `agent_log`
`id` u64 pk autoinc, `time` timestamp, `event` string, `tool` string, `args_json` string, `result_json` string.

### `watchdog_schedule` (internal)
`scheduled_id` u64 pk autoinc, `scheduled_at` ScheduleAt. `init` inserts one row with a 1 s interval, which drives `watchdog`. It isn't public, and clients don't use it.

## Reducers

Arguments are positional in the order shown.

| Reducer | Args | Does |
| --- | --- | --- |
| `robot_checkin` | `robot_id u32, x f32, y f32, heading f32, battery f32, mode string` | Upserts the robot (new ids get the name `Robot <id>`). Sets `last_seen`. Marks the cell `(floor(x), floor(y))` as searched by this robot. If the server has the robot in `waiting` or `manual`, the reported mode is ignored and the server's mode stays. Only `release_robot` leaves those states. Any other mode is taken as reported. |
| `report_detection` | `robot_id u32, x f32, y f32, confidence f32, snapshot_url string` | Robot must exist. Does nothing if any unresolved case is within 1.5 m. Otherwise creates a `survivor_case` (triage `unknown`, status `found`) and a `takeover_request` (priority 50, `waiting`), and puts the robot in `waiting` unless it's already `manual`. |
| `claim_robot` | none | Picks the waiting request with the highest priority (oldest wins ties), skipping robots that are `offline` or already `manual`. Marks it `claimed` by `ctx.sender`, sets the robot to `manual` with `operator_identity = sender`, and moves the case from `found` to `viewing`. Throws if the caller already drives a robot or nothing is waiting. It runs as one transaction, so two operators can't claim the same request. |
| `release_robot` | `robot_id u32` | Robot goes to `searching` and its operator is cleared. Its `claimed` requests become `done`. Doesn't check that the caller is the operator. |
| `set_triage` | `case_id u64, color string, description string` | `color` is one of `unknown`, `red`, `yellow`, `green`, `black`. |
| `set_priority` | `request_id u64, priority u32, reason string` | Re-ranks a takeover request. |
| `request_takeover` | `robot_id u32, case_id u64, priority u32, reason string` | New `waiting` request. `case_id` 0 = none, otherwise it must exist. Doesn't change the robot's mode. |
| `dispatch_responders` | `case_id u64, team_id u32` | Team must be `available` and the case not `resolved`. Team goes to `en_route`, case to `responders_sent` with `responder_team_id = team_id`. |
| `mark_hazard` | `x f32, y f32, kind string, note string` | `kind` is one of `drop`, `obstacle`, `heat`, `other`. |
| `push_alert` | `text string, level string` | `level` is one of `info`, `warning`, `critical`. |
| `request_confirmation` | `action string, details_json string` | New `pending` row. `action` must not be empty. |
| `resolve_confirmation` | `confirmation_id u64, approved bool` | Headset answer. The row must still be `pending`. Sets `approved` or `denied` and `resolved_at`. |
| `log_agent_action` | `event string, tool string, args_json string, result_json string` | Appends to `agent_log`. |
| `watchdog` | scheduled | Every 1 s: robots not seen for over 5 s become `offline` and get a warning alert; pending confirmations older than 60 s become `expired`. Throws if a client calls it. |
| `init` | lifecycle | Seeds robot 1 (`PiCrawler`, offline), teams 1 and 2, and the watchdog schedule. |

## Useful SQL

```sql
SELECT * FROM robot
SELECT * FROM takeover_request WHERE status = 'waiting'
SELECT * FROM survivor_case WHERE status != 'resolved'
SELECT * FROM confirmation WHERE status = 'pending'
SELECT * FROM alert
SELECT * FROM coverage_cell
SELECT * FROM responder_team WHERE status = 'available'
```

SpacetimeDB SQL is a subset. `ORDER BY` and `LIMIT` may not be supported, so sort on the client.

## Unverified

The CLI wasn't installed while this was written, so the module has passed `tsc` against `spacetimedb@2.14.1` but has never been built, published or called. Check these on the first publish:

- `spacetime publish` builds `server/` as laid out.
- The watchdog fires every second.
- The JSON value encodings above (u64, timestamp, identity, option) in SQL results.
- Whether `ORDER BY` / `LIMIT` work in HTTP SQL.
- The error status code and body when a reducer throws.

## Local migration: shared application data

The live app now uses a local SpacetimeDB 2.14.1 instance. Start it with
`python3 scripts/local-stack.py start` from the repository root. Full lifecycle,
identity, import, and verification instructions are in `server/README.md`.

Additional private tables:

| Table | Purpose |
| --- | --- |
| `service_owner` | Publishing identity authorized to write shared records |
| `damage_incident` | Incident ID, survey/location, photo hash, complete structured observations and human review |
| `damage_photo` | JPEG bytes encoded as base64, committed with its incident |
| `shared_state` | Latest dashboard snapshot and saved damage survey details |
| `operator_update` | Durable spoken-update history |
| `app_event` | Complete audit events with stable, idempotent import IDs |

Additional reducers: `record_damage(record_json, image_base64)`,
`review_damage(incident_id, note)`, `save_shared_state(key, value_json)`,
`append_operator_update(value_json)`, and `import_app_event(id, value_json)`.
The module enforces the service identity on external writes. Photo/record
insertion and deduplication run in a transaction, including during migration.
Reviews use the database timestamp and preserve the original photo and model
observations. Maincloud is optional; the local server does not need hackathon
credits. Database state does not replace the direct video/audio or motor safety
path. Surveying and motion never resume automatically from saved state.
