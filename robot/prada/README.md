# prada

prada is our PiCrawler: Raspberry Pi 5 Model B (4 GB), SunFounder Robot HAT v4, Ubuntu Server 24.04.5 LTS (arm64). This folder holds everything specific to it, so a fresh SD card can be brought back with a clone and one script.

| | |
| --- | --- |
| Hostname | `prada` (mDNS: `prada.local`, via avahi) |
| User | `liquidiv` (no hyphen) |
| Wi-Fi | phone hotspot `liquid-iv-iot` |
| Repo checkout | `/home/liquidiv/hackutd26` |
| Vendor sources | `~/vendor/robot-hat`, `~/vendor/picrawler`, `~/vendor/raspi-utils` |
| HAT on I2C | bus 1, address `0x14` |
| Battery at bring-up | 7.84 V |

## Everyday use

Everything on prada goes through one script. You never edit files on the Pi: change the repo on a laptop, push, then on the Pi:

```bash
~/hackutd26/robot/prada/prada.sh update     # pull, reinstall, restart if it was running
~/hackutd26/robot/prada/prada.sh start      # start the server and joystick relay
```

| Command | Does |
| --- | --- |
| `update` | `git pull`, rerun `install.sh`, restart the services if they were running |
| `install` | Set up or repair everything (add `--enable` to start both services at boot) |
| `start` / `stop` / `restart` | Control the server, joystick relay and camera stream together |
| `status` | Service state, repo commit, HAT on I2C, battery (when stopped), last joystick events, camera URL |
| `logs` | Follow both services' logs |
| `keyboard` | Stop the joystick relay so a laptop keyboard controller can connect |

None of these move servos. The robot moves only after the joystick is clicked and pushed.

## Files

| File | What it is |
| --- | --- |
| `prada.sh` | The one entry point above |
| `install.sh` | Idempotent setup: packages, pinned vendor libraries, `pinctrl`, I2C/GPIO access, venv, calibration restore, token, services |
| `prada.toml` | Bridge config: loopback only, every gait at speed 80 |
| `picrawler.config` | Saved calibration offsets (copied to `~/.config/.picrawler.config` if missing) |
| `system/prada-server.service` | Bridge server in hardware mode |
| `system/prada-joystick.service` | Bluetooth relay from the Arduino 101 joystick |
| `system/prada-camera.service` | Camera MJPEG stream on port 8080 |
| `build-camera.sh` | Builds Raspberry Pi libcamera and rpicam-apps (run by `install.sh`) |
| `system/60-picrawler-gpiomem.rules` | Gives the `gpio` group the Pi 5 GPIO memory windows that `pinctrl` needs |
| `system/picrawler-lock.conf` | Recreates `/run/lock/picrawler-control.lock` at boot |
| `system/i2c-dev.conf` | Loads the I2C device driver at boot |

## Fresh install

1. Flash Ubuntu Server 24.04 LTS (64-bit) with Raspberry Pi Imager: hostname `prada`, user `liquidiv`, Wi-Fi `liquid-iv-iot`, SSH on with your key.
2. SSH in, clone, install, reboot once for the new groups, then start:

   ```bash
   git clone git@github.com:tanushchauhan/hackutd26.git ~/hackutd26
   ~/hackutd26/robot/prada/prada.sh install
   sudo reboot
   ~/hackutd26/robot/prada/prada.sh start
   ```

   The repo is private, so the Pi pulls with its own read-only deploy key (`~/.ssh/id_github_deploy` on prada, added under the repo's Settings, Deploy keys). On a new card, make a new key with `ssh-keygen -t ed25519 -f ~/.ssh/id_github_deploy -N ""` and add the `.pub` there.

## Driving

**Arduino 101 joystick** (`controller/joystick_ble`): flash it once from a laptop (needs Rosetta on Apple Silicon, since the Intel Curie toolchain is x86 only):

```bash
arduino-cli core install Intel:arc32
arduino-cli compile --fqbn Intel:arc32:arduino_101 controller/joystick_ble
arduino-cli upload --fqbn Intel:arc32:arduino_101 -p /dev/cu.usbmodem* controller/joystick_ble
```

Grove wiring: thumb joystick on A0 (X on A0, Y on A1), buzzer on D3. It advertises over Bluetooth LE as `prada`, and the relay on the Pi finds it by service UUID. It re-centers each time it connects, so leave the stick alone while it connects.

| Buzzer | Meaning |
| --- | --- |
| Three quick beeps | Joystick connected to the Pi (it re-centers then, so don't touch the stick) |
| One long beep | Enabled |
| Two short beeps | Stopped |
| Two long beeps | Enable refused: the stick doesn't read centered |
| Two short, one long | Re-centered |

Click the stick to enable, push to walk or turn, click again to stop. Enable only works when the stick reads centered, so a bad center can't make the robot move by itself. If you get two long beeps, let go of the stick, **hold the click for 1.5 s** and release to re-center, then click again.

If the stick drives the wrong way, add `--invert-forward` or `--invert-turn` to `ExecStart` in `prada-joystick.service`.

**Laptop keyboard** (for testing). The server only listens on the Pi's loopback, so tunnel to it. Stop the joystick relay first, since the server takes one controller at a time:

```bash
ssh prada hackutd26/robot/prada/prada.sh keyboard
ssh -N -L '[::1]:18765:127.0.0.1:8765' prada &
ROBOT_CONTROL_TOKEN=... robot/.venv/bin/python -m picrawler_control.controller 'ws://[::1]:18765'
```

Get the token with `ssh prada sudo cat /etc/picrawler-control.env`.

## Camera

Camera Module 3 (Sony IMX708, with autofocus) on **CAM0**. Ubuntu's libcamera 0.2 can't drive the Pi 5 camera pipeline, so `build-camera.sh` builds Raspberry Pi's `libcamera` and `rpicam-apps` (pinned tags) into `/usr/local`; it skips itself once `rpicam-hello --list-cameras` sees the sensor.

`prada-camera` runs `stream.py --camera`, which reads `rpicam-vid --codec mjpeg` at **120 fps** (768x432 from the IMX708's 1536x864 mode, JPEG quality 60, about 20 Mbit/s on the hotspot, continuous autofocus, rotated 180 degrees for prada's mount). The service log prints the measured fps every 10 s and publishes each JPEG to the stream's `FrameBuffer`. It listens on IPv4 (`--host 0.0.0.0`) so the visor on the hotspot can reach `http://<prada-ipv4>:8080/stream`; the control socket stays on loopback. A Mac on the same hotspot often has only IPv6, so view it through a tunnel instead:

```bash
ssh -N -L 18080:127.0.0.1:8080 prada &
open http://localhost:18080/stream
```

The camera does not need the Robot HAT. If it isn't detected, check that the Pi 5 cable (22-pin end at the Pi) is seated in CAM0 with the latch closed, with the Pi off.

## SSH from a laptop

```
Host prada
  HostName prada.local
  User liquidiv
  AddressFamily inet6
  IdentityFile ~/.ssh/id_prada
  IdentitiesOnly yes
```

`AddressFamily inet6` is there because a Mac on the same iPhone hotspot often only gets IPv6 (NAT64), so IPv4 to the Pi's `172.20.10.x` address fails. The Pi's IPv6 address is global, so keep password login off or strong.

## Calibration

Offsets from the paper-sheet calibration (`0_calibration.py`), per leg in servo order:

| Leg | HAT ports | Servo 1 | Servo 2 | Servo 3 (hip) |
| --- | --- | --- | --- | --- |
| 1 front right | P9 P10 P11 | 3.24 | 1.82 | 6.16 |
| 2 front left | P3 P4 P5 | 10.74 | 6.03 | **-19.91** |
| 3 back left | P0 P1 P2 | 9.98 | 5.61 | 6.70 |
| 4 back right | P6 P7 P8 | 2.40 | 1.35 | -7.17 |

**Known issue:** leg 2's hip servo (P5) is at the helper's 20° limit, so its horn is most likely one spline notch off. With power off, reseat it one notch toward zero and recalibrate leg 2. Watch that servo for buzzing or heat until then.

To recalibrate: stop both services, set the robot flat on the sheet, then `cd ~/vendor/picrawler/examples && ~/hackutd26/robot/.venv/bin/python 0_calibration.py`. Save with space then `Y`. Copy the new `~/.config/.picrawler.config` into this folder and commit it.

## Bring-up notes

- The Imager first-boot password didn't work, so the key and password were reset by editing cloud-init on the SD card's `system-boot` partition. Bump `instance-id` in `meta-data` and the `i=` value in `cmdline.txt` to make cloud-init run again. `chpasswd` runs too early on a re-run, so use a `runcmd` entry with `chpasswd -e` instead.
- Passwordless sudo for `liquidiv` is in `/etc/sudoers.d/90-liquidiv-nopasswd` for the hackathon. Remove it afterwards.
- The Ubuntu mirror had a version mismatch for `libgnutls28-dev`. `pinctrl` builds without it, so `install.sh` skips it.
- `gpiodetect` without root still fails on chips 0 to 3; only `gpiochip4` (RP1, the 40-pin header) matters and the `dialout` group already has it.
- The 128-bit service UUID plus a name must fit in a 31-byte advertisement, which is why the joystick advertises as `prada` rather than a longer name.
- ROS is not installed on prada. The bridge runs without `--ros`.
- prada lost power right after an update and came back with empty service files (one zero-filled, one empty, which systemd reports as "masked"). `install.sh` now writes system files only when they change, through a temp file and `sync`. If it happens again, run `prada.sh install`. Keep the battery charged: it read 6.81 V before this.
