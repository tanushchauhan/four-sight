# Native Prada visor

Robot feed mode retains segmentation, range, motion, and the survivor radar. Phone camera mode is
the rescuer HUD: coordinator voice/transcript, real robot systems and mission status, without local
person segmentation. The foreground microphone listens for "Sight" (also accepting the transcription
"site"), followed by a command and a pause. Mute microphone in settings to disable wake listening.
Speech recognition may use Apple's network service. Playback suspends wake capture to prevent echo.

Robot telemetry is read from the coordinator Mac's dashboard at port 8090 `/api/status`; start the
dashboard on that Mac with a LAN-accessible bind address. Battery is displayed in volts, temperature
in Celsius, and unavailable/stale readings are labeled. Signal strength is not exposed by that API.

The visor reports structured person observations (normalized boxes, confidence, mask coverage,
estimated range/bearing and image-motion category) with stable robot-feed detections, at most every
five seconds. Raw mask pixels are not uploaded. The coordinator receives these as provisional evidence,
including for voice questions. Noncritical spoken updates are spaced 12 seconds apart with one pending
update; critical alerts interrupt immediately.

No agent token is required. At build time, the visor automatically uses the build Mac's Bonjour
address (`http://<Mac-name>.local:8788`). Run the agent on that Mac and put the phone on the same LAN.
An optional `AGENT_URL` in `agent/.env` or the build environment overrides this for another server.
Rebuild/install after changing it. The agent defaults to listening on `0.0.0.0`; existing environments
that explicitly set `OPERATOR_FEED_HOST=127.0.0.1` must change that to `0.0.0.0`.
Commands and detections are unauthenticated LAN endpoints; keep port 8788 on a trusted network.
The native visor polls `/api/operator/recent` and speaks recent coordinator updates through the phone
or connected headset. Tap Speak to coordinator, grant microphone and speech recognition access,
then tap Send voice command. Recognition also finishes automatically after 20 seconds. Speech is
transcribed using Apple's speech recognizer (network availability may be required), and only the
transcript is posted to the `/api/operator/command` endpoint. Coordinator speech pauses
while listening. Backgrounding stops recording and playback. Run the updated agent with `npm start`,
`OPERATOR_VOICE=on`, and `OPERATOR_FEED_HOST=0.0.0.0` for LAN access.

Open Visor.xcodeproj, choose your signing team and iPhone, and run Visor.
Connect the phone to the same network as Prada and allow Local Network access.
The default camera URL is http://prada.local:8080/stream. If name resolution fails,
enter http://<Pi-IP>:8080/stream and tap Connect. Hide controls before wearing it.
The address persists between launches. The Pi camera service must be running.

The receiver reads the MJPEG stream directly and keeps only the latest JPEG.
Both eyes show the same image with its aspect ratio preserved and lens distortion applied.
Disconnected streams retry automatically; images older than two seconds are cleared.
Choose Robot feed for the Pi MJPEG stream or Phone camera for local ARKit passthrough.

## Survivor segmentation

The visor bundles the INT8 Core ML export of Ultralytics YOLO26n-seg and uses its COCO `person`
class as the baseline survivor signal. Inference runs on a dedicated queue at up to 15 analyses per
second while the Metal camera and lens renderer remains at 60 FPS. Only the newest completed mask is
shown, so a slow inference never stalls the camera feed. Detected people receive a translucent cyan
segmentation highlight in both eyes, and the HUD shows a survivor count, confidence, and alert.

Both the robot MJPEG feed and the ARKit phone-camera feed use the same detector. The phone path reads
ARKit's camera pixel buffer directly; it does not create a second AVCapture session. Test performance
and mask alignment on a physical iPhone because the simulator has no representative Neural Engine or
camera path. The generic COCO person class is not rescue-scene validation: fine-tune a compatible
segmentation model on prone, obscured, low-light, and partially visible people before treating the
alert as safety-critical.

The lens HUD also reports an approximate distance and horizontal direction for the highest-confidence
person, a moving/stationary/uncertain marker based on short-term image motion, and a breadcrumb that
retains the last relative survivor fix. Distance is a monocular human-height heuristic, motion can be
confused by camera movement, and the breadcrumb is camera-relative rather than a world-map coordinate.
Battery, link quality, and thermal values are visibly marked simulated until robot telemetry is wired.

The lens profile is the Pop-Tech 34 mm biconvex with a 45 mm focal length: the screen is
treated as the focal plane, with the Cardboard V2 distortion pair for that lens diameter.
Inter-lens distance and tray alignment are still the printed viewer's. Check them on the phone.

## Head steering (whole robot)

Update Prada and restart its server and joystick relay. The relay now listens on
TCP 8766 on the private network; the motor server stays on loopback at 8765.
The handset connects to the same host as the camera URL, port 8766.
Head steering connects automatically when the robot camera becomes live. Face forward while it
connects; that pose becomes center. Click the physical joystick to enable motion. With the stick
centered, looking past 18 degrees turns the robot; returning to center stops it.
Joystick movement overrides head steering. Click the joystick again to stop all
motion. Recenter while facing your desired neutral direction. Reverse steering
is available if the physical direction is opposite to your expectation.

Head steering is rate control, not absolute robot-heading tracking. No forward
motion is generated by the headset. Port 8766 no longer authenticates the headset, so keep it on a
trusted private network and firewall it from untrusted clients. A disconnected/stale headset command expires
in 0.3 seconds; loss of joystick still disables motion. Backgrounding the app or
stale video stops head steering. A gait already executing must finish before the
robot stops. Verify turn direction at low speed with space around the robot.

The deployment config now scales gait speed from 20 to 80 instead of a fixed 80;
head steering is capped at 0.4 input (approximately speed 38). Physical speed and
head-to-camera latency have not yet been calibrated.

## Agent trigger

When the on-phone detector stably sees a person on the live robot feed, Visor posts
to `/api/detections` on the laptop agent. Three of five detections must contain a
person at confidence >= 0.5; repeated reports are limited to once every five seconds.
Reports include count, confidence, frame time and structured boxes/observations.
The phone camera never triggers robot handoff. Network retries do not block video.

The configured `AGENT_URL` is bundled by `configure_agent.py`; the fallback uses the
camera hostname with port 8788. Phone LAN intake is token-free. `AGENT_TOKEN` remains
private between the laptop agent and dashboard and is never bundled in the phone.
Run the agent with `ROBOT_MODE=prada` and `OPERATOR_FEED_HOST=::` for the hotspot.

The dashboard admission gate must be armed: **Arm person demo · stationary**, or
person detection enabled during autonomous exploration. A valid sighting stops
exploration, posts one alert, plays one greeting and waits for manual takeover.
Repeated local/Visor detections merge into the same encounter. Rearming begins a
new encounter. The status may show `ignored (mission_disabled)` while manual/disarmed;
this is expected. See `dashboard/README.md` for the demo checklist and live radio.

## Current Prada demo and hands-free coordinator

The current build reads `AGENT_URL` from its generated AgentConfiguration plist;
phone requests do not carry agent secrets. Local dashboard YOLO can trigger the same
person handoff without Visor. Person alerts interrupt hands-free listening, and a
backend feed-ID reset is detected after an agent restart.

In Coordinator, select **Start hands-free conversation**. Grant microphone and speech
recognition access. Pause for about 1.2 seconds to submit; the phone speaks Nemotron's
reply and starts listening again. **End conversation** closes the mic while retaining
handoff alerts. The single-command button remains available. This is automatic turn-taking, not full-duplex barge-in or streamed model audio. Use the dashboard's live
radio for the responder-to-person call, not the coordinator conversation.

Prada's coordinator now uses measured dashboard facts and an explicitly restricted
capability description. It does not receive simulated coordinates/battery percentages,
assume a collapsed building, or dispatch responders from voice chat. Missing information
is represented as unknown. Real-world person handoff uses a short fixed greeting and
waits for a human; the simulation's medical triage workflow is not run on Prada.
