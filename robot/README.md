# PiCrawler joystick bring-up

Initial software milestone for **PiCrawler + Raspberry Pi 5 (4 GB) + Robot HAT v4** on **Ubuntu Server 24.04 LTS arm64**, with ROS 2 Jazzy ros-base. No Pi has been connected, flashed, calibrated, or tested by this work. ESP joystick firmware, video, autonomy and mapping are deferred.

The Python bridge receives normalized joystick commands directly over private local Wi-Fi. A terminal laptop controller substitutes for the future ESP controller. Default mode is hardware-free; vendor modules aren't imported at startup. See [Ubuntu setup](SETUP.md) for the real deployment path and hardware checks.

Our robot, **prada**, is set up and running: see [prada/README.md](prada/README.md) for its one-script install, services, Bluetooth joystick and calibration.

## Run on a laptop first

From the repository root (Python 3.11+, macOS or Linux):

```bash
python3 -m venv robot/.venv
robot/.venv/bin/python -m pip install -e ./robot
export ROBOT_CONTROL_TOKEN='replace-with-a-random-shared-secret'
robot/.venv/bin/python -m picrawler_control.server --config robot/config/control.toml
```

In another terminal, set the same `ROBOT_CONTROL_TOKEN` and run:

```bash
robot/.venv/bin/python -m picrawler_control.controller ws://127.0.0.1:8765
```

`e` enables; `w/s` selects forward/backward; `a/d` selects left/right; space stops and disables; `q` releases and exits. Direction presses produce 0.5 s input pulses (long enough to bridge the OS key-repeat delay). Hold/repeat a key for continued input; OS key repeat may pause between the first and second pulse. The controller sends a drive packet about every 50 ms while enabled, including neutral packets. This is a test controller, not a physical hold-to-run switch. Gaits may outlast a key pulse. A neutral command stops scheduling gaits but leaves control enabled; space requires a fresh `e`.

For Wi-Fi, copy `robot/config/control.toml` to a local file and set `host` to the Pi's private Wi-Fi IP (or `0.0.0.0` to listen on all interfaces). Use the Pi IP in the laptop URI. No cloud service or SpacetimeDB is on this path. The secret authenticates control but `ws://` is unencrypted: use a trusted private router/hotspot, restrict port 8765 to the controller, and never expose it to the internet. A client receives status after authentication and after each accepted packet; unsolicited periodic status is available through ROS only.

## Protocol v1

One authenticated controlling connection at a time. First packet, within 5 s:

```json
{"type":"hello","token":"same-secret-as-ROBOT_CONTROL_TOKEN"}
```

Then send strictly increasing integer `seq` values (0 through 2^53−1), reset for each new connection:

```json
{"type":"enable","seq":0}
{"type":"drive","seq":1,"forward":0.6,"turn":-0.2}
{"type":"drive","seq":2,"forward":0.0,"turn":0.0}
{"type":"stop","seq":3}
{"type":"release","seq":4}
```

`forward`: positive walks forward; negative backward. `turn`: positive turns left; negative right. Axes must be finite numbers in [-1, 1]. Booleans, strings, extra/duplicate keys, binary packets, unknown types and non-increasing sequences are rejected. Maximum WebSocket message size: 1024 bytes. Errors disable and close the connection; reconnect and enable again. `release` disables and closes normally. `look` and `mode` from the broader plan are not implemented here.

Enable clears old axes and starts a fresh command deadline. Send drive at 20 Hz, including neutral while enabled. **No drive packet for 0.5 s disables control**; new drive packets alone cannot re-enable it. Stop, release, disconnect, malformed input and backend errors also disable movement. A backend error is latched until process restart. Enable is rejected while a gait/initialization is still in progress.

Deadzone defaults to 0.15. The larger axis selects a discrete vendor gait; turn wins ties. The bridge does not blend forward and turn into curved walking. Magnitude above the deadzone scales vendor speed from 20 to 50; these are vendor speed units, not m/s. Config validates limits (max permitted speed 100, the vendor maximum; the vendor still caps servo rate at 428 deg/s). Decreasing speed makes a gait take longer; it does not necessarily make stopping quicker.

Status example:

```json
{"type":"status","connected":true,"enabled":false,"forward":0.0,"turn":0.0,"seq":3,"reason":"stop","fault":null,"busy":true,"action":"forward","backend":"dry-run"}
```

`busy: true` with `enabled: false` means a committed synchronous operation is still finishing. An acknowledgement confirms software state, not that the robot has physically stopped. Dry-run logs contain the selected gait, speed and `step=1`.

## Motion and stop limits

- One worker owns hardware and calls only `forward`, `backward`, `turn left`, `turn right`, always `step=1`. Network input can never supply vendor action names (the vendor uses an internal `eval` lookup).
- A single slot retains the latest valid axes. There is no gait queue. An independent watchdog checks the monotonic receipt deadline every 20 ms by default, even while gait execution is blocked. Normal operating-system scheduling can delay this check.
- Selecting `--hardware` acquires a shared advisory lock at `/run/lock/picrawler-control.lock`. The first **enabled, valid, non-neutral drive** triggers vendor initialization. Construction resets the MCU and moves all twelve servos to the vendor initial angles, using stored offsets. Neither starting the server nor sending enable or neutral performs this initialization. It is not a motion-free arm sequence.
- Vendor initialization and one committed gait cannot be interrupted by this adapter. After initialization it rechecks enable, timeout, latest axes and stop generation before starting a gait. A stop racing with the final decision can still leave that one committed call to finish. No subsequent gait is scheduled until explicitly enabled again.
- The bound is **one outstanding vendor call**, not a guaranteed number of milliseconds. Gaits contain multiple pose transitions and can take several seconds, particularly at low speed or first stand-up. Linux scheduling, I2C faults and vendor retry/silent-failure behavior prevent an honest hard wall-clock stop guarantee. Dry-run durations do not predict physical durations.
- Stop holds the last commanded pose: it does not sit, center, release PWM, reset the HAT or cut power. Servos can remain energized even after process exit. Shutdown waits for the worker to return and keeps the ownership lock meanwhile; a stuck call can hang shutdown. Have a reachable physical servo-power cutoff during bring-up. Software status is not an emergency stop or measured motion feedback.
- The lock coordinates instances of this program only. Do not run vendor examples, calibration tools or other servo processes concurrently. All deployments must use the same lock path and account/permissions.
- Sequence numbers prevent replay within a session; they do not detect all network-delayed packets. The deadline uses server receipt time, not client time. There is no real-time network guarantee. The laptop waits for each reply before sending again, limiting its outstanding packets; future clients should do the same.

## ROS 2 Jazzy integration

Source Jazzy before starting the same process with `--ros`:

```bash
source /opt/ros/jazzy/setup.bash
robot/.venv/bin/python -m picrawler_control.server --ros --config robot/config/control.toml
ros2 topic echo /picrawler/status
ros2 topic echo /picrawler/joystick
```

The node `picrawler_control` publishes at 10 Hz:

| Topic | Message | Meaning |
| --- | --- | --- |
| `/picrawler/status` | `std_msgs/msg/String` | JSON status matching WebSocket, including backend, enabled, fault, busy |
| `/picrawler/joystick` | `geometry_msgs/msg/Vector3` | Latest normalized input: x=forward, z=turn, y=0; zero when disabled |

ROS is telemetry in this milestone, not a second motion authority. There is no `/cmd_vel` subscriber or physical velocity/odometry claim. The WebSocket validates and enables commands in the same process as the hardware owner, avoiding competing command paths. `rclpy` comes from Jazzy apt packages, not pip. `package.xml`, resource registration and setup files also support `colcon build --symlink-install --packages-select picrawler_control` from a workspace containing this package. With a colcon install use `ros2 run picrawler_control picrawler-server --ros ...` and ensure the pinned WebSocket dependency is installed for the build's Python interpreter. The direct virtual-environment invocation above is the initial deployment path.

## Checks

```bash
robot/.venv/bin/python -m unittest discover -s robot/tests -v
robot/.venv/bin/python -m compileall -q robot/picrawler_control
```

The 22 tests cover validation, axis mapping, replay rejection, deadman expiry, explicit re-enable, single ownership, lazy initialization, backend faults, latest-command replacement and simulated WebSocket auth/disconnect/stop under a blocked gait. They also check actual dry-run process startup/shutdown and the ROS adapter contract using fake ROS modules. They require loopback sockets and never import the actual vendor library or actuate servos. ROS runtime, Ubuntu Python 3.12 and physical hardware still require on-Pi validation; local checks ran on macOS Python 3.14.
