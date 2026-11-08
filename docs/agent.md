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

## Operator voice updates

The rescuer wears the phone VR visor and drives the robot, so they can't read a screen. The agent tells them what is happening by voice instead: "Person seen, case 1 at 2.3, 1.1: behind the couch." or "Case 1: red. Responders notified."

**Code.** `agent/src/operator/voice.ts` holds the pure logic (no I/O). `agent/src/operator/feed-server.ts` is a small `node:http` server that streams it. The page that speaks is `agent/web/public/operator.html` (with `operator.js` and the shared `speech.js`). The lead wires `OperatorVoice.push()` next to `pushAlert`.

```ts
const voice = new OperatorVoice();               // options: now, setTimer, dedupeMs, minGapMs, maxQueue, historySize
voice.push({ text, level, source?, caseId?, time? }); // Utterance if said now, else null (empty, repeat or queued)
voice.subscribe((u) => ...);                     // returns unsubscribe; also voice.unsubscribe(fn)
voice.recent(20);                                // last n said, oldest first (50 kept)
const feed = await startOperatorFeed(voice, { host: "0.0.0.0", port: 8788 }); // { url, close() }
```

**What it does to each alert.**
- **Rewritten for the ear.** At most 22 words and 140 characters, cut at a sentence end, then at a clause, then at a word. No words are added. Markdown, links and emoji are removed. `(x, y)` becomes "at x, y". Other parentheses lose only the brackets. Numbers are rounded to one decimal ("2.0" becomes "2"). `RED` becomes "red", `-1.2` becomes "minus 1.2", `°`/`%`/`m` become "degrees"/"percent"/"meters".
- **Approval prompts.** An alert starting with `Approve?` becomes "Approval needed: \<summary\>. Answer on the console." at level warning.
- **Repeats dropped.** The same normalized text within 30 s gives `null`.
- **Paced.** Non-critical lines are at least 4 s apart. Up to 3 wait in a queue, and when it overflows the oldest *info* line is dropped first (the oldest line if there is no info). A timer releases queued lines to subscribers.
- **Critical lines.** They always go out at once with `interrupt: true`, meaning "stop talking and say this".

**Feed contract** (default `http://127.0.0.1:8788`; bind `0.0.0.0` so the phone on the LAN can reach it). Every GET sends `Access-Control-Allow-Origin: *`. Only utterance text is served, never keys or config.

| Route | Returns |
| --- | --- |
| `GET /api/operator/feed` | Server-sent events. Event `say` with `id: <utterance id>` and the Utterance JSON as data. On connect it replays the last 5, or only those after `Last-Event-ID` / `?since=<id>`. Event `ping` `{"time": ms}` every 15 s. |
| `GET /api/operator/recent?n=20[&since=<id>]` | JSON array of the last n utterances (1-50), oldest first |
| `GET /operator` | The earpiece page (also `/operator.js`, `/speech.js`). Nothing else is served from disk. |

```json
{ "id": 7, "text": "Case 1: red. Unresponsive adult, heavy bleeding from the leg.", "level": "critical",
  "interrupt": true, "time": 1762600000000, "source": "set_triage", "caseId": 1 }
```

**Earpiece page (`/operator`).** Open it on the phone. It shows the latest line large, colored by level, with the history below, and it speaks each line through the Web Speech API (an English voice at rate 1.05; critical lines cancel current speech first). It has a "Tap to enable voice" button, because browsers block audio until a gesture, plus mute, volume and rate controls. Settings are saved in localStorage. Lines older than 20 s, such as replays after a reconnect, are shown but not spoken. `?feed=http://<host>:8788` points the page at another server. iOS Safari stops the page and its speech when it goes to the background or the screen locks, so the page asks for a screen wake lock where the browser supports one.

**Scene bench.** Run options now include **Speak updates**, which reads new headset alerts from `state` events with the same rewrite rules, and **Play robot voice**, which plays the robot's `speech` lines in a second, lower-pitched voice. Both are off by default and saved in localStorage. A silent turn says nothing.

**Visor integration (suggested).** The iOS visor app should not depend on the web page:
1. Open the feed with `URLSession` (SSE: read lines, split on blank lines, keep the last `id:`), or poll `GET /api/operator/recent?since=<lastId>` once a second.
2. For each Utterance, if `interrupt` is true call `synthesizer.stopSpeaking(at: .immediate)`, then speak `AVSpeechUtterance(string: u.text)` (en-US voice, rate a bit above default).
3. Set `AVAudioSession` category `.playback` with `.duckOthers` so the voice ducks the video audio and keeps playing with the screen locked.
4. Optionally play a short tone before `critical` lines. Approvals still need an answer on the console or headset UI (`resolve_confirmation`). The voice only says that one is waiting.

**Not verified.** Real speech output and voice choice on iOS Safari and Android Chrome, and how the page behaves in the background on a phone. The tests cover the rewrite rules, dedupe, pacing, the feed (SSE replay, recent, the page) and the browser port matching the TypeScript rules.

## Running on the real robot (`ROBOT_MODE=prada`)

What the agent can and can't do with prada today:

| Capability | How | Notes |
| --- | --- | --- |
| **See** | `GET http://prada.local:8080/frame` (latest JPEG from the camera service) | Frames larger than 170 KB are shrunk in pure JS (`jpeg-js`) before going to Nemotron VL |
| **Speak** | `POST http://prada.local:8081/say` (speech service, Piper/espeak-ng on the Pi) | The scripted questions and reassurances are pre-rendered on the Pi |
| **Listen** | `POST http://prada.local:8081/listen` (USB mic → silence check → whisper.cpp on the Pi) | A missing mic or speech-to-text is reported as **unavailable**, never as "no answer"; the triage decision is told so and won't call someone red for not answering |
| **Move / aim** | Not available | prada is joystick and head-steering only, and has no pan-tilt mount. Those tools refuse with a clear error. For a second look, the workflow asks the rescuer by voice to turn the robot ("Turn the robot toward the person for a better look"), waits `SECOND_LOOK_WAIT_MS`, and grabs a new frame |
| **Start a case** | The visor's YOLO detector → `POST /api/detections` | No autonomous search: the agent waits for detections |

### Inputs: video for the trigger, stills and clips for Nemotron

- **Video:** only YOLO on the phone sees it, at about 15 checks a second.
- **Nemotron:** looks at single frames on demand (at the trigger, for a second look, for the 2-frame movement check, and for re-checks).
- **Audio:** about 5 s is recorded after each question.

### Detection intake (on the agent's feed server, port 8788)

```
POST /api/detections   Authorization: Bearer <AGENT_TOKEN>
{"source":"visor","confidence":0.83,"count":1,"box":null,"frame_time":1762600000000}
→ 202 {"ok":true,"case_id":3,"new_case":true}
→ 200 {"ok":true,"ignored":"active_case"|"cooldown"|"low_confidence","case_id":3|null}
GET /api/status → {"ok":true,"active_case":3|null,"last_sighting_ms_ago":1200|null}
```

- **Sightings:** confidence below 0.5 is ignored. While a case is open, sightings fold into it.
  - If more people appear, the case is re-checked.
  - While the person is still in view, it's re-checked every 45 s, up to 4 times.
- **Closing:** a case closes after 120 s with no sightings. A case judged a false alarm closes with a 30 s cooldown.
- **One case per sighting:** prada has no localization, so each sighting episode becomes one case.
- **The visor side:** `headset/ios/Visor/AgentLink.swift` sends when YOLO sees a person in 3 of its last 5 results at confidence 0.5 or higher, with a 30 s cooldown. It is **not built yet**; see `headset/ios/README.md`.

### Setup on the demo laptop

```bash
# agent/.env
ROBOT_MODE=prada
ROBOT_CONTROL_TOKEN=<from /etc/picrawler-control.env on prada>
AGENT_TOKEN=<long random string, also typed into the visor>
OPERATOR_FEED_HOST=0.0.0.0          # so the phone can reach :8788
CAMERA_URL=http://prada.local:8080  # or the Pi's IP
SPEAKER_URL=http://prada.local:8081
```

1. `npm run prada:check -- --say --listen` checks each link: camera, speech health, Nemotron VL on a live frame, speaker and mic.
2. `npm start` runs the agent. It waits for the visor, or you can type a voice command.
3. Open `http://<laptop>:8788/operator` for spoken updates.

Test the intake without the visor:

```bash
curl -X POST -H "Authorization: Bearer $AGENT_TOKEN" -H "Content-Type: application/json" -d '{"source":"curl","confidence":0.9,"count":1}' http://localhost:8788/api/detections
```
