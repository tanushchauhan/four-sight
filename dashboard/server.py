"""Prada LAN dashboard with exclusive local debug control. No third-party dependencies."""
import argparse
import urllib.request
import urllib.error
from mission import Mission, Media, settings
import base64
import json
import secrets
import hmac
from control import DebugControl
from usb_glove import USBGlove
import re
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

# Latest frame only, in memory. Frames expire automatically; no recording is kept.
class VisorFrames:
    def __init__(self):
        self.lock = threading.Lock()
        self.frame = None
        self.received = 0

    def put(self, frame):
        if not 4 <= len(frame) <= 400_000 or not frame.startswith(b'\xff\xd8') or not frame.endswith(b'\xff\xd9'):
            raise ValueError('Expected JPEG under 400 KB')
        with self.lock:
            self.frame, self.received = frame, time.monotonic()

    def get(self):
        with self.lock:
            age = time.monotonic() - self.received
            return (self.frame if age < 12 else None), age

VISOR_FRAMES = VisorFrames()

# Executed on the Pi with its system Python, never the motor-control connection.
PROBE = r'''
import base64, json, pathlib, subprocess, time, urllib.request
def command(args):
    try:
        p = subprocess.run(args, capture_output=True, text=True, timeout=3)
        return p.stdout.strip() if p.returncode == 0 else None
    except (OSError, subprocess.TimeoutExpired):
        return None
services = {}
for name in ('prada-server', 'prada-joystick', 'prada-camera', 'prada-speech'):
    services[name] = command(['systemctl', 'show', name, '--property=ActiveState', '--value']) or 'unknown'
stick = command(['bluetoothctl', 'info', '98:4F:EE:0F:35:0C'])
sockets = command(['ss', '-Htn', 'state', 'established', '( sport = :8766 )'])
try: uptime = float(pathlib.Path('/proc/uptime').read_text().split()[0])
except (OSError, ValueError): uptime = None
try: temperature = int(pathlib.Path('/sys/class/thermal/thermal_zone0/temp').read_text()) / 1000
except (OSError, ValueError): temperature = None
battery = None
try:
    reading = json.loads(pathlib.Path('/tmp/prada-battery.json').read_text())
    if reading['boot'] == pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip() and 0 <= time.monotonic()-reading['sampled'] < 15:
        battery = reading['voltage']
except (OSError, ValueError, KeyError, TypeError): pass
camera = {'ok': False, 'bytes': 0, 'image': None}
try:
    with urllib.request.urlopen('http://127.0.0.1:8080/frame', timeout=2) as response:
        frame = response.read(2 * 1024 * 1024 + 1)
        if len(frame) <= 2 * 1024 * 1024 and frame.startswith(b'\xff\xd8') and frame.endswith(b'\xff\xd9'):
            camera = {'ok': True, 'bytes': len(frame), 'image': base64.b64encode(frame).decode()}
except Exception: pass
print(json.dumps({'services': services, 'joystick': None if stick is None else 'Connected: yes' in stick,
    'battery_voltage': battery, 'headset': None if sockets is None else bool(sockets), 'uptime': uptime, 'temperature': temperature,
    'throttled': command(['vcgencmd', 'get_throttled']),
    'revision': command(['git', '-C', '/home/liquidiv/hackutd26', 'rev-parse', '--short', 'HEAD']),
    'camera': camera}))
'''


def collect(target):
    started = time.monotonic()
    try:
        result = subprocess.run(
            ['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=4',
             '-o', 'ConnectionAttempts=1', '-o', 'ServerAliveInterval=3',
             '-o', 'ServerAliveCountMax=1', target, 'python3', '-'],
            input=PROBE, text=True, capture_output=True, timeout=18)
        if result.returncode:
            raise RuntimeError(result.stderr.strip().splitlines()[-1] if result.stderr.strip() else 'SSH connection failed')
        data = json.loads(result.stdout)
        if not isinstance(data, dict) or 'camera' not in data:
            raise ValueError('Invalid status response')
        encoded = data['camera'].pop('image', None)
        image = base64.b64decode(encoded, validate=True) if encoded else None
        data.update(reachable=True, error=None)
    except (OSError, subprocess.TimeoutExpired, RuntimeError, ValueError, KeyError, TypeError) as exc:
        data = {'reachable': False, 'error': str(exc)[:400], 'services': {},
                'camera': {'ok': False}, 'joystick': None, 'headset': None}
        image = None
    data.update(checked_at=time.time(), target=target,
                check_seconds=round(time.monotonic() - started, 2))
    return data, image


class Monitor:
    def __init__(self, target, interval):
        self.target, self.interval = target, interval
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.data = {'reachable': None, 'checked_at': None, 'target': target, 'services': {},
                     'camera': {'ok': False}, 'joystick': None, 'headset': None}
        self.image = None
        self.last_seen = None

    def run(self):
        while not self.stop.is_set():
            data, image = collect(self.target)
            with self.lock:
                if data['reachable']:
                    self.last_seen = data['checked_at']
                data['last_seen'] = self.last_seen
                self.data, self.image = data, image
            self.stop.wait(self.interval)

    def snapshot(self):
        with self.lock:
            data = dict(self.data)
        data['stale_after'] = self.interval + 25
        return data


def local_control_client(peer, local_address, host):
    """Accept this Mac over loopback or its own interface, never remote LAN peers."""
    return (peer in ('127.0.0.1', '::1') or peer == local_address) and host in (
        'localhost', '127.0.0.1', local_address)


class Handler(BaseHTTPRequestHandler):
    def control_allowed(self):
        return local_control_client(self.client_address[0], self.connection.getsockname()[0],
                                    self.headers.get('Host', '').split(':')[0])

    def do_GET(self):
        path = self.path.split('?', 1)[0]
        if path in ('/api/visor/frame', '/api/visor/status'):
            frame, age = VISOR_FRAMES.get()
            if path.endswith('/status'):
                self.send_body(200, 'application/json', json.dumps({'connected': frame is not None, 'age_seconds': round(age, 1) if frame else None}).encode())
            else:
                self.send_body(200 if frame else 404, 'image/jpeg' if frame else 'text/plain', frame or b'No live visor frame')
            return
        if path == '/api/shared' or path == '/api/damage' or path.startswith('/api/damage/'):
            self.damage_proxy()
        elif path in ('/damage', '/damage.js'):
            name = 'damage.js' if path.endswith('.js') else 'damage.html'
            kind = 'text/javascript; charset=utf-8' if name.endswith('.js') else 'text/html; charset=utf-8'
            self.send_body(200, kind, Path(__file__).with_name(name).read_text().replace('__CONTROL_NONCE__', self.server.nonce).replace('__CONTROL_ALLOWED__', 'true' if self.control_allowed() else 'false').encode())
        elif path == '/api/status':
            self.send_body(200, 'application/json', json.dumps(dict(self.server.monitor.snapshot(), control=self.server.control.snapshot(), usb=self.server.usb.snapshot(), mission=self.server.mission.snapshot())).encode())
        elif path in ('/api/perception', '/api/perception/frame'):
            endpoint = '/frame' if path.endswith('/frame') else '/perception'
            try:
                with urllib.request.urlopen('http://127.0.0.1:8790' + endpoint, timeout=1) as response:
                    self.send_body(200, response.headers.get('Content-Type', 'application/json'), response.read(2_000_000))
            except Exception:
                self.send_body(503, 'application/json', b'{"ready":false,"action":"wait","reason":"Local perception unavailable"}')
        elif path == '/api/mission':
            self.send_body(200, 'application/json', json.dumps(self.server.mission.snapshot()).encode())
        elif path == '/api/agent/recent':
            try:
                with urllib.request.urlopen(self.server.media.agent + '/api/operator/recent?n=12', timeout=2) as r:
                    self.send_body(200, 'application/json', r.read(64000))
            except Exception:
                self.send_body(503, 'application/json', b'{"error":"Agent unavailable"}')
        elif path == '/api/camera/live':
            started = False
            try:
                with urllib.request.urlopen(self.server.media.camera + '/stream', timeout=4) as r:
                    started = True
                    self.send_response(200)
                    self.send_header('Content-Type', 'multipart/x-mixed-replace; boundary=frame')
                    self.send_header('Cache-Control', 'no-store')
                    self.end_headers()
                    while True:
                        chunk = r.read1(65536)
                        if not chunk: break
                        self.wfile.write(chunk)
                        self.wfile.flush()
            except Exception:
                if not started:
                    self.send_body(503, 'text/plain', b'Robot camera unavailable')
                self.close_connection = True
        elif path == '/api/frame':
            with self.server.monitor.lock:
                frame = self.server.monitor.image
            self.send_body(200 if frame else 503, 'image/jpeg' if frame else 'text/plain', frame or b'No camera image')
        elif path == '/':
            self.send_body(200, 'text/html; charset=utf-8', Path(__file__).with_name('index.html').read_text().replace('__CONTROL_NONCE__', self.server.nonce).replace('__CONTROL_ALLOWED__', 'true' if self.control_allowed() else 'false').encode())
        elif path in ('/console.css', '/tokens.css', '/base.css'):
            file = Path(__file__).with_name(path[1:])
            if file.is_file():
                self.send_body(200, 'text/css; charset=utf-8', file.read_bytes())
            else:
                self.send_body(404, 'text/plain', b'Not found')
        else:
            self.send_body(404, 'text/plain', b'Not found')

    def do_POST(self):
        if self.path == '/api/visor/frame':
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if not 4 <= length <= 400_000: raise ValueError('Frame too large')
                if self.headers.get('Content-Type') != 'image/jpeg': raise ValueError('Expected image/jpeg')
                self.connection.settimeout(4)
                frame = self.rfile.read(length)
                if len(frame) != length: raise ValueError('Incomplete frame')
                VISOR_FRAMES.put(frame)
                self.send_body(200, 'application/json', b'{"ok":true}')
            except (ValueError, OSError) as exc:
                self.send_body(400, 'application/json', json.dumps({'error': str(exc)}).encode())
            return
        if self.path in ('/api/agent/heartbeat', '/api/agent/person', '/api/agent/handoff'):
            token = self.server.media.agent_token
            if not token or not hmac.compare_digest(self.headers.get('Authorization', ''), 'Bearer ' + token):
                self.send_body(401, 'application/json', b'{"error":"Unauthorized"}')
                return
            try:
                length = int(self.headers.get('Content-Length', '0'))
                if not 0 < length <= 4096: raise ValueError('Invalid body size')
                data = json.loads(self.rfile.read(length))
                if not isinstance(data, dict): raise ValueError('Expected object')
                self.server.mission.agent_seen = time.monotonic()
                result = (self.server.mission.person(data) if self.path.endswith('/person') else
                          self.server.mission.update_handoff(data) if self.path.endswith('/handoff') else self.server.mission.snapshot())
                self.send_body(200, 'application/json', json.dumps(result).encode())
            except (ValueError, TypeError):
                self.send_body(400, 'application/json', b'{"error":"Invalid request"}')
            return
        # Control is local to this Mac; LAN viewers get status only. The nonce plus
        # exact Origin check prevents cross-site requests from driving the robot.
        origin = self.headers.get('Origin', '')
        expected = 'http://' + self.headers.get('Host', '')
        if (not self.control_allowed() or origin != expected
                or not hmac.compare_digest(self.headers.get('X-Control-Nonce', ''), self.server.nonce)):
            self.send_body(403, 'application/json', b'{"error":"Use this Mac and its dashboard to control the robot"}')
            return
        try:
            length = int(self.headers.get('Content-Length', '0'))
            if self.path == '/api/audio/play':
                if not 44 <= length <= 1000000: raise ValueError('Audio clip must be under 10 seconds')
                if self.server.mission.mode != 'manual': raise ValueError('Take manual control before speaking')
                result = self.server.media.request('/audio', self.rfile.read(length), raw=True)
                self.send_body(200, 'application/json', result)
                return
            if not 0 < length <= (10000 if self.path.startswith('/api/damage/') else 2048): raise ValueError('Invalid request size')
            data = json.loads(self.rfile.read(length))
            if not isinstance(data, dict): raise ValueError('Expected an object')
            if self.path in ('/api/damage/config', '/api/damage/analyze', '/api/damage/review'):
                self.damage_proxy(data)
                return
            if self.path.startswith('/api/control/') and self.path != '/api/control/input':
                if self.server.mission.mode in ('autonomous', 'watching', 'person_found') or self.server.mission.owner or (self.server.control.mode == 'auto' and self.server.control.owner):
                    self.server.mission.stop('Manual takeover requested', 'manual')
                    self.server.control.close()
                    try: self.server.media.request('/session', {'mode': 'manual'})
                    except Exception: pass
                if self.path in ('/api/control/acquire', '/api/control/usb', '/api/control/release'):
                    self.server.mission.mode = 'manual'
                    try: self.server.media.request('/session', {'mode': 'manual'})
                    except Exception: pass  # Driving takeover never depends on the speaker.
            if self.path == '/api/mission/config':
                result = self.server.mission.configure(data)
            elif self.path == '/api/mission/watch':
                result = self.server.mission.watch()
            elif self.path == '/api/mission/start':
                result = self.server.mission.start()
            elif self.path == '/api/mission/stop':
                result = self.server.mission.stop()
                try: self.server.media.request('/session', {'mode': 'manual'})
                except Exception: pass
            elif self.path == '/api/audio/session':
                self.server.mission.stop('Responder conversation', 'manual')
                result = json.loads(self.server.media.request('/session', {'mode': 'manual'}))
            elif self.path == '/api/audio/say':
                if self.server.mission.mode != 'manual': raise ValueError('Take manual control before speaking')
                result = json.loads(self.server.media.request('/say', {'text': data.get('text'), 'interrupt': True}))
            elif self.path == '/api/audio/listen':
                if self.server.mission.mode != 'manual': raise ValueError('Take manual control before listening')
                audio = self.server.media.request('/record', {'seconds': 5})
                self.send_body(200, 'audio/wav', audio)
                return
            elif self.path == '/api/control/acquire':
                result = {'owner': self.server.control.acquire(), 'control': self.server.control.snapshot()}
            elif self.path == '/api/control/stop':
                result = self.server.control.stop_motion()
            elif self.path == '/api/control/usb':
                result = self.server.usb.acquire()
            elif self.path in ('/api/control/hello', '/api/control/dance'):
                result = self.server.control.gesture(self.path.rsplit('/', 1)[1])
            elif self.path == '/api/control/tilt':
                result = self.server.control.tilt_back()
            elif self.path == '/api/control/zero':
                result = self.server.control.zero_servos()
            elif self.path == '/api/control/release':
                result = self.server.control.release()
            elif self.path == '/api/control/input':
                result = self.server.control.update(data)
            else:
                self.send_body(404, 'application/json', b'{"error":"Not found"}')
                return
            self.send_body(200, 'application/json', json.dumps(result).encode())
        except (ValueError, TypeError, OSError) as exc:
            self.send_body(400, 'application/json', json.dumps({'error': str(exc)}).encode())

    def damage_proxy(self, data=None):
        headers = {}
        if data is not None:
            headers = {'Content-Type': 'application/json', 'Authorization': 'Bearer ' + self.server.media.agent_token}
        request = urllib.request.Request(self.server.media.agent + self.path,
            data=json.dumps(data).encode() if data is not None else None, headers=headers)
        try:
            response = urllib.request.urlopen(request, timeout=40)
        except urllib.error.HTTPError as exc:
            response = exc
        except (OSError, ValueError):
            self.send_body(503, 'application/json', b'{"error":"Damage agent unavailable. Start the agent and try again."}')
            return
        with response:
            self.send_response(response.code)
            self.send_header('Content-Type', response.headers.get('Content-Type', 'application/json'))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            if response.headers.get('Content-Disposition'):
                self.send_header('Content-Disposition', response.headers['Content-Disposition'])
            self.end_headers()
            try:
                while True:
                    chunk = response.read(65536)
                    if not chunk: break
                    self.wfile.write(chunk)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def send_body(self, status, kind, body):
        self.send_response(status)
        self.send_header('Content-Type', kind)
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, *_):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--robot', default='prada', help='SSH host alias or user@host')
    parser.add_argument('--host', default='0.0.0.0', help='Use 127.0.0.1 for this Mac only')
    parser.add_argument('--port', type=int, default=8090)
    parser.add_argument('--interval', type=float, default=5)
    args = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.@:-]*', args.robot):
        parser.error('Invalid SSH target')
    if args.interval < 1:
        parser.error('Interval must be at least one second')
    monitor = Monitor(args.robot, args.interval)
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.monitor = monitor
    server.control = DebugControl(args.robot)
    server.media = Media(settings())
    from perception_runtime import PerceptionRuntime
    perception_runtime = PerceptionRuntime()
    perception_runtime.start()
    server.mission = Mission(server.control, server.media)
    from person_watch import PersonWatch
    person_watch = PersonWatch(server.mission, server.media)
    person_watch.start()
    server.usb = USBGlove(server.control)
    def glove_takeover():
        server.mission.stop('Glove requested manual control', 'manual')
        server.control.close()
        try:
            server.usb.acquire()
            try: server.media.request('/session', {'mode': 'manual'})
            except Exception: pass
        except ValueError as exc:
            server.mission.reason = str(exc)
    server.usb.can_takeover = lambda: server.mission.mode in ('autonomous', 'watching', 'person_found')
    server.usb.takeover = glove_takeover
    server.usb.worker.start()
    server.nonce = secrets.token_urlsafe(32)
    from live_audio import start as start_live_audio
    start_live_audio(server)
    threading.Thread(target=monitor.run, daemon=True).start()
    print(f'Prada status: http://localhost:{args.port} (listening on {args.host})', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        person_watch.close()
        server.mission.close()
        perception_runtime.close()
        server.usb.close()
        server.control.close()
        monitor.stop.set()
        server.server_close()


if __name__ == '__main__':
    main()
