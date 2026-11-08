# Prada LAN status

Run on the Mac with working `ssh prada` access (Python 3.10+; no packages):

```sh
python3 dashboard/server.py
```

Open http://localhost:8090 on the Mac or http://MAC_LAN_IP:8090 from devices on the same LAN. Allow macOS Local Network access for the terminal/app running the server. Status viewing is intended for the private demo LAN. Keyboard control is restricted to this Mac and the dashboard page. To limit access to the Mac, use `--host 127.0.0.1`. Stop with Ctrl-C.

Options: `--robot prada` (SSH alias or user@host), `--port 8090`, `--interval 5`. Each check is followed by the interval; a slow/offline robot can take up to 18 seconds per check. The refresh button fetches the latest cached check, not a second SSH session. Status polling never owns the motor-control connection; explicit debug takeover does. Existing SSH host-key verification remains enabled; verify and accept a new host in a terminal first.

## What is measured

- Robot: a successful SSH probe, uptime, temperature, revision and firmware throttle flags.
- Services: systemd ActiveState for motor server, relay and camera.
- Camera: most recent JPEG returned by `/frame`, refreshed once per probe. This does not prove the underlying sensor is still producing fresh frames.
- Joystick: BlueZ connection state for the project's Arduino (`98:4F:EE:0F:35:0C`). This does not indicate enable state or fresh joystick packets.
- Headset: an established TCP connection on the head-steering port 8766. Authentication/steering readiness is not inferred.

Status probes do not send motor commands, restart services, read the control token, or read battery hardware. Debug takeover is described below. Stale/unreachable telemetry is cleared instead of shown as current. Power flags are not a battery gauge. The dashboard can run without internet, provided the Mac can reach Prada. A Mac on the upstream side of a second Mac's Internet Sharing may not be able to initiate connections into the robot LAN; run this server on the downstream Mac or provide a suitable route.

Checks: `python3 -m unittest discover -s dashboard -p 'test_*.py' -v`.

## Debug keyboard controller

Open **http://localhost:8090** on the Mac running the server. LAN visitors can view status, but only requests originating on this Mac can control the robot (localhost or the Mac’s own LAN IP).

1. Click **Take keyboard control**. This pauses the glove relay and starts a replacement headset listener on port 8766, and opens a single authenticated connection to the motor server through SSH. The token stays on Prada.
2. Wait for **connected / Disabled**. Press **E** to enable, then hold **W/S** to walk or **A/D** to turn. With WASD released, fresh headset turn commands are used instead. Direction keys override the headset. E enables both sources, and space/blur/stale browser updates disable both. Headset commands expire independently after 0.3 seconds. Pick 50%, 75%, or 100% input; these are command magnitudes, not measured physical speed.
3. Press **space** or click **Stop** to disable. Losing browser focus, hiding the tab, or missing browser updates for 0.3 seconds also disarms. You must explicitly enable again.
4. Click **Release control** to close the control connection and restore the glove relay if it was running before takeover. An abandoned tab automatically releases after 15 seconds without updates.

The motor server's independent 0.5-second timeout also remains active. Network delay and synchronous vendor gaits mean stop is not instantaneous: one committed gait or initialization must finish. The dashboard starts disarmed, and it never restores enabled motion automatically. If the dashboard crashes, the Pi disables motion on timeout, but you may need `ssh prada sudo systemctl start prada-joystick` to restore the glove relay.

The Prada deployment maximum is now 100 vendor units (previously 80); the dashboard defaults to 50% input. Use the physical servo-power switch if a vendor call gets stuck.

## Keyboard + headset mode

Keyboard takeover now hosts the same head-steering listener as the glove relay on Prada port 8766. Visor reconnects automatically after the handoff. Press E in the focused dashboard to enable; when no WASD key is held, the latest head turn is used. WASD overrides head input, and space disables both. The browser must remain focused and send updates for either source to move. The panel reports whether the headset is connected and which input source was selected. No iPhone rebuild is required for this dashboard mode.

## Calibrated servo zero

**Move servos to zero** commands all 12 servos to the vendor's initial zero angles with the saved calibration offsets applied. Support the robot before pressing it. It releases keyboard control, stops the glove and motor services, and then uses the same advisory hardware lock and lazy initialization code as the motor server. It does not change calibration offsets. After completion the motor server is restarted disabled, and the glove relay is restored if previously running. Take keyboard control and enable again to resume. A stop failure prevents the maintenance command from running; failures are displayed instead of claiming success.

Hardware-free protocol integration: `PYTHONPATH=robot:dashboard robot/.venv/bin/python -m unittest discover -s dashboard -p 'test_*.py' -v`. This tests head steering, keyboard override, stale head input, and stop against the actual WebSocket bridge without a gait worker or hardware. The servo-zero failure paths are mocked; physical zeroing is initiated only by the operator's button press.

## USB glove through the Mac

The Arduino 101 can connect to the dashboard Mac over USB instead of Bluetooth. The Mac still forwards motor commands to Prada over the LAN; the robot does not need a USB connection to the glove.

Install the optional USB dependency and start with the same Python environment:

```sh
robot/.venv/bin/python -m pip install -r dashboard/requirements-usb.txt
robot/.venv/bin/python dashboard/server.py
```

Flash `controller/joystick_ble` to the Arduino 101 using `arduino-cli`. USB firmware emits `J,sequence,turn,forward,clicks` lines at 20 Hz even with no Bluetooth connection. The dashboard recognizes USB VID/PID `8087:0ab6`, opens the port exclusively, and displays its packet age and connection status. Leave the stick centered when the USB serial port opens; firmware calibrates then.

Click **Use USB glove control**, wait for the robot connection, and click the centered physical stick to enable. Push the stick to move, center it to allow head steering, and click again to stop. Hold the stick click for 1.5 seconds then release to recalibrate; this packet gap disables control, so take USB control again afterwards. Missed clicks cannot enable motion. A controller reset, stale data, excessive serial backlog, or unplug releases USB control; replugging never automatically re-enables it.

USB mode is independent of browser focus because fresh serial packets renew its command lease. The physical click and dashboard **Stop / space** disable movement; keyboard E does not enable USB mode. **Release control / Return to glove** releases the current controller. Select keyboard takeover separately to use WASD again. Only one mode can own the motor server.

### AirTag backward-tilt test

`Test AirTag drop · tilt back` runs one exclusive pose test on Prada. It releases
USB/keyboard ownership, pauses the glove relay, and sends a one-shot request to the running motor server.
The existing hardware owner stands and ramps to a nominal
17-degree backward pitch, rocks three times between 8 and 17 degrees at vendor speed 85, and returns level. It finishes with driving disabled. Observe the physical tag: completion does not confirm release.
The installed vendor geometry clips at 18 degrees; 17 is the largest whole-degree
unclipped pose checked, not a measured chassis angle or proof against tipping.
Use level ground with the rear clear. Dashboard Stop cancels the remaining poses after the current vendor pose transition. No autonomous movement is enabled by this test.

### Hello and dance

**Hello / wave** runs the vendor wave once at speed 55. **Dance** runs two short
side-to-side sway and body-bob cycles at speed 60, validating the dance poses
against vendor joint limits first. Both use the same exclusive maintenance path
as the tilt test, return to standing, and leave driving disabled. Hello is a
physical greeting, without speech playback. Stop or loss of the dashboard heartbeat cancels remaining poses; a committed pose
transition may finish. Leave space around the legs before triggering them.


Gestures reuse the motor server's existing Picrawler instance across requests and
controller connections. They no longer restart `prada-server` or construct a second
hardware owner. Initialization still occurs on the first motion after a fresh
motor-server start. The explicit **Move servos to zero** button retains its
intentional calibration-zero behavior. During gestures, dashboard-to-Pi and
Pi-client-to-motor-server leases must stay fresh; timeout/disconnect disables
movement and cancels subsequent poses. Resume always requires explicit enable.

## Autonomous demo and responder handoff

The mission panel uses local camera-only perception on the dashboard Mac. No NVIDIA
API calls participate in steering, and ultrasonic sensing is not initialized.
YOLO26n detects objects; NVIDIA SegFormer B5 (ADE20K) identifies floor/rug pixels.
B0 misclassified this reflective corridor, while B5 recognized it in stationary tests.
On the M4 Pro, the tested warm B5 segmentation pass took about 184 ms; actual combined
inference and image age are shown in the dashboard and depend on load.

**Local navigation vision** shows a recent annotated frame, three floor-coverage
scores, detections, proposed direction, processing time and image age. These are
image-space estimates, not measured distances or calibrated safety confidence.
The preview runs even while manual/disarmed and cannot command motion by itself.
Green pixels are confidently classified floor with a boundary margin. Object boxes
are inflated and excluded from the candidate paths. Unknown pixels are not free space.

Start remains explicit. Two distinct matching fresh frames establish a direction.
Fresh clear results sustain motion continuously, including while a vendor gait runs;
there is no periodic stop/look cycle. A still-clear current route is retained while
confirming a changed preference. Blocked/unknown routes and results older than 750 ms
command neutral, keep autonomous ownership, and retry. The gait already committed
can finish. Transport faults, explicit Stop, and manual takeover still disarm.
The speed slider scales conservative command magnitudes 0.22–0.30, not measured velocity.
No reverse is requested. Head steering is ignored in autonomous mode.

This is a camera-only demo policy: no SLAM, metric depth, or reliable drop-off protection.
It has not been validated for general navigation; reflections, unseen hazards and model
errors remain limitations. Test movement in a clear, level area with the Stop button ready.
Nemotron remains responsible for conversations and person-assessment workflows.

### Local perception setup

On an Apple Silicon Mac, from the repository root:

```sh
python3 -m venv dashboard/.venv-vision
dashboard/.venv-vision/bin/python -m pip install -r dashboard/requirements-vision.txt
PYTHONPATH=robot:dashboard robot/.venv/bin/python dashboard/server.py
```

The dashboard starts the local perception process automatically when the optional venv
exists. Model weights download on first use into ignored `dashboard/.vision-cache/`.
The model service listens only on `127.0.0.1:8790`, with `/perception` and `/frame` read-only
endpoints; logs are in `.vision-cache/service.log`. Dashboard proxies these to its UI.
If dependencies, camera, or models are unavailable, motion holds; there is no cloud fallback.
For standalone diagnosis: `dashboard/.venv-vision/bin/python dashboard/perception.py`.
The dashboard leaves an independently started perception service running on shutdown.

Validate policies without moving the robot:

```sh
PYTHONPATH=dashboard dashboard/.venv-vision/bin/python -m unittest dashboard/test_local_navigation.py dashboard/test_local_guidance.py -q
PYTHONPATH=robot:dashboard robot/.venv/bin/python -m unittest discover -s dashboard -p 'test_*.py' -q
```

### Person → greeting → responder demo

Use **Arm person demo · stationary** to stop/release the previous controller and
watch without driving. Or enable **Detect people & request responder** during
explicitly started autonomous exploration. Local YOLO requires three distinct fresh
frames with person confidence at least 0.60. Visor can also report sightings; admission
is serialized and both sources fold into the same encounter.

A sighting stops exploration before opening the agent case. The real Prada path posts
one immediate responder/phone alert, says one fixed greeting, and waits for takeover.
It does not infer injuries, interview the person, run simulated dispatches,
or narrate periodic status reports. Nemotron handles grounded operator questions using
fresh dashboard facts; it cannot call action tools in this real-robot demo path.

**Take control & talk** acquires keyboard ownership (initially disarmed) and starts the
live responder radio. Allow the browser microphone, press E to enable, and use WASD.
USB glove takeover remains available. Manual takeover/Stop switches speech to manual,
cancels pending agent speech, and rejects late handoff updates. New sightings cannot
interrupt the human conversation. Re-arm to repeat the demo; each new session permits
one new greeting and alert even when an earlier case is still in memory.

The panel shows Watching → Detected → Greeting → Waiting → Responder control. A speaker
failure remains visible instead of claiming a successful greeting. Rearming clears the
current sighting and filters old alerts from the dashboard. Visor is optional for the
local detector, but still useful for headset video, alerts, and steering.

Take keyboard control or USB glove control to cancel exploration and acquire manual
control, initially disabled. With the USB glove attached, a centered stick click also
requests takeover from autonomous/person-found mode. Wait for the connection, then
click again to enable. A baseline click counter, stale data, or a reconnect never
arms motion. Bluetooth-only takeover still uses the dashboard Return to glove button.

The **Live feed** option proxies Prada `/stream`, the same camera source Visor uses.
It requires a direct Mac-to-Pi route. The camera service now listens on both IPv4
and IPv6, so `CAMERA_URL=http://prada.local:8080` also works over the IPv6 route. The old periodic snapshot remains under device health.

**Start live conversation** stops exploration, pauses Nemotron speech, and streams
16 kHz mono PCM audio both ways between this Mac and Prada. Use `http://localhost:8090`,
allow microphone access, and use headphones to reduce echo. End live conversation closes
the mic and robot audio devices. Browser audio uses a nonce-protected loopback WebSocket
on 8091; the proxy authenticates to the robot speech service's WebSocket on 8082 without
exposing the robot token. Small bounded queues avoid accumulating old audio. This is live
responder radio, not streaming Nemotron conversation. Speech-session changes and disconnects
end the stream. Record/send/listen and typed-message controls remain as fallbacks.

Robot health shows measured battery voltage, not an invented charge percentage.
The motor worker samples the HAT ADC between servo calls and writes a timestamped cache.
Readings older than 15 seconds or from another boot are unavailable.

Configuration: dashboard reads ignored `agent/.env` then process environment.
`ROBOT_CONTROL_TOKEN` is Prada's existing token, `AGENT_TOKEN` is a private dashboard-to-agent shared secret (Visor itself uses
the latest main branch’s token-free LAN intake), `CAMERA_URL` defaults to `http://prada.local:8080`, `SPEAKER_URL` to port 8081,
`DASHBOARD_AGENT_URL` to `http://127.0.0.1:8788`. Agent uses `ROBOT_MODE=prada` and
`DASHBOARD_URL=http://127.0.0.1:8090`. Start dashboard with the robot Python venv and
agent with `cd agent && npm start`. Secrets are never placed in browser responses.
Deploy current motor control/backend/gesture modules, `config.py`, `live_audio.py`, and
`speech.py` to Prada; install/start `prada-speech.service` and camera before testing.
Service startup is disarmed and does not initialize servos.

## Damage & incidents

Open **http://localhost:8090/damage** (or use **Damage & incidents** from live
operations). Run the dashboard and `cd agent && npm start`. The agent needs
`LLM_MODE=nvidia`, `NVIDIA_API_KEY`, and `VISION_MODEL`; both services read the same
`agent/.env` and `AGENT_TOKEN`. Keys never reach the browser.

Enter a survey/property reference and an operator-provided location such as
"Ground floor / kitchen". **Analyze current frame** performs one inspection.
**Start survey** repeats every 20 seconds after each completed inspection;
**Stop survey** stops future inspections (an analysis already underway may finish).
The survey reads the camera and never drives the robot. Surveys start stopped
after an agent restart. Update location labels by stopping and restarting a survey.

Nemotron decides whether visible damage or a hazard merits a record. Normal or
unclear scenes do not automatically become incidents. Model/camera errors appear
in the survey status; no simulated observations or photos are substituted. Each
record contains a saved JPEG, frame retrieval time, robot/snapshot ID, SHA-256,
model ID, visible observations, uncertainties, confidence, and visible-impact
severity. Prada's map position is unknown. The timestamp identifies retrieval of
the camera JPEG, not independently verified sensor exposure time. Identical photo
bytes in the same survey/location are skipped. Images are the same JPEGs supplied
to Nemotron (the robot adapter may resize them).

Records and photos persist in `agent/data/incidents/` when launched from `agent/`;
set `DAMAGE_DATA_DIR` to an absolute path to choose another destination. Data is
ignored by Git. Human review adds notes and a review timestamp without replacing
the AI observations. Only this Mac can start/stop surveys or mark records reviewed;
LAN visitors can view and export, consistent with the local demo dashboard.

**Build report** opens a portable HTML report with embedded photos; use
**Print / save as PDF** in that report. An individual incident has its own report.
**Export evidence JSON** includes every record and its base64 photo. Filters apply
to the on-screen list; the overall report and JSON contain all saved incidents.
This is a human-review evidence packet for an insurance demo, not a State Farm
claim submission, verified damage appraisal, cost estimate, or coverage decision.
