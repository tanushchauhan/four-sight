"""Thread-safe latest-command slot. No transport or hardware dependencies."""
import json
import math
import threading
import time

ACTIONS = frozenset({"forward", "backward", "turn left", "turn right"})


def decode(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate field")
            result[key] = value
        return result
    try:
        value = json.loads(raw, object_pairs_hook=unique)
    except (TypeError, UnicodeError) as exc:
        raise ValueError("invalid JSON text") from exc
    if not isinstance(raw, str) or not isinstance(value, dict):
        raise ValueError("expected a JSON text object")
    return value


def validate(message):
    kind = message.get("type")
    expected = {"drive": {"type", "seq", "forward", "turn"},
                "enable": {"type", "seq"}, "stop": {"type", "seq"},
                "release": {"type", "seq"}}
    if not isinstance(kind, str) or kind not in expected or message.keys() != expected[kind]:
        raise ValueError("unsupported message or fields")
    if type(message["seq"]) is not int or not 0 <= message["seq"] <= 2**53 - 1:
        raise ValueError("seq must be a nonnegative safe integer")
    if kind == "drive":
        for axis in ("forward", "turn"):
            value = message[axis]
            if type(value) not in (int, float) or not -1 <= value <= 1 or not math.isfinite(value):
                raise ValueError("axes must be finite numbers in [-1, 1]")
    return message


class Control:
    def __init__(self, config, clock=time.monotonic):
        self.config = config
        self.clock = clock
        self.lock = threading.RLock()
        self.connected = False
        self.enabled = False
        self.seq = -1
        self.forward = self.turn = 0.0
        self.last_drive = None
        self.reason = "startup"
        self.fault = None
        self.busy = False
        self.action = None
        self.generation = 0

    def stop(self, reason):
        with self.lock:
            self.enabled = False
            self.forward = self.turn = 0.0
            self.last_drive = None
            self.reason = reason
            self.generation += 1

    def connect(self):
        with self.lock:
            self.stop("connected; enable required")
            self.connected = True
            self.seq = -1

    def disconnect(self):
        with self.lock:
            self.connected = False
            self.stop("disconnected")

    def expire(self):
        with self.lock:
            if self.enabled and self.clock() - self.last_drive >= self.config.timeout:
                self.stop("command timeout; enable required")

    def accept(self, message):
        with self.lock:
            self.expire()
            try:
                validate(message)
                if not self.connected or message["seq"] <= self.seq:
                    raise ValueError("no session or non-increasing seq")
                self.seq = message["seq"]
                kind = message["type"]
                if kind == "enable":
                    if self.fault or self.busy:
                        raise ValueError("fault or motion/initialization in progress")
                    self.stop("enabled; waiting for drive")
                    self.enabled = True
                    self.last_drive = self.clock()
                elif kind in ("stop", "release"):
                    self.stop(kind)
                else:
                    if not self.enabled:
                        raise ValueError("enable required")
                    self.forward = float(message["forward"])
                    self.turn = float(message["turn"])
                    self.last_drive = self.clock()
                    self.reason = "drive"
            except ValueError:
                self.stop("invalid command; enable required")
                raise

    def choice(self):
        with self.lock:
            self.expire()
            if not self.enabled or self.fault:
                return None
            f = self.forward if abs(self.forward) > self.config.deadzone else 0.0
            t = self.turn if abs(self.turn) > self.config.deadzone else 0.0
            magnitude = max(abs(f), abs(t))
            if magnitude == 0:
                return None
            # Discrete vendor gaits cannot blend arcs. Turn wins ties.
            action = ("turn left" if t > 0 else "turn right") if abs(t) >= abs(f) else (
                "forward" if f > 0 else "backward")
            fraction = (magnitude - self.config.deadzone) / (1 - self.config.deadzone)
            speed = round(self.config.min_speed + fraction * (self.config.max_speed - self.config.min_speed))
            return action, speed

    def snapshot(self):
        with self.lock:
            self.expire()
            return {"type": "status", "connected": self.connected, "enabled": self.enabled,
                    "forward": self.forward, "turn": self.turn, "seq": self.seq,
                    "reason": self.reason, "fault": self.fault, "busy": self.busy,
                    "action": self.action}
