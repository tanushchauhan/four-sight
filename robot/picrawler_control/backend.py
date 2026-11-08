import logging
import json
import math
import shutil
from pathlib import Path
import threading
import time

from .control import ACTIONS, GESTURES


class GestureCancelled(Exception):
    pass


class CheckedRobot:
    """Check cancellation between vendor pose calls, including recovery poses."""
    def __init__(self, robot, cancelled):
        self.robot, self.cancelled = robot, cancelled

    def __getattr__(self, name):
        return getattr(self.robot, name)

    def do_step(self, *args, **kwargs):
        if self.cancelled():
            raise GestureCancelled()
        return self.robot.do_step(*args, **kwargs)

    def do_action(self, name, step=1, speed=50):
        if name not in ('stand', 'wave'):
            raise ValueError('unsupported gesture action')
        for _ in range(step):
            self.robot.move_list.stand_position = self.robot.stand_position
            for pose in self.robot.move_list[name]:
                self.do_step(pose, speed=speed)


class DryRun:
    def __init__(self, duration):
        self.duration = duration

    def gesture(self, name, cancelled):
        if cancelled():
            raise GestureCancelled()
        time.sleep(self.duration)

    def initialize(self):
        pass

    def execute(self, action, speed):
        if action not in ACTIONS:
            raise ValueError("action not whitelisted")
        logging.info("DRY RUN: %s speed=%d step=1", action, speed)
        time.sleep(self.duration)


class Hardware:
    """Only the gait worker may initialize or call the vendor library."""
    def __init__(self, lock_path="/run/lock/picrawler-control.lock"):
        import fcntl
        # All deployments/users must share this path; don't unlink a held lock.
        self.lock_file = open(lock_path, "a")
        try:
            fcntl.flock(self.lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.lock_file.close()
            raise RuntimeError("another hardware owner is running") from None
        self.robot = None

    def initialize(self):
        # Constructing Picrawler resets the MCU and commands all 12 servos.
        # Deliberately never called at server startup or for enable/neutral/invalid input.
        if shutil.which("pinctrl") is None:
            raise RuntimeError("pinctrl is required for Pi 5 MCU reset")
        from picrawler import Picrawler
        calibration = Path(Picrawler.OFFSET_FILE)
        if not calibration.is_file():
            raise RuntimeError(f"calibration file missing: {calibration}")
        self.robot = Picrawler()

    def execute(self, action, speed):
        if action not in ACTIONS or type(speed) is not int or not 1 <= speed <= 100:
            raise ValueError("invalid hardware action/speed")
        self.robot.do_action(action, step=1, speed=speed)

    def gesture(self, name, cancelled):
        robot = CheckedRobot(self.robot, cancelled)
        if name == 'tilt':
            from .tilt import release_test
            release_test(robot)
        else:
            from .gestures import perform
            perform(robot, name)

    def sample_battery(self):
        # Called only by the hardware worker, serialized with every servo operation.
        from robot_hat.device import get_battery_voltage
        voltage = get_battery_voltage()
        if not math.isfinite(voltage) or not 0 < voltage < 20:
            raise ValueError('Invalid battery voltage')
        path = Path('/tmp/prada-battery.json')
        temporary = path.with_suffix('.tmp')
        temporary.write_text(json.dumps(dict(voltage=round(voltage, 2), sampled=time.monotonic(),
            boot=Path('/proc/sys/kernel/random/boot_id').read_text().strip())))
        temporary.replace(path)

    def close(self):
        self.lock_file.close()


class Worker:
    def __init__(self, control, backend):
        self.control = control
        self.backend = backend
        self.halt = threading.Event()
        self.thread = threading.Thread(target=self.run, name="gait-owner", daemon=False)
        self.ready = False

    def start(self):
        self.thread.start()

    def run(self):
        next_battery = 0
        while not self.halt.is_set():
            if time.monotonic() >= next_battery:
                next_battery = time.monotonic() + 5
                sample = getattr(self.backend, 'sample_battery', None)
                if sample:
                    try: sample()
                    except Exception: logging.debug('Battery reading unavailable', exc_info=True)
            c = self.control
            with c.lock:
                choice = c.choice()
                if choice:
                    c.busy = True
                    c.action = "initializing" if not self.ready else choice[0]
                    generation = c.generation
            if not choice:
                self.halt.wait(c.config.poll_interval)
                continue
            try:
                if not self.ready:
                    self.backend.initialize()
                    self.ready = True
                # Recheck latest state after potentially slow servo initialization.
                with c.lock:
                    choice = c.choice()
                    if self.halt.is_set() or c.generation != generation or choice is None:
                        continue
                    c.action = choice[0]
                    is_gesture = choice[0] in GESTURES
                    if is_gesture:
                        c.gesture_pending = False
                # A committed synchronous call is non-interruptible. Never hold the
                # state lock over vendor I/O: stop/timeout reception must stay responsive.
                if is_gesture:
                    def cancelled():
                        with c.lock:
                            c.expire()
                            return self.halt.is_set() or c.generation != generation
                    self.backend.gesture(choice[0], cancelled)
                    with c.lock:
                        if c.generation == generation:
                            c.stop('gesture complete; enable required')
                else:
                    self.backend.execute(*choice)
            except GestureCancelled:
                pass
            except Exception as exc:
                logging.exception("gait backend failed; restart required")
                with c.lock:
                    c.fault = f"{type(exc).__name__}: {exc}"
                    c.stop("backend fault; restart required")
            finally:
                with c.lock:
                    c.busy = False
                    c.action = None

    def close(self):
        self.control.stop("shutdown")
        self.halt.set()
        # Retain the process/ownership lock until a vendor call actually returns.
        self.thread.join()
        close = getattr(self.backend, "close", None)
        if close:
            close()
