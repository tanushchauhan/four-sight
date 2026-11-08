"""Dashboard mission ownership and private agent/media integration."""
import json
import math
import os
import secrets
import threading
import time
import urllib.request
import urllib.error
from pathlib import Path
from local_guidance import LocalGuide


def settings():
    values = {}
    path = Path(__file__).resolve().parents[1] / 'agent' / '.env'
    if path.exists():
        for line in path.read_text().splitlines():
            key, sep, value = line.strip().partition('=')
            if sep and not key.startswith('#'):
                values[key] = value.strip().strip('\"\'')
    values.update(os.environ)
    return values


class Media:
    def __init__(self, config):
        self.camera = config.get('CAMERA_URL', 'http://prada.local:8080').rstrip('/')
        self.speech = config.get('SPEAKER_URL', 'http://prada.local:8081').rstrip('/')
        self.agent = config.get('DASHBOARD_AGENT_URL', 'http://127.0.0.1:8788').rstrip('/')
        self.token = config.get('ROBOT_CONTROL_TOKEN', '')
        self.agent_token = config.get('AGENT_TOKEN', '')

    def request(self, path, data=None, raw=False):
        if not self.token:
            raise ValueError('Configure ROBOT_CONTROL_TOKEN in agent/.env for robot audio')
        body = data if raw else json.dumps(data or {}).encode()
        request = urllib.request.Request(self.speech + path, data=body, headers={
            'Authorization': 'Bearer ' + self.token,
            'X-Speech-Source': 'operator',
            'Content-Type': 'audio/wav' if raw else 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=3 if path == "/session" else 25) as response:
                return response.read(2_000_000)
        except urllib.error.HTTPError as exc:
            try: detail = json.loads(exc.read(4096)).get('error', 'Audio request failed')
            except (ValueError, AttributeError): detail = 'Audio request failed'
            raise ValueError(str(detail)) from None


class Mission:
    def __init__(self, control, media):
        self.control, self.media = control, media
        self.guide = LocalGuide()
        self.lock = threading.RLock()
        self.mode = 'manual'
        self.speed = .5
        self.detection = False
        self.reason = 'Ready for manual control'
        self.last_person = None
        self.session_id = secrets.token_hex(8)
        self.session_started = time.time()
        self.handoff = {"stage": "idle"}
        self.watch_attempt = None
        self.owner = None
        self.seq = 0
        self.control_generation = 0
        self.halt = threading.Event()
        self.agent_seen = 0
        self.worker = threading.Thread(target=self.run, daemon=True)
        self.worker.start()

    def snapshot(self):
        with self.lock:
            return dict(mode=self.mode, speed=self.speed, person_detection=self.detection,
                        reason=self.reason, person=self.last_person,
                        agent_connected=time.monotonic()-self.agent_seen < 3,
                        navigation='local', session_id=self.session_id, session_started=self.session_started, handoff=dict(self.handoff))

    def configure(self, data):
        with self.lock:
            speed = data.get('speed', self.speed)
            detection = data.get('person_detection', self.detection)
            if type(speed) not in (int, float) or not .2 <= speed <= 1:
                raise ValueError('Speed must be 20–100%')
            if type(detection) is not bool:
                raise ValueError('Person detection must be true or false')
            change_audio = detection != self.detection and self.mode in ('autonomous', 'watching', 'person_found')
            if detection and change_audio and time.monotonic()-self.agent_seen >= 3:
                raise ValueError('Person detection agent is unavailable')
        # Never block drive heartbeats on an HTTP request while changing the toggle.
        if change_audio:
            self.media.request('/session', {'mode': 'agent' if detection else 'manual'})
        with self.lock:
            self.speed, self.detection = speed, detection
            manual_won = change_audio and self.mode not in ('autonomous', 'watching', 'person_found')
            result = self.snapshot()
        if manual_won:
            self.media.request('/session', {'mode': 'manual'})
        return result

    def start(self):
        with self.lock:
            if self.mode == 'autonomous':
                raise ValueError('Autonomous mode is already running')
            if self.owner or self.control.owner or self.control.maintenance:
                raise ValueError('Release the current controller before starting autonomous mode')
            if self.detection and time.monotonic()-self.agent_seen >= 3:
                raise ValueError('Start the Prada agent before enabling person detection')
            # Speech mode must be ready before allowing the agent to greet anyone.
            if self.detection:
                self.media.request('/session', {'mode': 'agent'})
            self.session_id = secrets.token_hex(8)
            self.session_started = time.time()
            self.last_person = None
            self.handoff = {'stage': 'watching' if self.detection else 'idle'}
            self.guide.reset()
            self.owner = self.control.acquire(mode='auto')
            self.seq = 0
            self.control_generation = self.control.generation
            self.mode, self.reason = 'autonomous', 'Connecting; waiting for local vision'
            return self.snapshot()

    def stop(self, reason='Stopped by operator', mode='stopped'):
        with self.lock:
            self.watch_attempt = None
            self.guide.reset()
            self.control.stop_motion()
            if self.owner:
                self.control.release()
            self.owner = None
            self.mode, self.reason = mode, reason
            if mode != 'person_found':
                self.handoff = dict(self.handoff, stage='manual' if mode == 'manual' else 'stopped')
            return self.snapshot()

    def watch(self):
        if time.monotonic()-self.agent_seen >= 3:
            raise ValueError('Start the agent before arming the person demo')
        self.stop('Preparing stationary person demo')
        with self.lock:
            attempt = self.watch_attempt = object()
        self.control.close()
        with self.lock:
            if self.watch_attempt is not attempt:
                raise ValueError('Demo arming cancelled by another control action')
        self.media.request('/session', {'mode': 'agent'})
        with self.lock:
            if self.watch_attempt is attempt:
                self.session_id = secrets.token_hex(8)
                self.session_started = time.time()
                self.last_person = None
                self.detection = True
                self.mode = 'watching'
                self.reason = 'Watching for a person; robot remains stationary'
                self.handoff = {'stage': 'watching'}
                return self.snapshot()
        self.media.request('/session', {'mode': 'manual'})
        raise ValueError('Demo arming cancelled by another control action')

    def update_handoff(self, data):
        with self.lock:
            if data.get('session_id') != self.session_id or self.mode != 'person_found' or not self.detection:
                return {'accepted': False}
            stage=data.get('stage')
            if stage not in ('alerted', 'greeting', 'waiting', 'error'):
                raise ValueError('Invalid handoff stage')
            allowed = {'detected': ('alerted',), 'alerted': ('greeting', 'error'), 'greeting': ('waiting', 'error')}
            current = self.handoff.get('stage')
            if stage != current and stage not in allowed.get(current, ()):
                return {'accepted': False}
            self.handoff = dict(stage=stage, case_id=data.get('case_id'), detail=str(data.get('detail', ''))[:200])
            return {'accepted': True}

    def person(self, data):
        confidence, count = data.get('confidence'), data.get('count')
        if (type(confidence) not in (int, float) or not math.isfinite(confidence)
                or not .5 <= confidence <= 1 or type(count) is not int or not 1 <= count <= 50):
            return {'accepted': False, 'reason': 'Invalid or weak sighting'}
        frame_time = data.get('frameTime')
        if frame_time is not None and (type(frame_time) not in (int, float) or not math.isfinite(frame_time) or not -5000 <= time.time()*1000-frame_time <= 15000):
            return {'accepted': False, 'reason': 'Stale sighting'}
        with self.lock:
            if not self.detection or self.mode not in ('autonomous', 'watching', 'person_found'):
                return {'accepted': False, 'reason': 'detection disabled or manual control'}
            self.last_person = dict(confidence=data.get('confidence'), count=data.get('count'),
                                    box=data.get('box'), visor=data.get('visor'), source=data.get('source', 'visor'), time=time.time())
            if self.mode != 'person_found':
                self.stop('Person detected — exploration stopped; responder requested', 'person_found')
                self.handoff = {'stage': 'detected'}
        # Do not let Nemotron begin until the previous motor connection is gone.
        deadline = time.monotonic() + 1
        while self.control.owner and time.monotonic() < deadline:
            time.sleep(.01)
        with self.lock:
            return {'accepted': self.mode == 'person_found' and self.detection and self.control.owner is None, 'session_id': self.session_id}

    def run(self):
        enabled_once = False
        session = None
        while not self.halt.wait(.05):
            with self.lock:
                owner = self.owner
                if not owner or self.mode != 'autonomous':
                    session = None
                    continue
                if session != owner:
                    session, enabled_once = owner, False
                c = self.control.snapshot()
                if self.control.generation != self.control_generation:
                    self.stop('Controller stopped; restart explicitly')
                    continue
                if c.get('connection') == 'error' or self.control.owner != owner:
                    self.stop(c.get('reason', 'Control connection lost'))
                    continue
                if c.get('connection') != 'connected':
                    # Keep connection setup lease fresh, without enabling.
                    with self.control.lock:
                        self.control.received = time.monotonic()
                    continue
                try:
                    if enabled_once and not self.control.armed:
                        self.stop('Autonomous drive stopped; restart explicitly')
                        continue
                    action = 'heartbeat'
                    if not enabled_once and not c.get('busy'):
                        action, enabled_once = 'enable', True
                    if self.detection and time.monotonic()-self.agent_seen >= 3:
                        self.guide.reset()
                        forward, turn, reason = 0, 0, 'Waiting for person detection agent; retaining control'
                    else:
                        forward, turn, reason = self.guide.command(self.speed, c.get('busy', False))
                    self.control.update(dict(owner=owner, seq=self.seq, action=action,
                                             forward=forward, turn=turn))
                    self.seq += 1
                    self.reason = reason
                except ValueError as exc:
                    self.stop(str(exc))

    def close(self):
        self.halt.set()
        self.stop('Dashboard shutting down')
        self.worker.join(timeout=2)
