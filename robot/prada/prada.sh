#!/usr/bin/env bash
# The one command for running prada. Run on the Pi from anywhere:
#   ~/hackutd26/robot/prada/prada.sh <command>
#
#   update    pull the repo, reinstall, restart running services (the usual one)
#   install   set up or repair everything (packages, libraries, access, services)
#   start     start the server, the joystick relay and the camera stream
#   stop      stop all three
#   restart   restart all three
#   status    show service state, HAT, battery, joystick and camera
#   logs      follow all three services' logs
#   keyboard  stop the joystick relay so a laptop keyboard controller can connect
#
# None of these move servos. The robot moves only after the joystick is clicked
# (enable) and pushed.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
VENV="$REPO/robot/.venv"
SERVICES=(prada-server prada-joystick prada-camera)

running() { systemctl is-active --quiet prada-server; }

case "${1:-help}" in
  update)
    git -C "$REPO" pull --ff-only
    was_running=0; running && was_running=1
    "$REPO/robot/prada/install.sh"
    if (( was_running )); then sudo systemctl restart "${SERVICES[@]}"; echo "restarted"; fi
    ;;
  install)
    "$REPO/robot/prada/install.sh" "${@:2}"
    ;;
  start)
    sudo systemctl start "${SERVICES[@]}"
    systemctl --no-pager --lines=0 status "${SERVICES[@]}" | grep -E "●|Active:"
    ;;
  stop)
    sudo systemctl stop prada-joystick prada-camera prada-server
    ;;
  restart)
    sudo systemctl restart "${SERVICES[@]}"
    ;;
  status)
    for s in "${SERVICES[@]}"; do printf '%-16s %s\n' "$s" "$(systemctl is-active "$s")"; done
    printf '%-16s %s\n' "repo" "$(git -C "$REPO" log --oneline -1)"
    scan="$(i2cdetect -y 1 2>/dev/null || true)"  # not piped: grep -q would SIGPIPE it under pipefail
    if grep -qE ' (14|15) ' <<<"$scan"; then hat=found; else hat=missing; fi
    printf '%-16s %s\n' "HAT on I2C" "$hat"
    if running; then
      printf '%-16s %s\n' "battery" "(skipped while the server owns the HAT)"
    else
      printf '%-16s %s V\n' "battery" "$("$VENV/bin/python" -W ignore -c 'from robot_hat import utils; print(round(utils.get_battery_voltage(), 2))' 2>/dev/null || echo '?')"
    fi
    journalctl -u prada-joystick -n 40 --no-pager -o cat 2>/dev/null | grep -E "found joystick|connected|enabled|disabled|session ended" | tail -3 || true
    if systemctl is-active --quiet prada-camera; then
      printf '%-16s %s\n' "camera" "http://$(hostname).local:8080/stream  (also http://$(hostname -I | awk '{print $1}'):8080/stream)"
    fi
    ;;
  logs)
    journalctl -f -u prada-server -u prada-joystick -u prada-camera
    ;;
  keyboard)
    sudo systemctl stop prada-joystick
    echo "joystick relay stopped; connect the laptop controller through an SSH tunnel (see robot/prada/README.md)"
    ;;
  *)
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    ;;
esac
