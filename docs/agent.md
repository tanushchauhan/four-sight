# Nemotron agent

The code is in `agent/`; to run it, see `agent/README.md`. This page covers what the agent does, how it is built, and the contracts the robot and headset teams need to build against.

> **Status (Nov 8):** The agent runs end to end with **real Nemotron models** against a simulated robot, real photos and AI-generated evolving scenes (see `docs/scene-bench.md`). Detection now runs as a fixed workflow with Nemotron at the judgment points, with an offline fallback and movement/heat evidence. 58 tests pass. Nothing has been connected to the Pi, the headset or a deployed SpacetimeDB yet. The Pi is joystick-only, so the agent can't drive it; its camera can be read at `GET /frame`. The parts marked **unverified** below are code written to the agreed contract but never run against the real thing.

## What it is

With one robot, Nemotron is not a fleet dispatcher. It is a **team of three Nemotron agents** that runs the robot's mission, so the rescuer can do other work.

| Agent | Model (default) | Job | Tools it can see |
| --- | --- | --- | --- |
| **Coordinator** (lead) | `nemotron-3-ultra-550b-a55b` | Gets every event and decides what happens. It delegates robot work and owns everything that involves people: takeovers, SMS, dispatch, beacon, ranking, alerts, return home | read tools, `request_takeover`, `set_priority`, `notify_responders`, `dispatch_responders`, `drop_beacon`, `push_headset_alert`, `return_to_start`, **`delegate_triage`**, **`delegate_search`** |
| **Triage** | `nemotron-3.5-lightning-30b-a3b` | Takes one case. It looks, takes second looks from other angles, talks to the survivor, and writes the triage card | camera, vision, `speak`, `listen`, movement, `set_triage`, `mark_hazard` |
| **Search** | `nemotron-3.5-bolt-e4b` | Picks new ground, walks there, does one sweep, and maps hazards | coverage, movement, `sweep_360`, `mark_hazard` |
| *(vision, called as a tool)* | `nemotron-3.5-super-vl-120b-a12b` | Reads a camera frame and returns triage JSON | n/a |

The biggest model does the planning. The fast models run the hands-on loops, where speed matters more. Each agent has its own prompt (`ROLE: …`) and only sees its own tools; the registry enforces that.

Nobody chats with it. Changes in state (a new detection, low battery, a case that has waited too long, the rescuer's voice) become **events**. Each event runs one **episode** that starts at the coordinator. A delegation runs the specialist's own multi-step loop and returns its result to the coordinator as a tool result:

```
event ─▶ COORDINATOR ──delegate_triage(case)──▶ TRIAGE agent ──▶ camera / vision / speaker / mic / set_triage
            │   ▲                                   (own loop, own model)
            │   └────── {case: {triage: "red", …}, summary} ◀──┘
            ├──delegate_search()──▶ SEARCH agent ──▶ coverage / navigate / sweep_360 / mark_hazard
            │   ▲                                   (stops after one sweep)
            │   └────── {new_case_ids, percent_searched, area_complete} ◀──┘
            └──▶ request_takeover / notify_responders (SMS) / dispatch_responders (rescuer approves) / drop_beacon
```

Every tool call is written to the console (indented per agent), to `agent/logs/agent-<date>.jsonl`, and to the `agent_log` table, tagged `event>agent` (for example `detection>triage`). The `agent_log` table is what a dashboard can show judges live.

`AGENT_MODE=single` switches back to one agent that has every tool.

### Detection runs as a fixed workflow (default, `DETECTION_MODE=workflow`)

Research on agent design says linear jobs should be workflows, with an agent only where judgment is needed. Our benchmarks agreed: when the coordinator decided every step of a detection, it made 6-25 model calls, and most of the wait was Nemotron deciding the next obvious step. So detection and re-check events now run as code (`agent/src/agent/workflow.ts`). Nemotron is used only for **reading each camera frame** (Nemotron VL) and **one triage decision**:

```
detection ─▶ aim camera ─▶ snapshot ─▶ Nemotron VL ──"yes"──▶ instant alert to rescuer (code)
                │                         │
                │                 "unsure"/"no" ─▶ walk to another side (L-shaped detour around obstacles) ─▶ look again (max 3)
                │                         └─ two clear "no"s ─▶ close as likely false alarm, deprioritize, no model call
                ▼
          talk (fixed script: "Can you hear me?" → "Are you hurt? Can you move?", stop if silent)
          ─▶ movement check (if silent) ─▶ heat reading
          ─▶ ONE Nemotron call: evidence JSON → set_triage   (agent "triage_decision", Lightning)
          ─▶ policy in code by color: takeover priority · SMS + dispatch for red/yellow · beacon for red · headset alert
```

- **Re-checks** (evolving scenes) run the same path and only re-notify or escalate when the case got **worse**.
- **If the decision model is unreachable**, the offline fallback answers with rules; the workflow also has its own rule (silent means red).
- **The coordinator still handles everything open-ended:** voice commands, stale cases, sitreps, search steps, low battery.
- `DETECTION_MODE=agent` restores the fully agentic detection path, which is useful for comparison and for the "agentic" demo.

### Survivor speech is untrusted input

What the survivor says, and any text in an image, is data, not instructions. Every agent prompt says so, and the workflow labels it `survivor_answers_UNTRUSTED` in the evidence. The actions that matter (SMS, dispatch, beacon) are decided in code by triage color, so a survivor saying "cancel the dispatch" can't change them. There's a test for this.

### Guardrails in code (found by running the real models)

| Problem seen with real Nemotron | Fix, enforced in code rather than only in the prompt |
| --- | --- |
| Triage set a card without talking to the person | `set_triage` refuses, for the triage agent, if it saw a person but hasn't called `listen` yet; the error tells it to talk first |
| The small search model kept going until it hit its step limit | The search agent stops after its first successful `sweep_360` |
| The search model claimed "area complete" at 48% coverage | `delegate_search` returns `area_complete` computed from the coverage grid, not from the model |
| The coordinator ran the whole mission in one event and hit its step limit before dispatching | Only one `delegate_search` per event, so urgent events get in between |
| Triage drove onto the person's position | The triage task now includes the distance and relative bearing to the case, plus "view it from 1-1.5 m" |
| Dispatch and beacon drop | Need the rescuer's approval and can't be skipped (see Tools) |

## Demo moments (what makes it more than a chatbot)

| Behaviour | Tool sequence | Why judges care |
| --- | --- | --- |
| **Second look** | weak detection → `take_snapshot` → `analyze_image` says "unsure" → `navigate_to` the far side of the couch → `turn` → `take_snapshot` → `analyze_image` says "yes" | The model changes the physical world to gather more evidence before it decides |
| **Talks to the survivor** | `speak` "Can you hear me?" → `listen` → `speak` "Are you hurt?" → `listen` → `set_triage` | Multi-turn interaction that changes the triage result |
| **Triage → action** | `set_triage` → `request_takeover` (priority from color) → `notify_responders` (real SMS) → `dispatch_responders` (rescuer approves) → `drop_beacon` | Calls real outside APIs, with a human approving the risky steps |
| **Search planning** | `get_coverage` → `navigate_to` the nearest unsearched cell → blocked → detour → `mark_hazard` → `sweep_360` | Plans coverage on its own and maps hazards as it goes |
| **Self-care / escalation** | low battery → `push_headset_alert` + `return_to_start`; a stale case → `set_priority` + alert; sitrep on a timer | Runs unattended |

The offline demo (`npm run demo`) shows all of these with no hardware and no API key.

## Code map

```
agent/src/
  main.ts            long-running agent; stdin lines stand in for the rescuer's voice
  demo.ts            offline end-to-end run with a simulated rescuer
  config.ts          env → Config (see agent/.env.example)
  wiring.ts          picks sim/pi, memory/spacetime, mock/nvidia, console/twilio from config
  types.ts           domain types (mirror the SpacetimeDB tables)
  agent/
    events.ts        event kinds, priority queue (dedupes)
    runner.ts        watchers that turn state into events; runs one episode at a time
    loop.ts          runAgent (the multi-step tool loop for any agent) + runEpisode
    team.ts          coordinator / triage / search specs, toolsets, delegate_* tools
    prompts.ts       one system prompt per role (+ solo)
    logger.ts        console + JSONL + agent_log
  llm/
    nemotron.ts      OpenAI-compatible client for build.nvidia.com; native tools or JSON fallback
    vision.ts        Nemotron VL triage of a frame; SimVision for simulated frames
    mock-planner.ts  rule-based offline stand-in for the model; ScriptedModel for tests
  tools/
    registry.ts      schemas, argument checks, rescuer-approval gate, errors returned to the model
    index.ts         the 23 tools
  adapters/
    robot.ts         RobotAdapter interface
    sim-robot.ts     simulated PiCrawler in an 8×6 m collapsed room (couch, shelf, hole, 2 people)
    pi-robot.ts      WebSocket client for the Pi (unverified)
    state.ts         StateStore interface
    memory-state.ts  in-process copy of the SpacetimeDB rules
    spacetime-state.ts  SpacetimeDB HTTP client (unverified)
    notifier.ts      console printer and Twilio SMS
```

## Tools

| Group | Tool | Notes |
| --- | --- | --- |
| Read | `get_robot_state`, `get_sensor_readings`, `get_coverage`, `get_cases`, `get_responders` | `get_coverage` returns % searched, the nearest unsearched cells and the hazards |
| Perception | `point_camera`, `take_snapshot`, `analyze_image`, `listen` | `analyze_image` calls the vision model and returns `person_visible`, confidence, injuries, suggested triage and a `next_look` hint |
| Movement | `move`, `turn`, `navigate_to`, `sweep_360`, `return_to_start` | Moves stop early at obstacles, drops or low battery and report why; `sweep_360` turns detections into cases |
| Survivor | `speak`, `set_triage`, `drop_beacon`* | |
| Coordination | `request_takeover`, `set_priority`, `mark_hazard`, `dispatch_responders`*, `notify_responders` | `notify_responders` sends SMS through Twilio |
| Human | `push_headset_alert` | Sitreps and anything urgent |

\* **Rescuer approval:** the registry wraps these tools and the model cannot skip it. It writes a `confirmation` row, pushes an alert, and waits up to `CONFIRM_TIMEOUT_MS`. If the rescuer denies it or doesn't answer, the model gets `{status: "denied" | "expired"}` back and carries on.

Tool failures (bad arguments, an unknown case, a robot error) come back to the model as `{error: ...}` so it can recover. They never crash the episode.

## Events

| Event | Fired by | Priority |
| --- | --- | --- |
| `detection` | a new case with triage `unknown` | highest |
| `voice_command` | the rescuer speaks (stdin for now) | |
| `low_battery` | battery < 20%, once | |
| `robot_stuck` | (hook only; nothing fires it yet) | |
| `stale_case` | a case waiting `STALE_CASE_MS` without responders | |
| `operator_released` | the rescuer hands control back | |
| `mission_start` | startup | |
| `sitrep_due` | every `SITREP_INTERVAL_MS` | |
| `search_step` | idle, robot `searching`, mission not complete | lowest |

## Models

All of them go through `https://integrate.api.nvidia.com/v1` (OpenAI-compatible) with one `NVIDIA_API_KEY`. I tested each with our key on Nov 8 using a single tool-call request:

| Model | Works with our key | Native tool call | Latency | Used for |
| --- | --- | --- | --- | --- |
| `nvidia/nemotron-3-ultra-550b-a55b` | ✅ | ✅ | ~2.7 s | coordinator (`COORDINATOR_MODEL`), single mode (`REASONING_MODEL`) |
| `nvidia/nemotron-3.5-lightning-30b-a3b` | ✅ | ✅ | ~1.3 s | triage (`TRIAGE_MODEL`) |
| `nvidia/nemotron-3.5-bolt-e4b` | ✅ | ✅ | ~0.3 s | search (`SEARCH_MODEL`) |
| `nvidia/nemotron-3.5-super-vl-120b-a12b` | ✅ | n/a | ~3.6 s per frame | vision (`VISION_MODEL`); tested on a real JPEG |
| `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` | ✅ | ✅ | ~3.1 s | spare |
| `nvidia/nemotron-3-super-120b-a12b`, `nvidia/nemotron-nano-3-30b-a3b`, `nvidia/llama-3.1-nemotron-ultra-253b-v1` | ❌ 404 for our account | | | n/a |

- To see what your key can use, query the `/v1/models` endpoint. A 404 saying "Function … not found for account" means that model isn't available to your account.
- If native tool calls misbehave, set `TOOL_CALL_MODE=json`: the model replies with a strict JSON object and we run the tools ourselves.
- Simulated frames have no pixels, so `analyze_image` uses `SimVision` for them. Real frames (`imageBase64` or an `http(s)` URL) go to Nemotron VL.

## Contracts for the other teams

### Pi command protocol (proposal; the robot team implements this on the Pi's existing WebSocket)

```json
agent → Pi  {"type":"agent_cmd","id":7,"cmd":"move","args":{"meters":0.5}}
Pi → agent  {"type":"agent_result","id":7,"ok":true,"result":{ ... }}
            {"type":"agent_result","id":7,"ok":false,"error":"obstacle sensor offline"}
```

| cmd | args | result shape (`agent/src/adapters/robot.ts`) |
| --- | --- | --- |
| `get_state` | | `RobotState` `{id,name,mode,x,y,heading,battery,pan,tilt,lastSeen}` |
| `get_sensors` | | `{frontCm, dropCm, heat}` (null when the sensor is missing) |
| `move` | `meters` (±3) | `MoveResult` `{ok, movedM, stoppedBy?, state}` |
| `turn` | `degrees` (+ = left) | `RobotState` |
| `navigate_to` | `x, y` (m, map frame) | `MoveResult` |
| `sweep_360` | | `{detections:[{x,y,bearingDeg,distanceM,confidence}], notes:[], state}` |
| `point_camera` | `pan, tilt` | `RobotState` |
| `take_snapshot` | | `Snapshot` with `imageBase64` (JPEG) or an `http(s)` `url` |
| `speak` | `text` | `{ok}` (TTS on the HAT speaker) |
| `listen` | `seconds` | `{transcript}` (STT on the Pi or the laptop; `null` = silence) |
| `drop_beacon` | | `{ok, reason?}` |
| `return_to_start` | | `MoveResult` |
| `set_mode` | `mode` | `RobotState` |

Units: meters, and degrees with 0 = +x, counter-clockwise positive. The Pi must refuse to move in `manual` mode and keep its own obstacle and drop safety stops; the agent never overrides them.

### SpacetimeDB

See `docs/spacetime.md` (module in `server/`). The agent uses `set_triage`, `set_priority`, `request_takeover`, `dispatch_responders`, `mark_hazard`, `push_alert`, `request_confirmation`, `log_agent_action`, and reads every table with SQL. With `ROBOT_MODE=sim STATE_MODE=spacetime`, the sim robot also calls `robot_checkin`.

### Headset

The headset needs to show:
- `alert` rows (agent messages, read aloud if possible);
- the triage card from `survivor_case`;
- pending `confirmation` rows with approve/deny buttons that call `resolve_confirmation(id, approved)`.

Until speech-to-text exists, voice commands are typed into `npm start`.

## Not done yet

- [x] Run with a real `NVIDIA_API_KEY`: model IDs checked, native tool calls work, the full mission ran with the agent team
- [ ] Real camera frames from the Pi so `analyze_image` uses Nemotron VL instead of the sim stand-in
- [ ] Publish `server/` and run with `STATE_MODE=spacetime` (check the SQL value decoding)
- [ ] The robot team implements `agent_cmd` on the Pi; then try `ROBOT_MODE=pi`
- [ ] Speech-to-text for rescuer voice commands (headset mic → `voice_command`) and for `listen`
- [ ] Fire `robot_stuck` (commanded motion with no change in pose or ultrasonic reading)
- [ ] Dashboard that shows `agent_log`, the coverage grid and cases (the NVIDIA judges look at this)
- [ ] Twilio trial account and verified responder numbers (`NOTIFY_MODE=twilio`)

## Offline / degraded mode

The NVIDIA hosted API is a single point of failure: in a disaster zone the uplink drops, and under load it returns HTTP 429 or 5xx, or answers very slowly (one vision call in our benchmark took 52 s). `src/llm/fallback.ts` keeps the agent working when that happens.

- `ResilientChat(primary, fallback, opts)` wraps `NemotronChat` with `MockPlanner` as the fallback. Every Nemotron call has a timeout (default 25 s). If a call throws or times out, that same call is answered by the fallback, so the agent loop never stalls.
- Circuit breaker: after `failureThreshold` (default 2) consecutive failures the wrapper goes `degraded` and sends every call straight to the fallback for `cooldownMs` (default 60 s). After the cooldown, the next call tries Nemotron again; if it answers, the wrapper is `healthy` again; if not, the cooldown restarts.
- `ResilientVision(primary, opts)` does the same for `analyze_image` (default timeout 20 s). While vision is down it returns `personVisible: "unsure"`, `confidence: 0`, `suggestedTriage: "unknown"`, `source: "fallback"`. It never reports a person it cannot see, so the agent treats every sighting as unconfirmed and keeps asking for a human check.
- A shared `Health` object collects both wrappers' states. The app shows one "Nemotron: healthy/degraded" status (`health.mode`, `health.onChange`). `onModeChange` fires once per transition.
- Switching models in the middle of a run is safe. Both models read the same plain `ChatMessage[]` history, and `MockPlanner` rebuilds its state from that history.

The fallback is rule-based, not AI. In degraded mode the robot follows a fixed, predictable policy for each role (sweep, look again, talk, ask for takeover, alert responders) and keeps its hardware safety stops. Rescuers can predict what it will do, and it cannot hallucinate. A small local LLM would make more mistakes exactly when nobody can check them, so we use fixed rules until Nemotron is back.

## Triage evidence: movement and heat

Triage from one photo is weak. On the DARPA Triage Challenge, one team went from 14% to 53% accuracy by combining several signals instead of trusting a single image. Until now our triage used one photo plus whatever the survivor said. Two more pieces of evidence are now available:

- **Movement across frames: `check_movement {seconds?: 1-5, default 2}`.** The tool takes a snapshot, waits `seconds`, takes a second snapshot (both are saved in `ctx.snapshots`), and asks the vision model whether the person visibly moved: chest or breathing, limbs, head, eyes. It returns `{movement: "yes"|"no"|"unsure", detail, confidence, snapshot_ids: [a, b]}`. Use it on a visible person who does not answer, to tell someone unresponsive but breathing (red) from someone showing no signs of life. The wait is skipped for `SimRobot`, `SceneRobot` and `NODE_ENV=test`. If the analyzer can't compare frames, the tool returns `unsure` / "movement check not supported".
  - `VisionAnalyzer.compareFrames?(a, b): Promise<MovementResult>` is optional and is exported from `src/llm/vision.ts` together with `MovementResult` and `unsupportedMovement()`.
  - `NemotronVision` makes **one** chat call that carries both frames as two `image_url` parts and gets back JSON `{movement, detail, confidence}`. It uses the same `enable_thinking:false` and `withRetry` setup as `analyze`.
  - `SimVision` answers from ground truth (`SimScene.moving`). In the sim world the yellow survivor behind the couch has `moving: true` and the red survivor by the shelf has `moving: false`. Scene frames can set an optional `moving` flag, and when it is missing the answer is `unsure`.
  - `AutoVision` sends the pair to the real model only when **both** frames have pixels. Otherwise it uses the sim.
- **Heat.** `get_sensor_readings` now describes `heat` as a relative 0-1 body-heat cue: higher means a warmer body nearby, and `null` means the robot has no heat sensor. The scene bench always reports `null`. Heat only supports other evidence and is never proof of life or death.
- The tool is in `ALL_TOOLS` but has **not** been added to any agent's toolset yet; that is done in `team.ts`.

**Real-API check (Nov 8, `nvidia/nemotron-3.5-super-vl-120b-a12b`, thinking off).** The model accepts two images in one user message. Each call took about 2-2.6 s:

| Frame A → frame B | Answer | Latency |
| --- | --- | --- |
| `gen-deteriorating-1` → `-2` (propped up and alert → face down, eyes closed) | `yes`, 0.95 ("changed from propped up on elbows ... to lying face down") | 2.2 s |
| `gen-deteriorating-2` → same image | `no`, 0.90 ("same prone position, no visible chest, limb, head or eye movement") | 2.6 s |
| `gen-deteriorating-2` → `-3` | `yes`, 0.90 (hand moved from under the cheek; the two generated images really do differ) | 2.0 s |
| `photo-unresponsive-01` → same image | `no`, 0.90 | 2.4 s |

**Limits.**
- A two-frame check a couple of seconds apart is a weak proxy for breathing. Shallow breathing, a blanket, a bad angle or low light can all hide motion, so `no` does not mean "not breathing".
- Camera shake, dust, smoke or the robot moving can make a frame look like the person moved, so `yes` can be a false positive.
- The robot has to stay still between the frames.
- The result is evidence for a *provisional* triage card, alongside the photo, the voice replies and heat. It is not a diagnosis, and the agent must never mark someone black from this check alone.
