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
| `install` | Set up or repair everything (add `--enable` to start the services at boot) |
| `start` / `stop` / `restart` | Control the server, joystick relay, camera stream and speech together |
| `status` | Service state, repo commit, HAT on I2C, battery (when stopped), last joystick events, camera URL, speech `/health` |
| `logs` | Follow all four services' logs |
| `keyboard` | Stop the joystick relay so a laptop keyboard controller can connect |

None of these move servos. The robot moves only after the joystick is clicked and pushed. Starting speech plays nothing; it speaks only when asked.

## Files

| File | What it is |
| --- | --- |
| `prada.sh` | The one entry point above |
| `install.sh` | Idempotent setup: packages, pinned vendor libraries, `pinctrl`, I2C/GPIO access, venv, optional Piper voice, calibration restore, token, services |
| `prada.toml` | Bridge config: loopback only, every gait at speed 80 |
| `picrawler.config` | Saved calibration offsets (copied to `~/.config/.picrawler.config` if missing) |
| `system/prada-server.service` | Bridge server in hardware mode |
| `system/prada-joystick.service` | Bluetooth relay from the Arduino 101 joystick |
| `system/prada-camera.service` | Camera MJPEG stream on port 8080 |
| `system/prada-speaker.service` | Turns on the HAT speaker amplifier at boot |
| `system/prada-speech.service` | Text to speech for the rescue agent on port 8081 |
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

`prada-camera` runs `stream.py --camera`, which reads `rpicam-vid --codec mjpeg` at **120 fps** (768x432 from the IMX708's 1536x864 mode, JPEG quality 60, continuous autofocus, rotated 180 degrees for prada's mount) and publishes each JPEG to the stream's `FrameBuffer`. The service log prints the measured fps every 10 s. It listens on IPv4 (`--host 0.0.0.0`) so the visor on the hotspot can reach it; the control socket stays on loopback.

Watch it from any device on `liquid-iv-iot` at `http://<prada-ipv4>:8080/stream` (`prada.sh status` prints the address; it was `172.20.10.3`).

**Measured over the hotspot** (Mac on the hotspot, direct, no tunnel): 113-117 fps delivered, 11-12 Mbit/s, 11-13 KB per frame, median 6-7 ms between frames with occasional 100-250 ms Wi-Fi hiccups. Busy, bright scenes can reach about 25 KB per frame (about 24 Mbit/s); if a viewer starts dropping frames, lower `--quality` in `prada-camera.service`.

The stream never queues for a slow viewer: each client socket keeps only a few frames in flight and skips frames it can't take yet, so a slow link gets fewer, newer frames instead of growing lag. Before that change, 120 fps over a slow path built up a 2.8 MB backlog (over a second of lag).

If a Mac on the hotspot only gets IPv6, an SSH tunnel works, but it adds its own buffering, so judge lag from a direct IPv4 connection:

```bash
ssh -N -L 18080:127.0.0.1:8080 prada &
open http://localhost:18080/stream
```

The camera does not need the Robot HAT. If it isn't detected, check that the Pi 5 cable (22-pin end at the Pi) is seated in CAM0 with the latch closed, with the Pi off.

## Audio

- **Speaker:** the Robot HAT v4's I2S amplifier, as ALSA card `sndrpihifiberry` (from `dtoverlay=hifiberry-dac` in `config.txt`). Its amplifier is off until GPIO 20 is high; the overlay claims that pin at boot, so `prada-speaker.service` sets it afterwards. Test: `speaker-test -D plughw:sndrpihifiberry -c 2 -t sine -l 1`.
- **Microphone:** a USB "PnP Sound Device" (C-Media), mono capture at 48 kHz, ALSA card `Device`.

## Speech

`prada-speech` runs `speech.py`: the rescue agent posts text and prada says it through the HAT speaker. It is offline (nothing leaves the Pi) and listens on port **8081**, on IPv4 and IPv6 (`--host ::`), separate from the control socket. It moves nothing.

```bash
export ROBOT_CONTROL_TOKEN=...   # ssh prada sudo cat /etc/picrawler-control.env
curl -X POST -H "Authorization: Bearer $ROBOT_CONTROL_TOKEN" -H 'Content-Type: application/json' \
  -d '{"text":"Can you hear me?"}' http://prada.local:8081/say
curl -X POST -H "Authorization: Bearer $ROBOT_CONTROL_TOKEN" -H 'Content-Type: application/json' \
  -d '{"text":"Responders have been notified.","interrupt":true}' http://prada.local:8081/say
curl -X POST -H "Authorization: Bearer $ROBOT_CONTROL_TOKEN" http://prada.local:8081/stop
curl http://prada.local:8081/health
curl http://prada.local:8081/phrases
```

| Request | Auth | Answer |
| --- | --- | --- |
| `POST /say` `{"text": "...", "interrupt": false}` | token | 202 `{"ok":true,"id":7,"queued":1,"engine":"piper","cached":false}` |
| `POST /stop` | token | 200 `{"ok":true,"cleared":2}`: stops playback and clears the queue |
| `GET /health` | none | 200 `{"ok":true,"engine":"piper","device":"plughw:sndrpihifiberry","queue":0,"speaking":false,"last_error":null}` |
| `GET /phrases` | none | 200 `{"ok":true,"phrases":[{"text":"...","cached":true}, ...]}` |

- **Auth:** `Authorization: Bearer <token>`, the same `ROBOT_CONTROL_TOKEN` as the bridge (the unit reads `/etc/picrawler-control.env`). A missing or wrong token gets 401 and nothing is queued. GET needs no token and shows no secrets. Plain HTTP: anyone on the hotspot can see the token in transit, same as the control socket.
- **Text:** a JSON string, 1 to 300 characters after control characters are removed. Unknown or duplicate keys, a non-boolean `interrupt`, a body over 4 KB or a non-JSON `Content-Type` are rejected (400, 413 or 415).
- **Order:** one worker speaks lines in order. At most 5 wait; a sixth drops the oldest waiting line (never an interrupt). `queued` is how many are waiting, this one included. `interrupt: true` kills the current line and clears the queue first; use it for critical lines.
- **Engines,** picked once at startup and shown in `/health`: Piper with `~/.local/share/piper/en_US-lessac-medium.onnx` (and its `.onnx.json`), else `espeak-ng -v en-us -s 150`, else `dry-run` (silent, log only). Each is test-run on one word first, so a broken Piper falls back to espeak-ng. `install.sh` installs espeak-ng from apt and tries `pip install piper-tts` plus the voice download; offline it warns and keeps espeak-ng. `device` is `null` when nothing is played (dry run, or no `aplay`).
- **Cache:** WAVs are kept in `~/.cache/prada-speech`, named by sha256 of engine, voice and text, so a repeated line skips synthesis (`cached: true`). The agent's four scripted lines are rendered at startup; `/phrases` shows which are ready. About 200 files are kept, oldest first out.
- **Logs:** each line logs its id, the first 60 characters, synthesis ms and playback ms (`prada.sh logs`). `last_error` holds the latest failure (synthesis, `aplay`, timeout) until restart.
- **Limits:** synthesis is killed after 30 s and `aplay` after the WAV's length plus 10 s. `plughw` opens the card exclusively, so `speaker-test` or another player fails with "device busy" while speech is playing, and the other way round.

Test the speaker without the service: `sudo systemctl stop prada-speech`, then `speaker-test -D plughw:sndrpihifiberry -c 2 -t sine -l 1` (a tone) or `espeak-ng -v en-us -w /tmp/t.wav 'Can you hear me?' && aplay -D plughw:sndrpihifiberry /tmp/t.wav`. No sound with no error usually means the amplifier is off: check `systemctl status prada-speaker`.

Turn speech off: `sudo systemctl disable --now prada-speech` stops it and keeps it from starting at boot. `prada.sh start`, `restart` and `update` (when running) start it again, because it is in `SERVICES` in `prada.sh`; take it out of that list in the repo to keep it off for good.

On a laptop, without a speaker: `ROBOT_CONTROL_TOKEN=... robot/.venv/bin/python -m picrawler_control.speech --config robot/config/control.toml --dry-run`.

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
