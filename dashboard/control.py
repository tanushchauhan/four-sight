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
import asyncio, json, sys, threading
from websockets.sync.client import connect
from websockets.asyncio.server import serve
from picrawler_control.ble_relay import HeadInput
HEAD_PORT = 8766
INPUT_MODE = 'keyboard'
head = HeadInput()
ready = threading.Event()
head_error = []
async def serve_head():
    try:
        async with serve(head.handle, '0.0.0.0', HEAD_PORT, max_size=1024, max_queue=1, compression=None):
            ready.set()
            await asyncio.Future()
    except Exception as exc:
        head_error.append(str(exc))
        ready.set()
threading.Thread(target=lambda: asyncio.run(serve_head()), daemon=True).start()
if not ready.wait(4) or head_error:
    raise RuntimeError('Head steering listener unavailable: ' + '; '.join(head_error))
with open('/etc/picrawler-control.env') as f:
    token = next(line.split('=', 1)[1].strip() for line in f if line.startswith('ROBOT_CONTROL_TOKEN='))
with connect('ws://127.0.0.1:8765', open_timeout=4, close_timeout=1) as ws:
    def report(timeout):
        reply = json.loads(ws.recv(timeout=timeout))
        reply.update(headset_connected=head.owner is not None, head_turn=head.value(), input_source=source)
        print(json.dumps(reply), flush=True)
    source = 'disabled'
    ws.send(json.dumps({'type':'hello', 'token':token}))
    report(3)
    seq = 0
    for line in sys.stdin:
        msg = json.loads(line)
        source = 'disabled'
        if msg['type'] == 'drive':
            if msg['forward'] == 0 and msg['turn'] == 0:
                msg['turn'] = head.value()
                source = 'headset' if msg['turn'] else 'neutral'
            else:
                source = INPUT_MODE
        msg['seq'] = seq
        seq += 1
        ws.send(json.dumps(msg))
        report(2)
        if msg['type'] == 'release': break
'''



class DebugControl:
    def __init__(self, target):
        self.target = target
        self.lock = threading.RLock()
        self.owner = None
        self.mode = 'keyboard'
        self.maintenance = False
        self.revision = 0
        self.seq = -1
        self.received = 0
        self.armed = False
        self.axes = (0., 0.)
        self.generation = 0
        self.closing = threading.Event()
        self.status = {'connection': 'idle', 'enabled': False, 'reason': 'Take control to use the keyboard'}

    def snapshot(self):
        with self.lock:
            self.revision += 1
            return dict(self.status, revision=self.revision, mode=self.mode)

    def acquire(self, mode="keyboard"):
        with self.lock:
            if self.maintenance:
                raise ValueError('Servo zero operation is in progress')
            if self.owner is not None:
                raise ValueError('Another tab owns debug control. Release it first.')
            if mode not in ('keyboard', 'usb'):
                raise ValueError('Unknown controller mode')
            self.mode = mode
            self.owner = secrets.token_urlsafe(24)
            self.seq = -1
            self.received = time.monotonic()
            self.armed = False
            self.closing = threading.Event()
            self.status = {'connection': 'connecting', 'enabled': False, 'reason': 'Taking over from glove'}
            self.worker = threading.Thread(target=self.run, daemon=True)
            self.worker.start()
            return self.owner

    def update(self, data):
        with self.lock:
            if self.maintenance:
                raise ValueError('Servo zero operation is in progress')
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

    def tilt_back(self):
        with self.lock:
            if self.maintenance:
                raise ValueError('A robot pose operation is already running')
            self.maintenance = True
            self.disarm()
            self.closing.set()
            self.status = {'connection': 'maintenance', 'enabled': False,
                           'reason': 'Preparing backward tilt test; drive paused'}
            threading.Thread(target=self.run_zero, args=('tilt',), daemon=True).start()
            return self.snapshot()

    def zero_servos(self):
        with self.lock:
            if self.maintenance:
                raise ValueError('Servo zero operation is already running')
            self.maintenance = True
            self.disarm()
            self.closing.set()
            threading.Thread(target=self.run_zero, daemon=True).start()
            return {'connection': 'maintenance', 'enabled': False, 'reason': 'Stopping control before moving servos to zero'}

    def run_zero(self, operation="zero"):
        restore = False
        try:
            self.close()
            worker = getattr(self, 'worker', None)
            if worker is not None and worker.is_alive():
                raise RuntimeError('Control did not release; no zero command sent')
            with self.lock:
                self.status = {'connection':'maintenance', 'enabled':False, 'reason':'Tilting backward, holding 3 seconds, then returning level' if operation == 'tilt' else 'Moving 12 servos to calibrated zero'}
            p = subprocess.run(self.ssh('systemctl is-active prada-joystick'), capture_output=True, text=True, timeout=7)
            restore = p.stdout.strip() == 'active'
            subprocess.run(self.ssh('sudo -n systemctl stop prada-joystick prada-server'), check=True, capture_output=True, timeout=30)
            script = "from picrawler_control.backend import Hardware; h=Hardware(); h.initialize(); h.close(); print('calibrated zero complete')"
            command = ('/home/liquidiv/hackutd26/robot/.venv/bin/python -m picrawler_control.tilt' if operation == 'tilt' else '/home/liquidiv/hackutd26/robot/.venv/bin/python -c ' + shlex.quote(script))
            subprocess.run(self.ssh(command), check=True, capture_output=True, timeout=60 if operation == 'tilt' else 15)
            subprocess.run(self.ssh('sudo -n systemctl start prada-server' + (' prada-joystick' if restore else '')), check=True, capture_output=True, timeout=10)
            status = {'connection':'idle', 'enabled':False, 'reason':('Tilt completed; check whether the AirTag dropped. ' if operation == 'tilt' else 'Servos at calibrated zero. ') + 'Control restarted disabled; take control to resume.'}
        except Exception as exc:
            status = {'connection':'error', 'enabled':False, 'reason':('Tilt test failed: ' if operation == 'tilt' else 'Servo zero failed: ') + str(exc)[:250] + '. Check robot services before resuming.'}
        finally:
            with self.lock:
                self.maintenance = False
                self.status = status

    def close(self):
        self.release()
        worker = getattr(self, 'worker', None)
        if worker is not None:
            worker.join(timeout=25)

    def stop_motion(self):
        with self.lock:
            self.disarm()
            return self.snapshot()

    def release(self):
        with self.lock:
            self.disarm()
            self.closing.set()
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
            command = 'sudo -n /home/liquidiv/hackutd26/robot/.venv/bin/python -u -c ' + shlex.quote(REMOTE.replace("INPUT_MODE = 'keyboard'", 'INPUT_MODE = ' + repr(self.mode)))
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
