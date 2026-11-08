"""USB Arduino 101 receiver. Merely attaching the glove never enables the robot."""
import threading
import time


def parse_packet(line):
    parts = line.decode('ascii').strip().split(',')
    if len(parts) != 5 or parts[0] != 'J':
        raise ValueError('Not a joystick packet')
    seq, turn, forward, clicks = map(int, parts[1:])
    if not (0 <= seq < 2**32 and -127 <= turn <= 127 and -127 <= forward <= 127 and 0 <= clicks <= 255):
        raise ValueError('Joystick packet out of range')
    return seq, -turn / 127, forward / 127, clicks


class USBGlove:
    def __init__(self, control):
        self.control = control
        self.lock = threading.RLock()
        self.stop = threading.Event()
        self.received = 0
        self.port = None
        self.owner = None
        self.seq = 0
        self.packet_seq = None
        self.clicks = None
        self.forward = self.turn = 0.
        self.reason = 'Looking for the USB Arduino 101'
        self.worker = threading.Thread(target=self.run, daemon=True)

    def snapshot(self):
        with self.lock:
            return dict(connected=self.port is not None and time.monotonic()-self.received < .3,
                        port=getattr(self.port, 'port', None), reason=self.reason,
                        forward=self.forward, turn=self.turn,
                        age=round(time.monotonic()-self.received,2) if self.received else None,
                        controlling=self.owner is not None and self.control.owner == self.owner)

    def acquire(self):
        with self.lock:
            if not self.snapshot()['connected']:
                raise ValueError('Connect the USB glove and wait for fresh packets')
            owner = self.control.acquire(mode='usb')
            self.owner, self.seq, self.clicks = owner, 0, None
            self.reason = 'USB control starting; keep the stick centered'
            return self.snapshot()

    def disable(self, reason):
        with self.lock:
            self.reason = reason
            if self.owner is not None and self.control.owner == self.owner:
                self.control.release()
            self.owner = None
            self.clicks = None

    def accept(self, packet):
        seq, turn, forward, clicks = packet
        with self.lock:
            if self.received and time.monotonic() - self.received > .3:
                self.disable('USB data was stale; take USB control again')
            if self.packet_seq is not None and seq <= self.packet_seq:
                self.disable('USB controller restarted; take USB control again')
            self.packet_seq = seq
            self.received = time.monotonic()
            self.forward, self.turn = forward, turn
            if not self.owner or self.control.owner != self.owner:
                self.owner = None
                self.reason = 'USB packets received; take USB control to drive'
                return
            status = self.control.snapshot()
            centered = abs(forward) < .15 and abs(turn) < .15
            action = 'heartbeat'
            feedback = None
            changed = self.clicks is not None and clicks != self.clicks
            if status['connection'] == 'connected' and changed:
                delta = (clicks-self.clicks) % 256
                if self.control.armed:
                    action, feedback = 'stop', b'D'
                elif centered and delta == 1:
                    action, feedback = 'enable', b'E'
                else:
                    action, feedback = 'stop', b'R'
            self.clicks = clicks
            if status['connection'] != 'connected':
                self.reason = 'Waiting for robot control connection'
                return
            try:
                self.control.update(dict(owner=self.owner, seq=self.seq, action=action,
                                         forward=0 if centered else forward, turn=0 if centered else turn))
                self.seq += 1
                self.reason = 'USB control enabled' if self.control.armed else 'USB ready; click centered stick to enable'
                if feedback and self.port: self.port.write(feedback)
            except ValueError as exc:
                self.reason = str(exc)
                with self.control.lock: self.control.disarm()
                if self.port: self.port.write(b'R')

    def run(self):
        try:
            import serial
            from serial.tools import list_ports
        except ImportError:
            self.reason = 'USB requires pyserial; run the dashboard with robot/.venv/bin/python'
            return
        while not self.stop.is_set():
            try:
                devices = [p.device for p in list_ports.comports() if (p.vid,p.pid)==(0x8087,0x0ab6)]
                if not devices:
                    self.stop.wait(1)
                    continue
                with serial.Serial(devices[0],115200,timeout=.1,write_timeout=.1,exclusive=True) as port:
                    with self.lock:
                        self.port = port
                        self.packet_seq = None
                        self.clicks = None
                        self.received = 0
                    port.reset_input_buffer()
                    while not self.stop.is_set():
                        if port.in_waiting > 1024:
                            port.reset_input_buffer()
                            self.disable('USB backlog discarded; take control again')
                        line = port.read_until(b'\n', size=96)
                        if line.endswith(b'\n'):
                            try: self.accept(parse_packet(line))
                            except (ValueError, UnicodeError): pass
                        if self.received and time.monotonic()-self.received > .3:
                            self.disable('USB packets stopped; movement disabled')
            except (OSError, serial.SerialException) as exc:
                self.disable('USB disconnected: ' + str(exc)[:150])
            finally:
                with self.lock: self.port = None
            self.stop.wait(1)
        self.disable('USB receiver stopped')

    def close(self):
        self.stop.set()
        if self.worker.is_alive(): self.worker.join(timeout=2)
        self.disable('USB receiver stopped')
