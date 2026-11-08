#!/usr/bin/env bash
# Build Raspberry Pi's libcamera and rpicam-apps on prada (Ubuntu 24.04, Pi 5).
# Ubuntu's libcamera 0.2 has no Pi 5 (PiSP) pipeline, so the Camera Module 3
# (IMX708 on CAM0) needs the Raspberry Pi fork. Installs to /usr/local.
# Safe to re-run; skips the build when rpicam-vid already works. Takes ~20 min.
set -euo pipefail

VENDOR="$HOME/vendor"
LIBCAMERA_REV=v0.7.2+rpt20261009
RPICAM_REV=v1.14.0

if command -v rpicam-vid >/dev/null && rpicam-hello --list-cameras 2>&1 | grep -q imx; then
  echo "rpicam-apps already installed and sees the camera"
  exit 0
fi

export DEBIAN_FRONTEND=noninteractive
sudo apt-get update -q
# No libdw-dev: its libzstd-dev lags the installed libzstd1 on this mirror, and
# libcamera only uses it for backtraces.
sudo apt-get install -y -q git meson ninja-build pkg-config cmake python3-yaml python3-ply \
  python3-jinja2 libyaml-dev libssl-dev libudev-dev libevent-dev libdrm-dev \
  libjpeg-dev libtiff-dev libpng-dev libexif-dev libboost-program-options-dev nlohmann-json3-dev

mkdir -p "$VENDOR"
if [[ ! -d "$VENDOR/libcamera" ]]; then
  git clone -q https://github.com/raspberrypi/libcamera.git "$VENDOR/libcamera"
fi
git -C "$VENDOR/libcamera" fetch -q --tags
git -C "$VENDOR/libcamera" -c advice.detachedHead=false checkout -q "$LIBCAMERA_REV"
cd "$VENDOR/libcamera"
[[ -d build ]] || meson setup build --buildtype=release --prefix=/usr/local \
  -Dpipelines=rpi/pisp -Dipas=rpi/pisp -Dv4l2=false -Dgstreamer=disabled -Dtest=false \
  -Dlc-compliance=disabled -Dcam=disabled -Dqcam=disabled -Ddocumentation=disabled -Dpycamera=disabled
ninja -C build
sudo ninja -C build install
sudo ldconfig

if [[ ! -d "$VENDOR/rpicam-apps" ]]; then
  git clone -q https://github.com/raspberrypi/rpicam-apps.git "$VENDOR/rpicam-apps"
fi
git -C "$VENDOR/rpicam-apps" fetch -q --tags
git -C "$VENDOR/rpicam-apps" -c advice.detachedHead=false checkout -q "$RPICAM_REV"
cd "$VENDOR/rpicam-apps"
[[ -d build ]] || meson setup build --buildtype=release --prefix=/usr/local \
  -Denable_libav=disabled -Denable_drm=disabled -Denable_egl=disabled -Denable_qt=disabled \
  -Denable_opencv=disabled -Denable_tflite=disabled -Denable_hailo=disabled -Denable_imx500=false
ninja -C build
sudo ninja -C build install
sudo ldconfig

rpicam-hello --list-cameras
