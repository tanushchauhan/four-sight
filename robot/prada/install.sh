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

step "Packages"
export DEBIAN_FRONTEND=noninteractive
sudo apt-get update -q
sudo -E apt-get install -y -q git curl python3-venv python3-pip i2c-tools gpiod \
  python3-lgpio python3-smbus2 python3-gpiozero python3-spidev python3-serial python3-pil \
  python3-pyaudio python3-pygame cmake build-essential device-tree-compiler bluez

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
echo i2c-dev | sudo tee /etc/modules-load.d/i2c-dev.conf >/dev/null
getent group gpio >/dev/null || sudo groupadd --system gpio
sudo usermod -aG i2c,dialout,gpio liquidiv
sudo install -m 644 "$PRADA/system/60-picrawler-gpiomem.rules" /etc/udev/rules.d/
sudo udevadm control --reload
sudo udevadm trigger --action=add --sysname-match='gpiomem*' || true
sudo install -m 644 "$PRADA/system/picrawler-lock.conf" /etc/tmpfiles.d/
sudo systemd-tmpfiles --create /etc/tmpfiles.d/picrawler-lock.conf
sudo systemctl enable --now bluetooth >/dev/null

step "Python environment"
[[ -d "$VENV" ]] || python3 -m venv --system-site-packages "$VENV"
"$VENV/bin/python" -m pip install -q -e "$REPO/robot[ble]" "$VENDOR/robot-hat" "$VENDOR/picrawler"
"$VENV/bin/python" -m unittest discover -s "$REPO/robot/tests" 2>&1 | tail -1

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
sudo install -m 644 "$PRADA/system/prada-server.service" "$PRADA/system/prada-joystick.service" /etc/systemd/system/
sudo systemctl daemon-reload
if (( ENABLE )); then
  sudo systemctl enable prada-server prada-joystick
  echo "enabled at boot"
fi

cat <<'EOF'

Done. Log out and back in (or reboot) if group membership changed.
Start:   sudo systemctl start prada-server prada-joystick
Logs:    journalctl -fu prada-server -u prada-joystick
Stop:    sudo systemctl stop prada-joystick prada-server
EOF
