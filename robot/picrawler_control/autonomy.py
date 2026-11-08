"""Reactive exploration. No localization; invalid range always inhibits motion."""
import math
import threading
import time


class Avoidance:
    def __init__(self, clock=time.monotonic):
        self.clock = clock
        self.turn_started = None
        self.fault = None
        self.clear_count = 0
        self.last_clear_sample = None
        self.had_valid_reading = False

    def choose(self, cm, sampled_at, speed):
        now = self.clock()
        if self.fault:
            return 0., 0., self.fault
        if (type(cm) not in (int, float) or not math.isfinite(cm) or not 2 <= cm <= 400
                or sampled_at is None or not 0 <= now - sampled_at < .5):
            self.clear_count = 0
            if self.had_valid_reading:
                self.fault = 'Ultrasonic reading lost; restart explicitly'
            return 0., 0., self.fault or 'Waiting for fresh ultrasonic reading'
        self.had_valid_reading = True
        if cm < 15:
            self.fault = 'Obstacle too close; manual reposition required'
            return 0., 0., self.fault
        if cm < 40 or self.turn_started is not None:
            if self.turn_started is None:
                self.turn_started = now
            if cm >= 50:
                if sampled_at != self.last_clear_sample:
                    self.clear_count += 1
                    self.last_clear_sample = sampled_at
                if self.clear_count >= 3:
                    self.turn_started = None
                    self.clear_count = 0
                    return 0., 0., 'Path clear; checking before forward'
            else:
                self.clear_count = 0
            if now - self.turn_started > 4:
                self.fault = 'No clear route; manual reposition required'
                return 0., 0., self.fault
            return 0., min(speed, .5), 'Turning away from obstacle'
        return speed, 0., 'Exploring'


class Sonar:
    """Dedicated reader: a stuck vendor read becomes stale without blocking stop."""
    def __init__(self):
        self.sample = (None, None)
        self.error = None
        threading.Thread(target=self.run, daemon=True).start()

    def run(self):
        try:
            from robot_hat import Ultrasonic, Pin
            sensor = Ultrasonic(Pin('D2'), Pin('D3'))
            while True:
                started = time.monotonic()
                distance = sensor.read()
                # Timestamp the start, not the end, of a potentially slow read.
                self.sample = (distance, started)
                time.sleep(.06)
        except Exception as exc:
            self.error = str(exc)
            self.sample = (None, None)
