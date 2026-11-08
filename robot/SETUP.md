# Ubuntu 24.04 + Pi 5 + Robot HAT v4 setup

This is a manual bring-up procedure, not a tested image or unattended installer. Actual checkout on the development laptop: `/Users/atharva/Code/hackutd26`; target checkout below: `~/hackutd26` on the Pi. Nothing here has flashed the blank microSD or contacted a Pi.

## 1. Prepare the Pi

Use Raspberry Pi Imager to select **Ubuntu Server 24.04 LTS, 64-bit arm64 for Raspberry Pi 5** and the correct microSD device. Configure your own username, SSH key, hostname and private Wi-Fi. Flash only after identifying the card yourself; this repository supplies no disk-writing script. Boot and SSH into the Pi; check `/etc/os-release`, `uname -m` (aarch64) and network connectivity. Use cooling appropriate to Pi 5. Confirm the HAT v4 power arrangement against its documentation before attaching it; community power modifications are not wiring instructions for this robot.

```bash
sudo apt update
sudo apt upgrade
sudo apt install git curl locales software-properties-common python3-venv python3-pip
sudo locale-gen en_US en_US.UTF-8
sudo update-locale LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8
export LANG=en_US.UTF-8
sudo add-apt-repository universe
```

## 2. Install Jazzy ros-base

Follow the [official Jazzy Ubuntu deb instructions](https://docs.ros.org/en/jazzy/Installation/Ubuntu-Install-Debs.html) for apt-source registration. Ubuntu Noble arm64 has binary packages; building ROS from source is unnecessary. The current official source setup is:

```bash
export ROS_APT_SOURCE_VERSION=$(curl -fsSL https://api.github.com/repos/ros-infrastructure/ros-apt-source/releases/latest | python3 -c 'import json,sys; print(json.load(sys.stdin)["tag_name"])')
curl -fL -o /tmp/ros2-apt-source.deb "https://github.com/ros-infrastructure/ros-apt-source/releases/download/${ROS_APT_SOURCE_VERSION}/ros2-apt-source_${ROS_APT_SOURCE_VERSION}.$(. /etc/os-release && echo ${UBUNTU_CODENAME:-${VERSION_CODENAME}})_all.deb"
sudo dpkg -i /tmp/ros2-apt-source.deb
sudo apt update
sudo apt install ros-jazzy-ros-base ros-jazzy-geometry-msgs ros-jazzy-std-msgs
source /opt/ros/jazzy/setup.bash
ros2 --help
```

## 3. Install this bridge and test dry-run

```bash
cd ~
git clone https://github.com/tanushchauhan/hackutd26.git
cd ~/hackutd26
python3 -m venv --system-site-packages robot/.venv
robot/.venv/bin/python -m pip install -e ./robot
robot/.venv/bin/python -m unittest discover -s robot/tests -v
```

`--system-site-packages` exposes Ubuntu's Python hardware packages and sourced ROS environment; use the OS `/usr/bin/python3` (3.12 on Noble), not a different Python installed from source. If the checkout already exists, use it rather than cloning over files. New robot files must first be transferred/pushed from the laptop; they aren't automatically in the remote repository.

Set a random shared secret (e.g. generate with `openssl rand -hex 32`) in `ROBOT_CONTROL_TOKEN` in both Pi and laptop shells. Do not commit it. Copy the default config to `robot/config/local.toml`, set `host` to the Pi private Wi-Fi IP, then run:

```bash
source /opt/ros/jazzy/setup.bash
robot/.venv/bin/python -m picrawler_control.server --ros --config robot/config/local.toml
```

On the laptop follow [README](README.md), using `ws://PI_PRIVATE_IP:8765`. Confirm enable, all directions, neutral, space stop, quit, lost connection and expiry before any hardware run. If using a firewall, permit TCP 8765 only from the controller's private IP and retain SSH access. Local Wi-Fi and SSH configuration are done by the operator on the actual Pi.

## 4. Manually prepare the vendor libraries

The upstream installers assume Raspberry Pi OS and perform unrelated audio/voice setup. Do **not** run `install.py`, pipe an installer into a root shell, or apply old forum steps verbatim on Ubuntu. This milestone needs gait control, not camera or audio services. The current library nevertheless imports some audio Python modules, so those import dependencies are included.

The following exact source revisions were inspected for the adapter (not physically validated):

| Library | Revision |
| --- | --- |
| [SunFounder robot-hat, branch 2.5.x](https://github.com/sunfounder/robot-hat/tree/4556bd8ba81277819d8e9711319e32cd19504fe9) | `4556bd8ba81277819d8e9711319e32cd19504fe9` |
| [SunFounder picrawler](https://github.com/sunfounder/picrawler/tree/14a54bc1909d4ce8f73e95d0fc09514c974910ce) | `14a54bc1909d4ce8f73e95d0fc09514c974910ce` |

On the Pi:

```bash
sudo apt install i2c-tools gpiod python3-lgpio python3-smbus2 python3-gpiozero python3-spidev python3-serial python3-pil python3-pyaudio python3-pygame
mkdir -p ~/vendor
cd ~/vendor
git clone --branch 2.5.x https://github.com/sunfounder/robot-hat.git
git -C robot-hat checkout 4556bd8ba81277819d8e9711319e32cd19504fe9
git clone https://github.com/sunfounder/picrawler.git
git -C picrawler checkout 14a54bc1909d4ce8f73e95d0fc09514c974910ce
cd ~/hackutd26
robot/.venv/bin/python -m pip install ~/vendor/robot-hat ~/vendor/picrawler
```

Retain these revisions during first bring-up; re-review initialization/stop semantics before upgrading. Vendor packages remain separate dependencies with their own GPL licenses; their source is not copied into this package.

### Enable only the needed interfaces

Back up `/boot/firmware/config.txt`, edit its existing `[all]` section to include `dtparam=i2c_arm=on`, and ensure `i2c-dev` loads (for example a line `i2c-dev` in `/etc/modules-load.d/i2c-dev.conf`). Reboot. Do not replace the boot configuration. Gaits communicate with the HAT MCU on I2C bus 1; SPI and I2S audio are outside this milestone.

Do not enable the `sunfounder-robothat5` overlay on a HAT v4 merely because an old post lists it. The pinned upstream HAT code falls back to v4 when no v5 device-tree UUID is present. Neither a v5 identity override nor an audio overlay is part of this v4 gait procedure. If your installed firmware/kernel needs an additional board overlay, resolve that against the v4 documentation and actual device tree before running servos.

`pinctrl` is required for the vendor MCU reset on Pi 5. Check `command -v pinctrl`. If absent, follow the [Raspberry Pi utils build instructions](https://github.com/raspberrypi/utils) and build it on the Pi:

```bash
sudo apt install cmake build-essential device-tree-compiler libfdt-dev libgnutls28-dev
cd ~/vendor
git clone https://github.com/raspberrypi/utils.git raspi-utils
cmake -S raspi-utils/pinctrl -B raspi-utils/pinctrl/build
cmake --build raspi-utils/pinctrl/build
sudo cmake --install raspi-utils/pinctrl/build
pinctrl help
```

Record `git -C ~/vendor/raspi-utils rev-parse HEAD` with your bring-up notes. The current HAT library discovers the Pi 5 GPIO chip; don't assume it is gpiochip4 or gpiochip0. Use `gpiodetect` and actual device permissions. Ensure the login user can access `/dev/i2c-1`, the relevant `/dev/gpiochip*` and any `/dev/gpiomem*` needed by pinctrl. Ubuntu images vary: use existing `i2c`/`gpio` groups if present, otherwise add narrowly scoped udev/group access for the identified devices. Log out/in after group changes. Do not solve this with world-writable device nodes or by running the network server as root.

With servo power off, inspect:

```bash
ls -l /dev/i2c-1 /dev/gpiochip* /dev/gpiomem*
gpiodetect
i2cdetect -l
cd ~/hackutd26
robot/.venv/bin/python -c 'import robot_hat; from picrawler import Picrawler; print(robot_hat.__version__, Picrawler.OFFSET_FILE)'
```

The import does not construct a robot. No MCU scan, reset or servo example is invoked above. On the supported powered HAT, an operator can then check bus 1 for the MCU (vendor address alternatives 0x14/0x15); an I2C scan touches devices, so keep other I2C peripherals in mind. Missing devices, pinctrl errors or permission errors must be resolved before proceeding. Vendor I2C helpers can suppress some errors, so successful Python return alone is not proof of servo communication.

## 5. Calibrate and perform the first physical run

Follow [SunFounder's PiCrawler assembly/calibration documentation](https://docs.sunfounder.com/projects/pi-crawler/en/latest/) with the actual assembled robot supported off the floor and a reachable servo-power switch. Calibration itself moves servos; do it separately, with the bridge stopped. Match the twelve leg servo connections to the kit instructions. Save offsets as the same non-root account that runs the bridge: `~/.config/.picrawler.config`. The adapter requires this file, but existence alone does not prove correct calibration.

Use one process only; don't run vendor examples in parallel. Prepare the advisory lock once for your chosen account:

```bash
sudo touch /run/lock/picrawler-control.lock
sudo chown "$USER":"$(id -gn)" /run/lock/picrawler-control.lock
sudo chmod 600 /run/lock/picrawler-control.lock
```

`/run` is temporary; repeat after reboot or use the deployment tmpfiles example. Never delete/replace the lock file while a process holds it. Start manually, with robot supported, correct power and a physical cutoff:

```bash
cd ~/hackutd26
source /opt/ros/jazzy/setup.bash
robot/.venv/bin/python -m picrawler_control.server --ros --hardware --config robot/config/local.toml
```

Server startup, connection, enable and neutral must cause no servo movement. The first enabled nonzero command **does initialize and move servos**, followed by a gait only if commands remain fresh and enabled. Initialization may outlast a laptop key pulse; keep sending a deliberate direction when ready. Confirm directions and one gait at a time. Measure first initialization and all gait durations at configured speeds, then test neutral, stop during motion, quit, killed client, Wi-Fi loss and server shutdown. Verify disabled status while a committed gait completes, and no new gait after it. Expect the last pose to remain energized.

Do not demonstrate on the ground until leg calibration, directions, stop timing, power stability and disconnect behavior are verified. This stack provides no obstacle/drop protection, balance controller, battery watchdog, odometry or autonomous recovery.

## 6. Optional deployment after manual validation

`deploy/picrawler-control.service` is an editable example for user `ubuntu`, checkout `/home/ubuntu/hackutd26`. Change account and paths to the actual Pi. It starts **dry-run with ROS**, not hardware. Put the shared secret in `/etc/picrawler-control.env` (root-owned mode 600), set `local.toml`, then install/start the service manually:

```bash
# After editing the account, paths and local.toml:
sudo touch /etc/picrawler-control.env
sudo chown root:root /etc/picrawler-control.env
sudo chmod 600 /etc/picrawler-control.env
sudoedit /etc/picrawler-control.env
# Add one line: ROBOT_CONTROL_TOKEN=your-shared-secret
sudo install -m 644 robot/deploy/picrawler-control.service /etc/systemd/system/picrawler-control.service
sudo systemctl daemon-reload
sudo systemctl start picrawler-control
sudo systemctl status picrawler-control
journalctl -u picrawler-control -f
```

Stop with `sudo systemctl stop picrawler-control`. Only enable boot startup with `sudo systemctl enable picrawler-control` after dry-run validation. No service is installed or enabled by this repository.

To use hardware later, first configure `deploy/picrawler-control.conf` for the same account and install it under `/etc/tmpfiles.d/`, run `sudo systemd-tmpfiles --create /etc/tmpfiles.d/picrawler-control.conf`, and add `--hardware` to `ExecStart` only after the physical checklist above. The service won't auto-restart on failure. `TimeoutStopSec=infinity` preserves the single-owner rule while a vendor call is stuck; use the physical power cutoff and inspect the failure before another process starts.

## Compatibility evidence and remaining checks

- [Current vendor Robot constructor](https://github.com/sunfounder/robot-hat/blob/4556bd8ba81277819d8e9711319e32cd19504fe9/robot_hat/robot.py) and [PiCrawler actions](https://github.com/sunfounder/picrawler/blob/14a54bc1909d4ce8f73e95d0fc09514c974910ce/picrawler/picrawler.py) establish the lazy-init and one-call stop limitations described in README.
- [HAT Ubuntu report](https://forum.sunfounder.com/t/can-i-use-robot-hat-with-ubuntu-server-24-04-lts/2837) gives firsthand Pi 5/v4 experience, but its older branches, broad overlays and audio steps are not this procedure. [PiCrawler upgrades](https://forum.sunfounder.com/t/picrawler-upgrades/3750?page=2) also reports Ubuntu use with substantial hardware modifications. These are evidence of feasibility, not validation of this robot.
- On-Pi checks still outstanding: Ubuntu boot/network, package availability, pinctrl access/reset, HAT I2C, actual calibration offsets, initialization/gait duration and direction, power stability, Wi-Fi stop behavior, ROS publishers, colcon packaging and service lifecycle. The software tests use a fake backend; no hardware compatibility claim is made.


## Host-side software validation

The local suite also exercises the actual dry-run server process from startup through authenticated drive and SIGTERM shutdown. A fake ROS contract test checks topics, normalized axes and preserving the server's signal handlers; it does not load rclpy or test DDS. Wheel construction/content checks and deployment command syntax checks can run on macOS. These are separate from the remaining Ubuntu/Jazzy/systemd runtime and hardware validation listed above.
