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
- Beacon: explicitly not integrated. No heartbeat exists in its current firmware.

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

The mission panel uses camera-only Nemotron vision guidance (no SLAM or position estimate).
Ultrasonic sensing is not initialized or required. Start is explicit. The speed slider
scales conservative command magnitudes of 0.22–0.30, not measured walking velocity.
The Mac owns one SSH control connection and ignores headset steering in autonomous mode.
While stationary, it requests a fresh camera image and a constrained forward/left/right/wait
assessment. Two consecutive matching movement assessments are required. Confidence below 0.80, malformed output, camera/API failure, and responses
older than eight seconds hold motion and retry without releasing autonomous ownership.
A movement command lasts at most 150 ms, followed by neutral and a new stationary view;
a committed vendor gait can finish after neutral or Stop. This is experimental visual
guidance, not reliable obstacle ranging or drop-off detection. Test on clear, level ground.
The NVIDIA key and vision model come from ignored agent/.env. No model output can directly
execute a tool or arbitrary robot action.

Person detection defaults OFF for an obstacle-only demo. Toggle it while running.
When ON, the local Prada agent must heartbeat within three seconds, or exploration
holds motion and retries while retaining ownership. **Visor must be running its person detector on the live robot feed.** The
agent heartbeat confirms the agent connection, not detector health. Visor already
confirms detections across frames. A qualifying sighting calls the authenticated
dashboard stop endpoint before the agent opens its case; Nemotron verifies camera
images, asks its existing questions, triages, and posts responder alerts. No automatic
resume follows a person, false alarm, takeover, motor connection failure, or Stop.
The panel shows count, confidence, age, estimated bearing/range and image motion.
Fresh structured Visor boxes appear over the feed for at most three seconds and
are labeled delayed; they are not synchronized to the displayed camera frame. Turning detection off blocks new cases/rechecks and robot AI
speech, including an already-running workflow's subsequent lines; alerts already
in progress may still arrive.

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
Deploy `autonomy.py`, current motor control/backend/gesture modules, `config.py`, and
`speech.py` to Prada; install/start `prada-speech.service` and camera before testing.
Service startup is disarmed and does not initialize servos.
