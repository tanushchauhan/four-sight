#!/usr/bin/env bash
# Set up prada (Pi 5, Ubuntu Server 24.04, Robot HAT v4) from a fresh clone.
# Run as the robot user (liquidiv) from the repo root: robot/prada/install.sh [--enable]
# Safe to re-run. Moves no servos. Needs sudo.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PRADA="$REPO/robot/prada"
VENDOR="$HOME/vendor"
VENV="$REPO/robot/.venv"
ROBOT_HAT_REV=4556bd8ba81277819d8e9711319e32cd19504fe9
PICRAWLER_REV=14a54bc1909d4ce8f73e95d0fc09514c974910ce
ENABLE=0
[[ "${1:-}" == "--enable" ]] && ENABLE=1

if [[ "$(id -un)" != "liquidiv" || "$REPO" != "/home/liquidiv/hackutd26" ]]; then
  echo "Expected user liquidiv and checkout /home/liquidiv/hackutd26." >&2
  echo "Edit the paths in robot/prada/system/*.service and picrawler-lock.conf for another setup." >&2
  exit 1
fi

step() { printf '\n== %s\n' "$*"; }

# Copy a file into a system directory only when it differs, and flush it to the
# SD card. prada can lose power at any moment (low battery), and a half-written
# unit file leaves the service empty or masked.
put() {  # put <mode> <src> <dest-dir>
  local dest="$3/$(basename "$2")"
  if sudo cmp -s "$2" "$dest"; then return; fi
  sudo install -m "$1" "$2" "$dest.tmp" && sudo sync "$dest.tmp" && sudo mv -f "$dest.tmp" "$dest"
  sudo sync "$3"
  echo "updated $dest"
}

step "Packages"
export DEBIAN_FRONTEND=noninteractive
# The Imager image ships without noble-updates, so -dev packages lag their
# installed libraries (libgnutls, libzstd) and apt refuses them. Add it back.
if ! grep -qE '^Suites:.*noble-updates' /etc/apt/sources.list.d/ubuntu.sources; then
  sudo sed -i '0,/^Suites: noble$/s//Suites: noble noble-updates/' /etc/apt/sources.list.d/ubuntu.sources
fi
sudo apt-get update -q
sudo -E apt-get install -y -q git curl python3-venv python3-pip i2c-tools gpiod \
  python3-lgpio python3-smbus2 python3-gpiozero python3-spidev python3-serial python3-pil \
  python3-pyaudio python3-pygame cmake build-essential device-tree-compiler bluez alsa-utils espeak-ng

step "SunFounder libraries (pinned)"
mkdir -p "$VENDOR"
[[ -d "$VENDOR/robot-hat" ]] || git clone -q --branch 2.5.x https://github.com/sunfounder/robot-hat.git "$VENDOR/robot-hat"
git -C "$VENDOR/robot-hat" -c advice.detachedHead=false checkout -q "$ROBOT_HAT_REV"
[[ -d "$VENDOR/picrawler" ]] || git clone -q https://github.com/sunfounder/picrawler.git "$VENDOR/picrawler"
git -C "$VENDOR/picrawler" -c advice.detachedHead=false checkout -q "$PICRAWLER_REV"

step "pinctrl"
if ! command -v pinctrl >/dev/null; then
  [[ -d "$VENDOR/raspi-utils" ]] || git clone -q https://github.com/raspberrypi/utils.git "$VENDOR/raspi-utils"
  cmake -S "$VENDOR/raspi-utils/pinctrl" -B "$VENDOR/raspi-utils/pinctrl/build" >/dev/null
  cmake --build "$VENDOR/raspi-utils/pinctrl/build" >/dev/null
  sudo cmake --install "$VENDOR/raspi-utils/pinctrl/build" >/dev/null
fi
command -v pinctrl

step "I2C, GPIO access and lock"
grep -q '^dtparam=i2c_arm=on' /boot/firmware/config.txt || echo "WARNING: add dtparam=i2c_arm=on under [all] in /boot/firmware/config.txt"
put 644 "$PRADA/system/i2c-dev.conf" /etc/modules-load.d
getent group gpio >/dev/null || sudo groupadd --system gpio
sudo usermod -aG i2c,dialout,gpio,video,render,audio liquidiv
put 644 "$PRADA/system/60-picrawler-gpiomem.rules" /etc/udev/rules.d
sudo udevadm control --reload
sudo udevadm trigger --action=add --sysname-match='gpiomem*' || true
put 644 "$PRADA/system/picrawler-lock.conf" /etc/tmpfiles.d
sudo systemd-tmpfiles --create /etc/tmpfiles.d/picrawler-lock.conf
sudo systemctl enable --now bluetooth >/dev/null

step "Robot HAT speaker"
# The HAT v4 speaker is an I2S amplifier: it shows up as a sound card with the
# hifiberry-dac overlay, and its amplifier stays off until GPIO 20 is driven high
# (what robot_hat.enable_speaker() does). The overlay claims GPIO 20 at boot, so
# prada-speaker.service sets it after the sound card loads.
if ! grep -q '^dtoverlay=hifiberry-dac' /boot/firmware/config.txt; then
  printf '\n[all]\n# prada: Robot HAT v4 speaker (I2S amp)\ndtoverlay=hifiberry-dac\n' \
    | sudo tee -a /boot/firmware/config.txt >/dev/null
  sudo sync
  echo "speaker enabled in config.txt; reboot to load it"
fi
put 644 "$PRADA/system/prada-speaker.service" /etc/systemd/system
sudo systemctl daemon-reload
sudo systemctl enable --now prada-speaker >/dev/null 2>&1 || true

step "Python environment"
[[ -d "$VENV" ]] || python3 -m venv --system-site-packages "$VENV"
"$VENV/bin/python" -m pip install -q -e "$REPO/robot[ble]" "$VENDOR/robot-hat" "$VENDOR/picrawler"
"$VENV/bin/python" -m unittest discover -s "$REPO/robot/tests" 2>&1 | tail -1

step "Text to speech (optional Piper voice; espeak-ng is the fallback)"
# Piper sounds much better than espeak-ng but needs the network once. If pip or
# the download fails (offline), keep going: prada-speech falls back to espeak-ng.
PIPER_DIR="$HOME/.local/share/piper"
PIPER_VOICE=en_US-lessac-medium
PIPER_URL="https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium"
if "$VENV/bin/python" -c 'import piper' 2>/dev/null; then
  echo "piper-tts already installed"
elif "$VENV/bin/python" -m pip install -q --timeout 20 --retries 1 piper-tts; then
  echo "installed piper-tts"
else
  echo "WARNING: could not install piper-tts (offline?); speech will use espeak-ng"
fi
mkdir -p "$PIPER_DIR"
for f in "$PIPER_VOICE.onnx" "$PIPER_VOICE.onnx.json"; do
  if [[ -s "$PIPER_DIR/$f" ]]; then continue; fi
  if curl -fsSL --retry 2 --connect-timeout 10 -o "$PIPER_DIR/$f.tmp" "$PIPER_URL/$f?download=true" \
      && mv -f "$PIPER_DIR/$f.tmp" "$PIPER_DIR/$f"; then
    echo "downloaded $PIPER_DIR/$f"
  else
    rm -f "$PIPER_DIR/$f.tmp"
    echo "WARNING: could not download $f (offline?); speech will use espeak-ng"
  fi
done
sync

step "Speech to text (whisper.cpp, pinned; optional)"
# POST /listen records from the USB microphone and transcribes with whisper.cpp.
# Built once from a pinned tag; the model is fetched once from a pinned commit
# and checked. If either fails (offline), keep going: /listen still records and
# answers with stt_unavailable: true.
WHISPER_TAG=v1.9.4
WHISPER_SRC="$VENDOR/whisper.cpp"
WHISPER_CLI="$WHISPER_SRC/build/bin/whisper-cli"
WHISPER_DIR="$HOME/.local/share/whisper"
WHISPER_MODEL=ggml-base.en.bin
WHISPER_MODEL_URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/$WHISPER_MODEL"
WHISPER_MODEL_SHA256=a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002
build_whisper() {  # every step checked by hand: set -e does not apply inside `if`
  if [[ ! -d "$WHISPER_SRC/.git" ]]; then
    git clone -q --depth 1 --branch "$WHISPER_TAG" https://github.com/ggml-org/whisper.cpp.git "$WHISPER_SRC" || return 1
  fi
  git -C "$WHISPER_SRC" -c advice.detachedHead=false checkout -q "$WHISPER_TAG" || return 1
  cmake -S "$WHISPER_SRC" -B "$WHISPER_SRC/build" -DCMAKE_BUILD_TYPE=Release -DGGML_NATIVE=ON \
    -DBUILD_SHARED_LIBS=OFF -DWHISPER_BUILD_TESTS=OFF >/dev/null || return 1
  cmake --build "$WHISPER_SRC/build" -j 4 --target whisper-cli >/dev/null || return 1
  [[ -x "$WHISPER_CLI" ]]
}
if [[ -x "$WHISPER_CLI" ]]; then
  echo "keeping $WHISPER_CLI"
elif echo "building whisper.cpp $WHISPER_TAG (a few minutes)" && build_whisper; then
  echo "built $WHISPER_CLI"
else
  echo "WARNING: could not build whisper.cpp (offline?); /listen will report stt_unavailable"
fi
mkdir -p "$WHISPER_DIR"
if [[ -s "$WHISPER_DIR/$WHISPER_MODEL" ]]; then
  echo "keeping $WHISPER_DIR/$WHISPER_MODEL"
elif curl -fsSL --retry 2 --connect-timeout 10 -o "$WHISPER_DIR/$WHISPER_MODEL.tmp" "$WHISPER_MODEL_URL" \
    && echo "$WHISPER_MODEL_SHA256  $WHISPER_DIR/$WHISPER_MODEL.tmp" | sha256sum -c --quiet - \
    && mv -f "$WHISPER_DIR/$WHISPER_MODEL.tmp" "$WHISPER_DIR/$WHISPER_MODEL"; then
  echo "downloaded $WHISPER_DIR/$WHISPER_MODEL"
else
  rm -f "$WHISPER_DIR/$WHISPER_MODEL.tmp"
  echo "WARNING: could not download $WHISPER_MODEL (offline?); /listen will report stt_unavailable"
fi
sync
# Recording needs the audio group (added above; prada-speech gets it on start).
if [[ " $(id -nG liquidiv) " == *" audio "* ]]; then echo "liquidiv is in the audio group (may record)"
else echo "WARNING: liquidiv is not in the audio group; /listen cannot open the microphone"; fi
arecord -l 2>/dev/null | grep '^card' || echo "WARNING: no capture card in arecord -l; plug in the USB microphone"

step "Camera (Raspberry Pi libcamera + rpicam-apps)"
"$PRADA/build-camera.sh"

step "Calibration"
if [[ -f "$HOME/.config/.picrawler.config" ]]; then
  echo "keeping existing ~/.config/.picrawler.config"
else
  mkdir -p "$HOME/.config"
  cp "$PRADA/picrawler.config" "$HOME/.config/.picrawler.config"
  echo "restored prada offsets from robot/prada/picrawler.config"
fi

step "Control token"
if sudo test -f /etc/picrawler-control.env; then
  echo "keeping /etc/picrawler-control.env"
else
  if [[ -s "$HOME/.config/picrawler-token" ]]; then token="$(cat "$HOME/.config/picrawler-token")"
  else token="$(openssl rand -hex 32)"; fi
  printf 'ROBOT_CONTROL_TOKEN=%s\n' "$token" | sudo tee /etc/picrawler-control.env >/dev/null
  sudo chmod 600 /etc/picrawler-control.env
  unset token
  echo "wrote /etc/picrawler-control.env (copy it to laptops with: ssh prada sudo cat /etc/picrawler-control.env)"
fi

step "Services"
put 644 "$PRADA/system/prada-server.service" /etc/systemd/system
put 644 "$PRADA/system/prada-joystick.service" /etc/systemd/system
put 644 "$PRADA/system/prada-camera.service" /etc/systemd/system
put 644 "$PRADA/system/prada-speech.service" /etc/systemd/system
sudo systemctl unmask prada-server prada-joystick prada-camera prada-speech >/dev/null 2>&1 || true
sudo systemctl daemon-reload
if (( ENABLE )); then
  sudo systemctl enable prada-server prada-joystick prada-camera prada-speech
  echo "enabled at boot"
fi

sync

cat <<'EOF'

Done. Log out and back in (or reboot) if group membership changed.
Start:   robot/prada/prada.sh start
Status:  robot/prada/prada.sh status
Camera:  http://prada.local:8080/stream
Speech:  http://prada.local:8081/health
EOF
