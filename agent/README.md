# agent/: Nemotron mission agent

This is a team of three Nemotron agents that runs the rescue robot's mission: a **coordinator** (Nemotron 3 Ultra) delegates to a **triage** agent (3.5 Lightning) and a **search** agent (3.5 Bolt). Together they plan the search, take second looks, talk to survivors, triage, request takeovers and text responders, all through tool calls. Design and contracts: [docs/agent.md](../docs/agent.md).

## Run it

Needs Node 22 or newer.

```bash
cd agent
npm install
npm run demo        # offline: simulated robot + mock planner, no key needed
npm test            # 26 tests
npm run typecheck
```

Use real Nemotron:

```bash
cp .env.example .env    # set LLM_MODE=nvidia and NVIDIA_API_KEY=...
npm run demo            # Nemotron makes every decision; robot is still simulated
```

Long-running mode (what runs during the hackathon demo):

```bash
npm start
```

Type a line to act as the rescuer's voice ("status", "go to 5, 4", "come back"). The other commands are `/release`, `/approve N`, `/deny N`, `/sitrep` and `/quit`.

## Switches (`.env`)

| Variable | Offline default | Real |
| --- | --- | --- |
| `LLM_MODE` | `mock` | `nvidia` (+ `NVIDIA_API_KEY`) |
| `AGENT_MODE` | `team` | `single` = one agent with every tool |
| `COORDINATOR_MODEL` / `TRIAGE_MODEL` / `SEARCH_MODEL` | Ultra / Lightning / Bolt | any model your key can use |
| `TOOL_CALL_MODE` | `native` | `json` if native tool calls misbehave |
| `ROBOT_MODE` | `sim` | `pi` (+ `PI_WS_URL`) |
| `STATE_MODE` | `memory` | `spacetime` (+ `SPACETIME_URL`, `SPACETIME_DB`, `SPACETIME_TOKEN`) |
| `NOTIFY_MODE` | `console` | `twilio` (+ `TWILIO_*`, `RESPONDER_PHONES`) |
| `SPEAKER_MODE` | `off` | `pi`: the robot says its lines through the HAT speaker (+ `SPEAKER_URL`, `ROBOT_CONTROL_TOKEN`; Pi service `robot/picrawler_control/speech.py`) |
| `OPERATOR_VOICE` | `on` | spoken updates for the rescuer at `http://<OPERATOR_FEED_HOST>:8788/operator` (set host `0.0.0.0` for a phone on the LAN) |
| `DETECTION_MODE` | `workflow` | `agent` = coordinator decides every detection step |
| `AUTO_CONFIRM` | `approve` | in memory mode only; `none` = wait for `/approve N` |

Every combination works: for example, real Nemotron with the simulated robot, or the mock planner with SpacetimeDB.

## Logs

Each tool call is printed, appended to `logs/agent-<date>.jsonl`, and written to the `agent_log` table.

## Current Prada autonomous integration

The dashboard now owns reactive ultrasonic exploration and manual/USB takeover;
Nemotron does not generate low-level motor commands. `ROBOT_MODE=prada` still
uses the real camera/speaker/microphone adapter and its no-navigation capability
flags. `pi` remains the legacy proposed WebSocket adapter, not this deployment.

With `DASHBOARD_URL` pointing at the dashboard and a shared local `AGENT_TOKEN`,
the agent heartbeats the mission controller. Each qualifying Visor detection must
be admitted by the dashboard (detection enabled, autonomous/person-found mode,
and previous controller released) before a case opens. Latest main makes the
Visor-facing LAN intake token-free; the shared token here is only for the private
agent-to-dashboard stop/heartbeat calls. No secrets go into dashboard JavaScript.

Teammate components reused: Ultra coordinator, Lightning triage, Bolt search,
Nemotron VL image analysis, fixed look/talk/triage workflow, structured YOLO Visor
observations, operator voice/alert feed, and Pi speech/listening. The repository
also contains SpacetimeDB and Twilio adapters plus offline simulation/scene tests.
The current local deployment uses `STATE_MODE=memory` and `NOTIFY_MODE=console`:
no SpacetimeDB persistence or real SMS. SLAM/localization remains out of scope.

Set `OPERATOR_FEED_HOST=::` for both IPv4 and IPv6 LAN clients. Visor's build script
bundles only `AGENT_URL`, defaulting to this Mac's `.local` hostname. Operator
radio is on the dashboard: actual voice clips and robot microphone replies,
with an explicit manual speech session that blocks AI speech during takeover.
