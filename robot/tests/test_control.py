import json
import threading
import time
import unittest
from unittest.mock import patch

from picrawler_control.config import Config
from picrawler_control.control import Control, decode, validate
from picrawler_control.backend import Hardware, Worker


def wait_for(predicate, timeout=1):
    end = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() > end:
            raise AssertionError("condition timed out")
        time.sleep(0.005)


class FakeBackend:
    def __init__(self):
        self.initialized = 0
        self.calls = []
        self.started = threading.Event()
        self.release = threading.Event()

    def initialize(self):
        self.initialized += 1

    def execute(self, *choice):
        self.calls.append(choice)
        self.started.set()
        self.release.wait(2)


class ControlTests(unittest.TestCase):
    def setUp(self):
        self.now = 10.0
        self.c = Control(Config(), lambda: self.now)
        self.c.connect()
        self.seq = 0

    def send(self, kind, **axes):
        self.seq += 1
        self.c.accept(dict(type=kind, seq=self.seq, **axes))

    def test_disabled_enable_neutral_and_mapping(self):
        with self.assertRaises(ValueError):
            self.send("drive", forward=1, turn=0)
        self.send("enable")
        for forward, turn, expected in [(0.15, 0, None), (1, 0, "forward"),
                                        (-1, 0, "backward"), (0, 1, "turn left"),
                                        (0, -1, "turn right"), (0.6, -0.6, "turn right")]:
            self.send("drive", forward=forward, turn=turn)
            result = self.c.choice()
            self.assertEqual(result[0] if result else None, expected)
            if result:
                self.assertTrue(20 <= result[1] <= 50)
        self.assertEqual(self.c.choice()[1], 36)

    def test_timeout_latches_and_enable_clears_motion(self):
        self.send("enable")
        self.send("drive", forward=1, turn=0)
        self.now += 0.5
        self.assertIsNone(self.c.choice())
        with self.assertRaises(ValueError):
            self.send("drive", forward=1, turn=0)
        self.send("enable")
        self.assertIsNone(self.c.choice())
        self.now += 0.5
        self.assertFalse(self.c.snapshot()["enabled"])

    def test_stop_release_disconnect_and_reconnect(self):
        for kind in ("stop", "release"):
            self.send("enable")
            self.send("drive", forward=1, turn=0)
            self.send(kind)
            self.assertIsNone(self.c.choice())
        self.c.disconnect()
        self.c.connect()
        self.assertEqual(self.c.seq, -1)
        self.assertFalse(self.c.enabled)

    def test_replay_or_invalid_disarms(self):
        self.send("enable")
        self.send("drive", forward=1, turn=0)
        with self.assertRaises(ValueError):
            self.c.accept(dict(type="drive", seq=self.seq, forward=1, turn=0))
        self.assertFalse(self.c.enabled)
        for value in (True, "1", None, [], float("nan"), float("inf"), 1.01, -1.01, 10**400):
            with self.subTest(value=str(value)[:30]), self.assertRaises(ValueError):
                validate(dict(type="drive", seq=1, forward=value, turn=0))
        for message in ({}, {"type": []}, {"type": "__import__('os')", "seq": 1},
                        {"type": "enable", "seq": True}, {"type": "enable", "seq": -1},
                        {"type": "enable", "seq": 2**53},
                        {"type": "enable", "seq": 1, "speed": 100}):
            with self.assertRaises(ValueError):
                validate(message)

    def test_json_rejections(self):
        for raw in ('[]', 'null', '{', '{"type":"enable","type":"stop"}', b'{}'):
            with self.assertRaises(ValueError):
                decode(raw)

    def test_config_rejections(self):
        for kwargs in ({"timeout": 0}, {"port": True}, {"deadzone": 1},
                       {"max_speed": 99}, {"poll_interval": float("nan")}):
            with self.assertRaises(ValueError):
                Config(**kwargs)


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.c = Control(Config(timeout=2))
        self.c.connect()
        self.b = FakeBackend()
        self.w = Worker(self.c, self.b)
        self.w.start()
        self.seq = 0

    def send(self, kind, **axes):
        self.seq += 1
        self.c.accept(dict(type=kind, seq=self.seq, **axes))

    def tearDown(self):
        self.c.stop("test end")
        self.b.release.set()
        self.w.close()

    def move(self):
        self.send("enable")
        self.send("drive", forward=1, turn=0)
        self.assertTrue(self.b.started.wait(1))

    def test_no_initialization_until_enabled_nonzero(self):
        self.send("enable")
        self.send("drive", forward=0, turn=0)
        time.sleep(0.05)
        self.assertEqual(self.b.initialized, 0)
        self.send("drive", forward=1, turn=0)
        self.assertTrue(self.b.started.wait(1))
        self.assertEqual(self.b.initialized, 1)

    def test_latest_only_and_stop_while_busy(self):
        self.move()
        for turn in (1, -1, 1):
            self.send("drive", forward=0, turn=turn)
        self.send("stop")
        self.assertTrue(self.c.snapshot()["busy"])
        self.assertFalse(self.c.snapshot()["enabled"])
        self.b.release.set()
        wait_for(lambda: not self.c.snapshot()["busy"])
        time.sleep(0.05)
        self.assertEqual(self.b.calls, [("forward", 50)])

    def test_latest_replaces_old_directions(self):
        self.move()
        for forward, turn in ((-1, 0), (0, 1), (0, -1)):
            self.send("drive", forward=forward, turn=turn)
        original = self.b.execute
        def second(*choice):
            self.c.stop("observed second")
            original(*choice)
        self.b.execute = second
        self.b.release.set()
        wait_for(lambda: len(self.b.calls) == 2)
        self.assertEqual(self.b.calls, [("forward", 50), ("turn right", 50)])

    def test_stop_during_initialization_skips_gait(self):
        entered = threading.Event()
        release = threading.Event()
        def init():
            entered.set()
            release.wait(1)
        self.b.initialize = init
        self.send("enable")
        self.send("drive", forward=1, turn=0)
        self.assertTrue(entered.wait(1))
        self.send("stop")
        release.set()
        wait_for(lambda: not self.c.snapshot()["busy"])
        self.assertEqual(self.b.calls, [])

    def test_timeout_while_gait_busy_prevents_next(self):
        self.move()
        self.c.last_drive = time.monotonic() - 3
        self.assertFalse(self.c.snapshot()["enabled"])
        self.b.release.set()
        wait_for(lambda: not self.c.snapshot()["busy"])
        self.assertEqual(len(self.b.calls), 1)

    def test_exception_latches_fault(self):
        def fail(*args):
            raise RuntimeError("simulated I2C failure")
        self.b.execute = fail
        with self.assertLogs(level="ERROR"):
            self.send("enable")
            self.send("drive", forward=1, turn=0)
            wait_for(lambda: self.c.snapshot()["fault"] is not None)
        with self.assertRaises(ValueError):
            self.send("enable")

    def test_hardware_whitelist_without_vendor_import(self):
        hardware = Hardware.__new__(Hardware)
        with self.assertRaises(ValueError):
            hardware.execute("__import__('os')", 50)
        with self.assertRaises(ValueError):
            hardware.execute("forward", 100)

    def test_hardware_ownership_lock(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            owner = Hardware(directory + "/owner.lock")
            try:
                with self.assertRaises(RuntimeError):
                    Hardware(directory + "/owner.lock")
            finally:
                owner.close()
