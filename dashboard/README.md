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

1. Click **Take keyboard control**. This pauses `prada-joystick`, including headset steering, and opens a single authenticated connection to the motor server through SSH. The token stays on Prada.
2. Wait for **connected / Disabled**. Press **E** to enable, then hold **W/S** to walk or **A/D** to turn. Key release sends neutral input. Pick 50%, 75%, or 100% input; these are command magnitudes, not measured physical speed.
3. Press **space** or click **Stop** to disable. Losing browser focus, hiding the tab, or missing browser updates for 0.3 seconds also disarms. You must explicitly enable again.
4. Click **Release control** to close the control connection and restore the glove relay if it was running before takeover. An abandoned tab automatically releases after 15 seconds without updates.

The motor server's independent 0.5-second timeout also remains active. Network delay and synchronous vendor gaits mean stop is not instantaneous: one committed gait or initialization must finish. The dashboard starts disarmed, and it never restores enabled motion automatically. If the dashboard crashes, the Pi disables motion on timeout, but you may need `ssh prada sudo systemctl start prada-joystick` to restore the glove relay.

The Prada deployment maximum is now 100 vendor units (previously 80); the dashboard defaults to 50% input. Use the physical servo-power switch if a vendor call gets stuck.
