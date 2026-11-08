# server/: SpacetimeDB module

TypeScript SpacetimeDB module that holds the shared state and rules for the rescue robot: robots, coverage, survivor cases, the takeover queue, responder teams, hazards, alerts, agent confirmations and the agent log. Video and controls never go through here.

Full table and reducer reference: [docs/spacetime.md](../docs/spacetime.md).

```
server/
  package.json      spacetimedb (module library) + typescript
  tsconfig.json     copied from the official basic-ts template; don't change the "required" options
  src/index.ts      the whole module: tables, reducers, watchdog
```

The layout matches what `spacetime init --lang typescript` generates for the module part (`package.json`, `tsconfig.json`, `src/index.ts`), so the CLI's default module path works when you run it from this folder.

## Typecheck

```
cd server
npm install
npm run typecheck
```

`spacetimedb` 2.14.1 asks for Node 22.23.4+ in its `engines` field. Node 22.20 installs it with a warning and typechecks fine.

## Install the CLI

Follow https://spacetimedb.com/install. (Windows: the PowerShell one-liner on that page. macOS/Linux: `curl -sSf https://install.spacetimedb.com | sh`.)

## Publish to Maincloud

```
spacetime login                       # opens the browser
cd server
spacetime publish -s maincloud rescue-robot
spacetime logs -s maincloud rescue-robot -f
```

- Database name: `rescue-robot` (pick another if it's taken; everyone uses the same one).
- Maincloud HTTP base URL: `https://maincloud.spacetimedb.com`.
- Hackathon credit: redeem the code **`HACKUTD2026`** (from the leaflet QR) on your spacetimedb.com account before publishing. We haven't checked which page you redeem it on.

Republish after a code change with the same command. If the schema change isn't compatible with the existing data (for example a column was removed or retyped), wipe the data:

```
spacetime publish -s maincloud -c on-conflict rescue-robot   # or -c always
```

`init` (seed robot 1, two responder teams, the watchdog schedule) runs only when a database is first created or its data is wiped.

## Local fallback (flaky venue internet)

On a laptop that sits on our own router:

```
spacetime start                       # listens on http://localhost:3000 by default
cd server
spacetime publish -s local rescue-robot
```

For the Pi and the phone to reach it, listen on all interfaces: `spacetime start --listen-addr 0.0.0.0:3000`. Then clients use `http://<laptop-ip>:3000` as the base URL.

## Quick smoke test

```
spacetime call -s local rescue-robot robot_checkin 1 0.5 0.5 90 0.8 searching
spacetime call -s local rescue-robot report_detection 1 2.0 3.0 0.9 "http://pi.local/snap/1.jpg"
spacetime sql  -s local rescue-robot "SELECT * FROM survivor_case"
spacetime sql  -s local rescue-robot "SELECT * FROM takeover_request"
```

## Not verified yet

The CLI isn't installed on the dev machine, so nothing here has been built or published. Only `tsc` has run. Things to check on first publish:

- `spacetime publish` builds this folder as is (`package.json` + `src/index.ts`).
- The watchdog runs every second (`spacetime logs`, or watch a robot go `offline` 5 s after the last checkin).
- `spacetime call` argument syntax (positional, as above).
