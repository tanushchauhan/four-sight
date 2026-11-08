"""Exclusive debug control through SSH; the Pi keeps all motor validation."""
import json
import math
import queue
import secrets
import shlex
import subprocess
import threading
import time

REMOTE = r'''
import json, sys
from websockets.sync.client import connect
with open('/etc/picrawler-control.env') as f:
    token = next(line.split('=', 1)[1].strip() for line in f if line.startswith('ROBOT_CONTROL_TOKEN='))
with connect('ws://127.0.0.1:8765', open_timeout=4, close_timeout=1) as ws:
    ws.send(json.dumps({'type':'hello', 'token':token}))
    print(ws.recv(timeout=3), flush=True)
    seq = 0
    for line in sys.stdin:
        msg = json.loads(line)
        msg['seq'] = seq
        seq += 1
        ws.send(json.dumps(msg))
        print(ws.recv(timeout=2), flush=True)
        if msg['type'] == 'release': break
'''


class DebugControl:
    def __init__(self, target):
        self.target = target
        self.lock = threading.RLock()
        self.owner = None
        self.seq = -1
        self.received = 0
        self.armed = False
        self.axes = (0., 0.)
        self.generation = 0
        self.closing = threading.Event()
        self.status = {'connection': 'idle', 'enabled': False, 'reason': 'Take control to use the keyboard'}

    def snapshot(self):
        with self.lock:
            return dict(self.status)

    def acquire(self):
        with self.lock:
            if self.owner is not None:
                raise ValueError('Another tab owns debug control. Release it first.')
            self.owner = secrets.token_urlsafe(24)
            self.seq = -1
            self.received = time.monotonic()
            self.armed = False
            self.closing = threading.Event()
            self.status = {'connection': 'connecting', 'enabled': False, 'reason': 'Taking over from glove'}
            threading.Thread(target=self.run, daemon=True).start()
            return self.owner

    def update(self, data):
        with self.lock:
            if not self.owner or data.get('owner') != self.owner:
                raise ValueError('This tab does not own control')
            seq = data.get('seq')
            if type(seq) is not int or seq <= self.seq:
                raise ValueError('Old or invalid control packet')
            action = data.get('action')
            if action not in ('heartbeat', 'enable', 'stop', 'release'):
                raise ValueError('Unknown action')
            f, t = data.get('forward', 0), data.get('turn', 0)
            if any(type(v) not in (int, float) or not math.isfinite(v) or abs(v) > 1 for v in (f, t)):
                raise ValueError('Invalid axes')
            if action == 'enable':
                if self.status['connection'] != 'connected' or self.status.get('busy') or self.status.get('fault'):
                    raise ValueError('Robot is not ready to enable')
                self.armed = True
                self.axes = (0., 0.)
            elif action in ('stop', 'release'):
                self.disarm()
                if action == 'release': self.closing.set()
            else:
                self.axes = (float(f), float(t)) if self.armed else (0., 0.)
            self.seq, self.received = seq, time.monotonic()
            return self.snapshot()

    def disarm(self):
        self.armed = False
        self.axes = (0., 0.)
        self.generation += 1

    def desired(self):
        with self.lock:
            if time.monotonic() - self.received > .3:
                self.disarm()
            if time.monotonic() - self.received > 15:
                self.closing.set()
            return self.armed, self.axes, self.generation

    def ssh(self, command):
        return ['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=4',
                '-o', 'ServerAliveInterval=2', '-o', 'ServerAliveCountMax=1', self.target, command]

    def run(self):
        process = None
        restore = False
        try:
            p = subprocess.run(self.ssh('systemctl is-active prada-joystick'), capture_output=True, text=True, timeout=7)
            restore = p.stdout.strip() == 'active'
            subprocess.run(self.ssh('sudo -n systemctl stop prada-joystick'), check=True, capture_output=True, timeout=8)
            command = 'sudo -n /home/liquidiv/hackutd26/robot/.venv/bin/python -u -c ' + shlex.quote(REMOTE)
            process = subprocess.Popen(self.ssh(command), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                       stderr=subprocess.DEVNULL, text=True, bufsize=1)
            replies = queue.Queue(maxsize=1)
            def reader():
                for line in process.stdout:
                    replies.put(line)
                replies.put('')
            threading.Thread(target=reader, daemon=True).start()
            def receive():
                try: raw = replies.get(timeout=5)
                except queue.Empty: raise RuntimeError('Robot control response timed out')
                if not raw: raise RuntimeError('Robot control connection closed')
                status = json.loads(raw)
                if status.get('type') != 'status': raise RuntimeError(status.get('message', 'Unexpected robot response'))
                with self.lock:
                    self.status = dict(status, connection='connected')
                return status
            receive()
            enabled = False
            previous_generation = self.generation
            def send(kind, **axes):
                process.stdin.write(json.dumps(dict(type=kind, **axes)) + '\n')
                process.stdin.flush()
                return receive()
            while not self.closing.is_set():
                armed, axes, generation = self.desired()
                if generation != previous_generation or (enabled and not armed):
                    send('stop'); enabled = False
                    previous_generation = generation
                if armed and not enabled:
                    reply = send('enable'); enabled = reply['enabled']
                if enabled:
                    # Enabling/acknowledgement can take time: never use axes
                    # captured before an intervening stop or stale heartbeat.
                    current_armed, axes, current_generation = self.desired()
                    if not current_armed or current_generation != generation:
                        send('stop')
                        enabled = False
                    else:
                        reply = send('drive', forward=axes[0], turn=axes[1])
                        enabled = reply['enabled']
                        if not enabled:
                            with self.lock: self.disarm()
                if not enabled and not armed:
                    send('stop')  # Refresh busy/fault state while disarmed.
                self.closing.wait(.05)
            send('release')
        except Exception as exc:
            with self.lock:
                self.status = {'connection': 'error', 'enabled': False, 'reason': str(exc)[:250]}
        finally:
            if process:
                process.terminate()
                try: process.wait(timeout=2)
                except subprocess.TimeoutExpired: process.kill()
            restore_error = None
            if restore:
                try:
                    subprocess.run(self.ssh('sudo -n systemctl start prada-joystick'), check=True, capture_output=True, timeout=8)
                except Exception:
                    restore_error = 'Could not restore glove relay; restart it manually'
            with self.lock:
                self.owner = None
                self.disarm()
                if restore_error:
                    self.status = {'connection':'error', 'enabled':False, 'reason':restore_error}
                elif self.status.get('connection') != 'error':
                    self.status = {'connection':'idle', 'enabled':False, 'reason':'Released; glove relay restored' if restore else 'Released'}
