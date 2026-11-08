"""Text to speech through prada's speaker, so the rescue agent can talk to survivors.

Offline: nothing leaves the Pi. HTTP on speech_port (8081 unless the config
says otherwise), separate from the control socket and the camera stream.

    POST /say    {"text": "...", "interrupt": false}     needs the bearer token
        202 {"ok":true,"id":7,"queued":1,"engine":"piper","cached":false}
    POST /stop   stop playback and clear the queue      needs the bearer token
        200 {"ok":true,"cleared":2}
    GET /health  no auth, no secrets
        200 {"ok":true,"engine":"piper","device":"plughw:sndrpihifiberry",
             "queue":0,"speaking":false,"last_error":null}
    GET /phrases the scripted lines and whether each is already rendered

POST needs "Authorization: Bearer <ROBOT_CONTROL_TOKEN>", the same secret as the
motor server. text is 1 to 300 characters after control characters are removed.
Unknown or duplicate keys and wrong types are rejected. interrupt=true stops
what is playing and clears the queue first; use it for critical lines.

One worker speaks utterances in order. At most 5 wait; a sixth drops the oldest
waiting one that was not an interrupt. Synthesis, chosen once at startup:
Piper with the speech_voice model if both are installed, else espeak-ng, else a
dry run that writes silent WAVs and only logs. WAVs are cached by
sha256(engine, voice, text) in speech_cache, and the scripted phrases are
rendered at startup so they play without waiting for synthesis. Playback is
aplay on speech_device. Every subprocess gets an argument list (never a shell),
a timeout, and is killed on /stop or interrupt. Text goes to the synthesizer on
stdin, never as an argument.

    python -m picrawler_control.speech --config robot/config/control.toml --dry-run
    python -m picrawler_control.speech --config robot/prada/prada.toml --host ::
"""

import argparse
from collections import deque
from dataclasses import dataclass
import hashlib
import hmac
import importlib.util
import json
import logging
import os
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unicodedata
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from .control import decode

MAX_TEXT = 300
MAX_QUEUE = 5
MAX_BODY = 4096
SYNTH_TIMEOUT = 30
PLAY_MARGIN = 10  # seconds allowed beyond the WAV's length before aplay is killed
CACHE_LIMIT = 200  # WAV files kept; the oldest unscripted ones go first

# The agent's scripted lines. Rendered at startup so they play instantly.
PHRASES = (
    "This is a rescue robot. Help is coming. Can you hear me?",
    "Are you hurt? Can you move?",
    "Help is on the way. Stay still if you can.",
    "Responders have been notified.",
)


def clean_text(text):
    """Drop control and format characters, turn line breaks into spaces, collapse spaces."""
    kept = []
    for ch in text:
        if ch in "\t\n\r\v\f":
            kept.append(" ")
        elif unicodedata.category(ch)[0] != "C":
            kept.append(ch)
    return " ".join("".join(kept).split())


def parse_say(raw):
    """Validate a /say body. Returns (text, interrupt) or raises ValueError."""
    message = decode(raw)
    if "text" not in message or not message.keys() <= {"text", "interrupt"}:
        raise ValueError("expected text and optional interrupt only")
    if not isinstance(message["text"], str):
        raise ValueError("text must be a string")
    interrupt = message.get("interrupt", False)
    if type(interrupt) is not bool:
        raise ValueError("interrupt must be true or false")
    text = clean_text(message["text"])
    if not text:
        raise ValueError("text is empty")
    if len(text) > MAX_TEXT:
        raise ValueError(f"text is longer than {MAX_TEXT} characters")
    return text, interrupt


def preview(text, limit=60):
    return text if len(text) <= limit else text[:limit - 3] + "..."


def wav_seconds(path):
    with wave.open(path, "rb") as wav:
        return wav.getnframes() / wav.getframerate()


def write_silence(path, seconds, rate=8000):
    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(b"\0\0" * max(1, int(rate * seconds)))


def _run(args, text):
    """Run a synthesizer with text on stdin. Argument list only; killed on timeout."""
    result = subprocess.run(args, input=(text + "\n").encode("utf-8"), stdout=subprocess.DEVNULL,
                            stderr=subprocess.PIPE, timeout=SYNTH_TIMEOUT, check=False)
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", "replace").strip().splitlines()[-1:] or [""]
        raise RuntimeError(f"{os.path.basename(args[0])} exited {result.returncode}: {detail[0][:200]}")


class PiperEngine:
    name = "piper"

    def __init__(self, command, model):
        self.command = list(command)
        self.voice = model

    def synth(self, text, path):
        _run([*self.command, "-m", self.voice, "-f", path], text)


class EspeakEngine:
    name = "espeak-ng"

    def __init__(self, command="espeak-ng", voice="en-us", speed=150):
        self.command = command
        self.args = ["-v", voice, "-s", str(speed)]
        self.voice = f"{voice}@{speed}"

    def synth(self, text, path):
        _run([self.command, *self.args, "-w", path, "--stdin"], text)


class DryRunEngine:
    """Writes silence about as long as the speech would be, and logs the text."""
    name = "dry-run"
    voice = "silence"

    def __init__(self, seconds_per_char=0.06, max_seconds=8):
        self.seconds_per_char = seconds_per_char
        self.max_seconds = max_seconds

    def synth(self, text, path):
        logging.info("dry-run speech: %s", preview(text))
        write_silence(path, min(self.max_seconds, len(text) * self.seconds_per_char))


def _works(engine):
    """Synthesize one word to check an engine actually runs here."""
    with tempfile.TemporaryDirectory() as directory:
        path = os.path.join(directory, "probe.wav")
        try:
            engine.synth("Ready.", path)
            return wav_seconds(path) > 0
        except (OSError, RuntimeError, subprocess.SubprocessError, wave.Error, EOFError) as exc:
            logging.warning("%s unavailable: %s", engine.name, exc)
            return False


def choose_engine(voice, dry_run=False, which=shutil.which, find_spec=importlib.util.find_spec, probe=_works):
    """Piper (with the voice model), else espeak-ng, else dry run."""
    if dry_run:
        return DryRunEngine()
    candidates = []
    model = os.path.expanduser(voice)
    if os.path.isfile(model) and os.path.isfile(model + ".json"):
        if which("piper"):
            candidates.append(PiperEngine([which("piper")], model))
        elif find_spec("piper") is not None:
            candidates.append(PiperEngine([sys.executable, "-m", "piper"], model))
    else:
        logging.info("no Piper voice at %s (needs .onnx and .onnx.json)", model)
    if which("espeak-ng"):
        candidates.append(EspeakEngine(which("espeak-ng")))
    for engine in candidates:
        if probe(engine):
            return engine
    logging.warning("no speech engine works here; dry run (log only)")
    return DryRunEngine()


class AplayPlayer:
    def __init__(self, device, command="aplay"):
        self.device = device
        self.command = command
        self._lock = threading.Lock()
        self._proc = None
        self._stopped = None

    def play(self, path, still_wanted):
        """Play a WAV and wait. Returns False if skipped or stopped, True if it finished."""
        timeout = wav_seconds(path) + PLAY_MARGIN
        with self._lock:
            # Checked under the player lock: stop() either sees this process or
            # has already made still_wanted false.
            if not still_wanted():
                return False
            proc = subprocess.Popen([self.command, "-q", "-D", self.device, path],
                                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                    stderr=subprocess.PIPE)
            self._proc = proc
        try:
            _, err = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.communicate()
            raise RuntimeError("aplay did not finish in time; killed")
        finally:
            with self._lock:
                stopped = self._stopped is proc
                if self._proc is proc:
                    self._proc = None
        if stopped:
            return False
        if proc.returncode != 0:
            detail = err.decode("utf-8", "replace").strip().splitlines()[-1:] or [""]
            raise RuntimeError(f"aplay exited {proc.returncode}: {detail[0][:200]}")
        return True

    def stop(self):
        with self._lock:
            proc = self._proc
            if proc is not None and proc.poll() is None:
                self._stopped = proc
                proc.kill()


class DryRunPlayer:
    """Waits as long as the WAV lasts instead of playing it. stop() ends the wait."""
    device = None

    def __init__(self):
        self._lock = threading.Lock()
        self._current = None

    def play(self, path, still_wanted):
        seconds = wav_seconds(path)
        with self._lock:
            if not still_wanted():
                return False
            self._current = done = threading.Event()
        stopped = done.wait(seconds)
        with self._lock:
            if self._current is done:
                self._current = None
        return not stopped

    def stop(self):
        with self._lock:
            if self._current is not None:
                self._current.set()


@dataclass
class Utterance:
    id: int
    text: str
    interrupt: bool
    generation: int


class Speaker:
    """The queue and the one worker thread that synthesizes and plays."""

    def __init__(self, engine, player, cache_dir, max_queue=MAX_QUEUE, phrases=PHRASES):
        self.engine = engine
        self.player = player
        self.cache_dir = cache_dir
        self.max_queue = max_queue
        self.phrases = tuple(phrases)
        os.makedirs(cache_dir, exist_ok=True)
        self._cond = threading.Condition()
        self._queue = deque()
        self._next_id = 1
        # stop/interrupt bump this; a queued or synthesizing item from an older
        # generation is never played.
        self._generation = 0
        self._speaking = False
        self._closed = False
        self.last_error = None
        self._thread = threading.Thread(target=self._run, name="speech", daemon=True)
        self._thread.start()

    def cache_path(self, text):
        key = hashlib.sha256("\0".join((self.engine.name, self.engine.voice, text)).encode("utf-8"))
        return os.path.join(self.cache_dir, key.hexdigest() + ".wav")

    def render(self, text):
        """Return (wav path, synth ms, cached). Synthesizes on a cache miss."""
        path = self.cache_path(text)
        if os.path.isfile(path):
            try:
                os.utime(path)  # keeps recently used lines when the cache is pruned
            except OSError:
                pass
            return path, 0, True
        started = time.monotonic()
        fd, tmp = tempfile.mkstemp(suffix=".tmp.wav", dir=self.cache_dir)
        os.close(fd)
        try:
            self.engine.synth(text, tmp)
            if wav_seconds(tmp) <= 0:
                raise RuntimeError(f"{self.engine.name} wrote an empty WAV")
            os.replace(tmp, path)
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)
        self._prune()
        return path, round((time.monotonic() - started) * 1000), False

    def _prune(self):
        keep = {self.cache_path(p) for p in self.phrases}
        try:
            names = [os.path.join(self.cache_dir, n) for n in os.listdir(self.cache_dir) if n.endswith(".wav")]
            names = [n for n in names if n not in keep and not n.endswith(".tmp.wav")]
            names.sort(key=os.path.getmtime)
            for old in names[:max(0, len(names) + len(keep) - CACHE_LIMIT)]:
                os.remove(old)
        except OSError as exc:
            logging.warning("speech cache prune failed: %s", exc)

    def prerender(self):
        for text in self.phrases:
            try:
                _, ms, cached = self.render(text)
                logging.info("phrase %s: %s", "cached" if cached else f"rendered in {ms} ms", preview(text))
            except Exception as exc:  # keep going; the line is synthesized when asked for
                self._error(f"prerender failed: {exc}")

    def say(self, text, interrupt=False):
        with self._cond:
            if self._closed:
                raise RuntimeError("speaker closed")
            if interrupt:
                self._clear_locked()
            item = Utterance(self._next_id, text, interrupt, self._generation)
            self._next_id += 1
            self._queue.append(item)
            if len(self._queue) > self.max_queue:
                victim = next((u for u in self._queue if not u.interrupt), self._queue[0])
                self._queue.remove(victim)
                logging.warning("speech queue full; dropped #%d %s", victim.id, preview(victim.text))
            self._cond.notify_all()
            queued = len(self._queue)
        return {"ok": True, "id": item.id, "queued": queued, "engine": self.engine.name,
                "cached": os.path.isfile(self.cache_path(text))}

    def stop(self):
        with self._cond:
            cleared = self._clear_locked()
            self._cond.notify_all()
        logging.info("speech stopped; cleared %d", cleared)
        return cleared

    def _clear_locked(self):
        # Bump first, then kill: the worker either sees the new generation or
        # has a process for stop() to kill. Lock order is speaker then player.
        self._generation += 1
        cleared = len(self._queue)
        self._queue.clear()
        self.player.stop()
        return cleared

    def health(self):
        with self._cond:
            return {"ok": True, "engine": self.engine.name, "device": self.player.device,
                    "queue": len(self._queue), "speaking": self._speaking, "last_error": self.last_error}

    def phrase_list(self):
        return [{"text": p, "cached": os.path.isfile(self.cache_path(p))} for p in self.phrases]

    def wait_idle(self, timeout):
        with self._cond:
            return self._cond.wait_for(lambda: not self._queue and not self._speaking, timeout)

    def close(self):
        with self._cond:
            self._closed = True
            self._clear_locked()
            self._cond.notify_all()
        self._thread.join(timeout=2)

    def _error(self, message):
        logging.error("%s", message)
        with self._cond:
            self.last_error = message[:300]

    def _run(self):
        while True:
            with self._cond:
                self._cond.wait_for(lambda: self._queue or self._closed)
                if self._closed:
                    return
                item = self._queue.popleft()
                self._speaking = True
            try:
                if item.generation != self._generation:
                    continue
                path, synth_ms, cached = self.render(item.text)
                started = time.monotonic()
                # Read without the speaker lock: the player calls this under its own lock.
                finished = self.player.play(path, lambda: item.generation == self._generation)
                play_ms = round((time.monotonic() - started) * 1000)
                logging.info("said #%d%s synth %s ms play %d ms%s: %s", item.id,
                             " (interrupt)" if item.interrupt else "",
                             "0 (cached)" if cached else synth_ms, play_ms,
                             "" if finished else " (stopped)", preview(item.text))
            except Exception as exc:
                self._error(f"#{item.id} failed: {type(exc).__name__}: {exc}")
            finally:
                with self._cond:
                    self._speaking = False
                    self._cond.notify_all()


class SpeechServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, host, port, speaker, token):
        if ":" in host:
            self.address_family = socket.AF_INET6
        self.speaker = speaker
        self.token = token
        super().__init__((host, port), _Handler)

    def server_bind(self):
        # "::" also accepts IPv4, so phones and Macs on the hotspot reach it either way.
        if self.address_family == socket.AF_INET6 and hasattr(socket, "IPV6_V6ONLY"):
            self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        super().server_bind()


class _Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"
    timeout = 10  # a client that stalls mid-request is dropped

    def log_message(self, fmt, *args):
        logging.info("%s " + fmt, self.address_string(), *args)

    def _json(self, status, body, headers=()):
        data = json.dumps(body).encode() + b"\n"
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        for name, value in headers:
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)

    def _fail(self, status, error, headers=()):
        self._json(status, {"ok": False, "error": error}, headers)

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/health":
            self._json(200, self.server.speaker.health())
        elif path == "/phrases":
            self._json(200, {"ok": True, "phrases": self.server.speaker.phrase_list()})
        else:
            self._fail(404, "use POST /say, POST /stop, GET /health or GET /phrases")

    def do_POST(self):
        path = self.path.split("?", 1)[0]
        if path not in ("/say", "/stop"):
            self._fail(404, "use POST /say, POST /stop, GET /health or GET /phrases")
            return
        expected = ("Bearer " + self.server.token).encode()
        given = self.headers.get("Authorization", "").encode("utf-8", "replace")
        if not hmac.compare_digest(given, expected):
            self._fail(401, "missing or wrong bearer token", [("WWW-Authenticate", "Bearer")])
            return
        if path == "/stop":
            self._json(200, {"ok": True, "cleared": self.server.speaker.stop()})
            return
        if self.headers.get_content_type() != "application/json":
            self._fail(415, "Content-Type must be application/json")
            return
        try:
            length = int(self.headers.get("Content-Length", ""))
        except ValueError:
            self._fail(411, "Content-Length required")
            return
        if not 0 <= length <= MAX_BODY:
            self._fail(413, f"body over {MAX_BODY} bytes")
            return
        try:
            text, interrupt = parse_say(self.rfile.read(length).decode("utf-8"))
        except ValueError as exc:
            self._fail(400, str(exc)[:200])
            return
        self._json(202, self.server.speaker.say(text, interrupt))


def main():
    parser = argparse.ArgumentParser(description="prada text to speech over HTTP (dry run without piper or espeak-ng)")
    parser.add_argument("--config")
    parser.add_argument("--host", help="bind address for speech (default: config host; :: for IPv4 and IPv6)")
    parser.add_argument("--dry-run", action="store_true", help="log instead of synthesizing and playing")
    parser.add_argument("--no-prerender", action="store_true", help="skip rendering the scripted phrases at startup")
    args = parser.parse_args()
    from .config import load
    config = load(args.config)
    token = os.environ.get("ROBOT_CONTROL_TOKEN", "")
    if len(token) < 16:
        parser.error("set ROBOT_CONTROL_TOKEN to a secret of at least 16 characters")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    engine = choose_engine(config.speech_voice, args.dry_run)
    if engine.name == "dry-run":
        player = DryRunPlayer()
    elif shutil.which("aplay"):
        player = AplayPlayer(config.speech_device, shutil.which("aplay"))
    else:
        logging.warning("aplay not found; synthesizing but not playing")
        player = DryRunPlayer()
    speaker = Speaker(engine, player, os.path.expanduser(config.speech_cache))
    if not args.no_prerender:
        threading.Thread(target=speaker.prerender, name="prerender", daemon=True).start()
    host = args.host or config.host
    server = SpeechServer(host, config.speech_port, speaker, token)
    logging.info("speech listening on http://%s:%d (engine %s %s, device %s)", host, config.speech_port,
                 engine.name, engine.voice, player.device or "none")

    def terminate(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, terminate)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        speaker.close()
        server.server_close()


if __name__ == "__main__":
    main()
