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
USB/keyboard ownership, stops the glove and motor services, acquires the same
hardware lock, initializes using saved calibration, stands, ramps to a nominal
17-degree backward pitch, rocks three times between 8 and 17 degrees at vendor speed 85, and returns level. It restarts
control disabled. Observe the physical tag: completion does not confirm release.
The installed vendor geometry clips at 18 degrees; 17 is the largest whole-degree
unclipped pose checked, not a measured chassis angle or proof against tipping.
Use level ground with the rear clear. The maintenance sequence cannot be instantly
stopped with dashboard Stop. No autonomous movement is enabled by this test.

### Hello and dance

**Hello / wave** runs the vendor wave once at speed 55. **Dance** runs two short
side-to-side sway and body-bob cycles at speed 60, validating the dance poses
against vendor joint limits first. Both use the same exclusive maintenance path
as the tilt test, return to standing, and restart driving disabled. Hello is a
physical greeting, without speech playback. These sequences cannot be instantly
stopped from the dashboard; leave space around the legs before triggering them.
