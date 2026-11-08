# Local SpacetimeDB backend

SpacetimeDB 2.14.1 now stores the app's shared rescue state and property evidence:
robots, coverage, survivor cases, takeover requests, responder teams, hazards,
alerts, approvals, audit events, operator updates, dashboard snapshots, survey
settings, incident observations, review notes, and JPEG photos. Photos and incident
metadata are inserted in one transaction. Reviews update their record without
changing the saved observations or photo.

## Start the local stack

From the repository root (Python 3.11+, Node, and the existing robot Python
virtual environment are required):

```sh
python3 scripts/local-stack.py start
```

The helper installs the official project-local runtime if needed, starts a durable
server on `127.0.0.1:3000`, publishes the module without deleting data, configures
`agent/.env`, imports legacy evidence/logs, then starts the dashboard and agent.
It uses the local server's identity; no Maincloud login, credit, or internet is
needed for database operation. Installation and Nemotron model requests still
need internet. Existing manually launched old services should be stopped before
the first run to avoid port conflicts. Service logs and managed PIDs are under
`.spacetime/run/`.

- Operations: http://localhost:8090
- Damage & incidents: http://localhost:8090/damage
- Operator voice: http://localhost:8788/operator
- Stored shared state: http://localhost:8090/api/shared

```sh
python3 scripts/local-stack.py status
python3 scripts/local-stack.py stop
```

Stop only stops processes started by this helper. It never removes database data.
Restarting leaves robot control disabled and the damage survey stopped; survey
names, location labels, incident evidence, reviews, and operator history remain.

Individual actions: `database` starts only the database; `publish` builds and
publishes the module; `configure` updates local connection settings. Do not use
`--delete-data` when publishing this database.

## Storage and identity

All runtime data and local credentials are under the ignored `.spacetime/`
directory. Keep it when restarting or updating the app; copy it while the server
is stopped to make a local backup. Do not commit it. The helper backs up the prior
`.env` inside that private directory and saves the service token in the ignored
`agent/.env` with restricted file permissions. Tokens never go to the browser.

The database is loopback-only. The dashboard mediates LAN reads and restricts
changes to the host Mac as before. New evidence/state tables are private, and
external reducers require the publishing service identity. Existing public rescue
tables remain queryable by local database clients. Do not expose port 3000 to an
untrusted network.

In active use `STATE_MODE=spacetime`; unavailable database writes fail explicitly.
Memory state and file evidence are retained only for isolated offline tests and
legacy import. Camera/video, radio, motor commands, hardware leases, and emergency
stop handling stay on the direct local hardware path. Persisted control snapshots
are history, never commands to resume motion.

## Legacy import

```sh
cd agent
npm run migrate:spacetime
```

Imports JSON/JPEG incidents from `DAMAGE_DATA_DIR` (default `data/incidents`) and
JSONL logs from `logs/`. Each photo is hash-verified after insertion. Imports are
idempotent and do not overwrite later human reviews. Original files remain as
backups; the running app reads records and photos from SpacetimeDB.

## Verify

```sh
npm --prefix server run typecheck
npm --prefix agent run typecheck
```

The live integration test uses a separate database, never production incidents:
publish this module as `rescue-robot-test`, then run from `agent/`:

```sh
SPACETIME_TEST_DB=rescue-robot-test npm test
```

The migration was verified against the actual local server, including a full
server restart with preserved audit-event counts, incident records, reviewed
status, and matching photo hashes. Normal tests skip that integration check unless
`SPACETIME_TEST_DB` is set. Table/reducer details: [docs/spacetime.md](../docs/spacetime.md).

## Git-shared demo evidence

`demo-data/` contains selected, clearly labeled staged inspection records and
photos that can be restored without copying database credentials. After starting
the local stack, run `npm --prefix agent run demo:restore` from the repository root.
See `demo-data/README.md` for the snapshot format and scope. Raw runtime database
files remain ignored by Git.
