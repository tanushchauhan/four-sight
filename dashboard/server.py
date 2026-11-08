"""Prada LAN dashboard with exclusive local debug control. No third-party dependencies."""
import argparse
import base64
import json
import secrets
import hmac
from control import DebugControl
import re
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

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
for name in ('prada-server', 'prada-joystick', 'prada-camera'):
    services[name] = command(['systemctl', 'show', name, '--property=ActiveState', '--value']) or 'unknown'
stick = command(['bluetoothctl', 'info', '98:4F:EE:0F:35:0C'])
sockets = command(['ss', '-Htn', 'state', 'established', '( sport = :8766 )'])
try: uptime = float(pathlib.Path('/proc/uptime').read_text().split()[0])
except (OSError, ValueError): uptime = None
try: temperature = int(pathlib.Path('/sys/class/thermal/thermal_zone0/temp').read_text()) / 1000
except (OSError, ValueError): temperature = None
camera = {'ok': False, 'bytes': 0, 'image': None}
try:
    with urllib.request.urlopen('http://127.0.0.1:8080/frame', timeout=2) as response:
        frame = response.read(2 * 1024 * 1024 + 1)
        if len(frame) <= 2 * 1024 * 1024 and frame.startswith(b'\xff\xd8') and frame.endswith(b'\xff\xd9'):
            camera = {'ok': True, 'bytes': len(frame), 'image': base64.b64encode(frame).decode()}
except Exception: pass
print(json.dumps({'services': services, 'joystick': None if stick is None else 'Connected: yes' in stick,
    'headset': None if sockets is None else bool(sockets), 'uptime': uptime, 'temperature': temperature,
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


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = self.path.split('?', 1)[0]
        if path == '/api/status':
            self.send_body(200, 'application/json', json.dumps(dict(self.server.monitor.snapshot(), control=self.server.control.snapshot())).encode())
        elif path == '/api/frame':
            with self.server.monitor.lock:
                frame = self.server.monitor.image
            self.send_body(200 if frame else 503, 'image/jpeg' if frame else 'text/plain', frame or b'No camera image')
        elif path == '/':
            self.send_body(200, 'text/html; charset=utf-8', Path(__file__).with_name('index.html').read_text().replace('__CONTROL_NONCE__', self.server.nonce).encode())
        else:
            self.send_body(404, 'text/plain', b'Not found')

    def do_POST(self):
        # Control is local to this Mac; LAN viewers get status only. The nonce plus
        # exact Origin check prevents cross-site requests from driving the robot.
        origin = self.headers.get('Origin', '')
        expected = 'http://' + self.headers.get('Host', '')
        if (self.headers.get('Host', '').split(':')[0] not in ('localhost', '127.0.0.1')
                or self.client_address[0] not in ('127.0.0.1', '::1') or origin != expected
                or not hmac.compare_digest(self.headers.get('X-Control-Nonce', ''), self.server.nonce)):
            self.send_body(403, 'application/json', b'{"error":"Use this Mac and its dashboard to control the robot"}')
            return
        try:
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= 2048: raise ValueError('Invalid request size')
            data = json.loads(self.rfile.read(length))
            if not isinstance(data, dict): raise ValueError('Expected an object')
            if self.path == '/api/control/acquire':
                result = {'owner': self.server.control.acquire()}
            elif self.path == '/api/control/input':
                result = self.server.control.update(data)
            else:
                self.send_body(404, 'application/json', b'{"error":"Not found"}')
                return
            self.send_body(200, 'application/json', json.dumps(result).encode())
        except (ValueError, TypeError) as exc:
            self.send_body(400, 'application/json', json.dumps({'error': str(exc)}).encode())

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
    server.nonce = secrets.token_urlsafe(32)
    threading.Thread(target=monitor.run, daemon=True).start()
    print(f'Prada status: http://localhost:{args.port} (listening on {args.host})', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.control.closing.set()
        monitor.stop.set()
        server.server_close()


if __name__ == '__main__':
    main()
