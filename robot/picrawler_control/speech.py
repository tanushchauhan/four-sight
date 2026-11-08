"""Speech through prada's speaker and microphone, so the rescue agent can talk to
survivors and hear their answers.

Offline: nothing leaves the Pi. HTTP on speech_port (8081 unless the config
says otherwise), separate from the control socket and the camera stream.

    POST /say    {"text": "...", "interrupt": false}     needs the bearer token
        202 {"ok":true,"id":7,"queued":1,"engine":"piper","cached":false}
    POST /stop   stop playback and clear the queue      needs the bearer token
        200 {"ok":true,"cleared":2}
    POST /listen {"seconds": 5}  (body optional, 1 to 10) needs the bearer token
        200 {"ok":true,"transcript":"I'm here","silent":false,"rms":812.4,
             "voiced_ratio":0.31,"engine":"whisper.cpp","record_ms":5040,
             "stt_ms":2210,"stt_unavailable":false}
    GET /health  no auth, no secrets
        200 {"ok":true,"engine":"piper","device":"plughw:sndrpihifiberry",
             "queue":0,"speaking":false,"last_error":null,
             "mic":"plughw:2,0","stt":"whisper.cpp","listening":false}
    GET /phrases the scripted lines and whether each is already rendered

/listen waits (up to 15 s) until nothing is queued or playing, holds the
speaker so nothing starts while the microphone is open, records with arecord,
and releases the speaker. A voice activity check (frame RMS against
speech_vad_rms) skips speech to text on silence, where Whisper tends to invent
"Thank you." Speech to text is the whisper.cpp CLI if it and its model are
installed, else none (transcript null, stt_unavailable true), else a dry run
with a fixed transcript. One listen at a time; the WAV lives in a temporary
directory that is deleted afterwards unless speech_keep_audio is true.

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
from array import array
from collections import deque
from dataclasses import dataclass
import hashlib
import hmac
import importlib.util
import json
import logging
import math
import os
import re
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
DRAIN_LIMIT = 65536  # a rejected body up to this size is read and dropped before answering
SYNTH_TIMEOUT = 30
PLAY_MARGIN = 10  # seconds allowed beyond the WAV's length before aplay is killed
CACHE_LIMIT = 200  # WAV files kept; the oldest unscripted ones go first

LISTEN_DEFAULT = 5  # seconds recorded when /listen has no body
LISTEN_MAX = 10
SPEAKER_WAIT = 15  # seconds /listen waits for the speaker to go quiet
SETTLE = 0.15  # pause after playback so the speaker's tail is not recorded
RECORD_MARGIN = 5  # seconds allowed beyond the recording before arecord is killed
STT_TIMEOUT = 60
RATE = 16000  # what whisper.cpp wants: 16 kHz mono int16
FRAME_MS = 30
MIN_VOICED_SECONDS = 0.2  # less voice than this counts as silence
WHISPER_DIR = "~/vendor/whisper.cpp"

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
        self._held = False  # true while /listen has the microphone open
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

    def hold(self, timeout):
        """Wait until nothing is queued or playing, then keep the worker from starting
        anything until release(). Lines asked for meanwhile wait in the queue.
        Returns False if the speaker stayed busy (or closed) for timeout seconds."""
        with self._cond:
            idle = self._cond.wait_for(
                lambda: self._closed or not (self._queue or self._speaking or self._held), timeout)
            if not idle or self._closed:
                return False
            self._held = True
            return True

    def release(self):
        with self._cond:
            self._held = False
            self._cond.notify_all()

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
                self._cond.wait_for(lambda: (self._queue and not self._held) or self._closed)
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


# ---- Listening ----

def parse_listen(raw):
    """Validate a /listen body (may be empty). Returns seconds or raises ValueError."""
    if not raw.strip():
        return LISTEN_DEFAULT
    message = decode(raw)
    if not message.keys() <= {"seconds"}:
        raise ValueError("expected optional seconds only")
    seconds = message.get("seconds", LISTEN_DEFAULT)
    if type(seconds) not in (int, float) or not math.isfinite(seconds):
        raise ValueError("seconds must be a number")
    if not 1 <= seconds <= LISTEN_MAX:
        raise ValueError(f"seconds must be from 1 to {LISTEN_MAX}")
    return seconds


_CARD = re.compile(r"^card (\d+): (\S+) \[(.*?)\], device (\d+): (.*)$")


def parse_capture_cards(listing):
    """Parse `arecord -l` into [(card, device, card id, description)]."""
    cards = []
    for line in listing.splitlines():
        match = _CARD.match(line.strip())
        if match:
            cards.append((int(match[1]), int(match[4]), match[2], f"{match[3]} {match[5]}"))
    return cards


def pick_mic(listing, speaker_device="plughw:sndrpihifiberry"):
    """The first capture device that is not the HAT, preferring USB. plughw:<card>,<device> or None."""
    speaker_card = speaker_device.split(":", 1)[-1].split(",", 1)[0].lower()
    cards = [c for c in parse_capture_cards(listing)
             if "hifiberry" not in (c[2] + c[3]).lower() and c[2].lower() != speaker_card]
    usb = [c for c in cards if "usb" in (c[2] + " " + c[3]).lower()]
    chosen = (usb or cards or [None])[0]
    return None if chosen is None else f"plughw:{chosen[0]},{chosen[1]}"


def detect_mic(arecord, speaker_device, run=subprocess.run):
    """Run `arecord -l` and pick a microphone. None if arecord is missing or finds nothing."""
    if not arecord:
        return None
    try:
        result = run([arecord, "-l"], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, timeout=5, check=False)
    except (OSError, subprocess.SubprocessError) as exc:
        logging.warning("arecord -l failed: %s", exc)
        return None
    return pick_mic(result.stdout.decode("utf-8", "replace"), speaker_device)


def measure(path, threshold, frame_ms=FRAME_MS):
    """Voice activity from int16 samples: (overall RMS, fraction of frames whose RMS
    reaches threshold, seconds of such frames)."""
    with wave.open(path, "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError("expected 16-bit samples")
        channels, rate = wav.getnchannels(), wav.getframerate()
        samples = array("h")
        samples.frombytes(wav.readframes(wav.getnframes()))
    if sys.byteorder == "big":
        samples.byteswap()
    if channels > 1:
        samples = samples[::channels]
    if not samples:
        return 0.0, 0.0, 0.0
    step = max(1, rate * frame_ms // 1000)
    floor = threshold * threshold
    total = frames = voiced = 0
    for start in range(0, len(samples), step):
        chunk = samples[start:start + step]
        energy = sum(x * x for x in chunk)
        total += energy
        frames += 1
        if energy >= floor * len(chunk):
            voiced += 1
    return math.sqrt(total / len(samples)), voiced / frames, voiced * step / rate


def write_tone(path, seconds, rate=RATE, amplitude=6000, hz=220):
    """A WAV with a tone in its middle half, for dry runs and tests."""
    count = max(1, int(rate * seconds))
    samples = array("h", (int(amplitude * math.sin(2 * math.pi * hz * n / rate)) if count // 4 <= n < 3 * count // 4
                          else 0 for n in range(count)))
    if sys.byteorder == "big":
        samples.byteswap()
    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(samples.tobytes())


def _tail(stderr):
    return (stderr.decode("utf-8", "replace").strip().splitlines()[-1:] or [""])[0][:200]


class ArecordRecorder:
    def __init__(self, device, command="arecord"):
        self.device = device
        self.command = command

    def record(self, path, seconds):
        whole = math.ceil(seconds)  # arecord -d takes whole seconds
        args = [self.command, "-q", "-D", self.device, "-f", "S16_LE", "-r", str(RATE), "-c", "1",
                "-d", str(whole), path]
        result = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                stderr=subprocess.PIPE, timeout=whole + RECORD_MARGIN, check=False)
        if result.returncode != 0:
            raise RuntimeError(f"arecord exited {result.returncode}: {_tail(result.stderr)}")


class DryRunRecorder:
    """Writes a tone burst instead of recording, after waiting about as long."""

    def __init__(self, wait=True):
        self.wait = wait

    def record(self, path, seconds):
        if self.wait:
            time.sleep(seconds)
        write_tone(path, seconds)


# Whisper marks non-speech as [BLANK_AUDIO], (silence), [Music], *coughs* or music notes.
_TAGS = re.compile(r"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[\u266a\u266b]")


def clean_transcript(output):
    """Whisper's text without non-speech tags; None when nothing worded is left."""
    text = clean_text(_TAGS.sub(" ", output))
    return text if any(ch.isalnum() for ch in text) else None


class WhisperEngine:
    name = "whisper.cpp"

    def __init__(self, command, model, threads=4):
        self.command = command
        self.model = model
        self.threads = threads

    def transcribe(self, path):
        # whisper-cli: -nt no timestamps, -np no progress or system info on stdout.
        args = [self.command, "-m", self.model, "-f", path, "-nt", "-np", "-l", "en", "-t", str(self.threads)]
        result = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=STT_TIMEOUT, check=False)
        if result.returncode != 0:
            raise RuntimeError(f"{os.path.basename(self.command)} exited {result.returncode}: {_tail(result.stderr)}")
        return clean_transcript(result.stdout.decode("utf-8", "replace"))


class NoSTT:
    name = "none"

    def transcribe(self, path):
        return None


class DryRunSTT:
    name = "dry-run"

    def __init__(self, transcript="I'm here. My leg is stuck."):
        self.transcript = transcript

    def transcribe(self, path):
        return self.transcript


def choose_stt(setting, model, dry_run=False, which=shutil.which, isfile=os.path.isfile):
    """whisper.cpp if both the CLI and the model are present, else none."""
    if dry_run:
        return DryRunSTT()
    model = os.path.expanduser(model)
    if setting == "auto":
        candidates = [os.path.expanduser(f"{WHISPER_DIR}/build/bin/whisper-cli"), which("whisper-cli")]
    elif "/" in setting or os.sep in setting:
        candidates = [os.path.expanduser(setting)]
    else:
        candidates = [which(setting)]
    command = next((c for c in candidates if c and isfile(c) and os.access(c, os.X_OK)), None)
    if command is None:
        logging.warning("no whisper.cpp CLI found (%s); /listen records but returns no transcript", setting)
        return NoSTT()
    if not isfile(model):
        logging.warning("no whisper model at %s; /listen records but returns no transcript", model)
        return NoSTT()
    return WhisperEngine(command, model)


class Listener:
    """One listen at a time: wait for the speaker, hold it, record, check for voice, transcribe."""

    def __init__(self, speaker, recorder, stt, mic, vad_rms=500, keep_audio=False,
                 speaker_wait=SPEAKER_WAIT, settle=SETTLE):
        self.speaker = speaker
        self.recorder = recorder
        self.stt = stt
        self.mic = mic
        self.vad_rms = vad_rms
        self.keep_audio = keep_audio
        self.speaker_wait = speaker_wait
        self.settle = settle
        self._busy = threading.Lock()

    def health(self):
        return {"mic": self.mic, "stt": self.stt.name, "listening": self._busy.locked()}

    def listen(self, seconds):
        """Returns (HTTP status, body)."""
        if self.mic is None:
            return 503, {"ok": False, "error": "no microphone"}
        if not self._busy.acquire(blocking=False):
            return 409, {"ok": False, "error": "already listening"}
        directory = None
        try:
            if not self.speaker.hold(self.speaker_wait):
                return 409, {"ok": False, "error": "speaker busy"}
            directory = tempfile.mkdtemp(prefix="prada-listen-")
            path = os.path.join(directory, "listen.wav")
            try:
                time.sleep(self.settle)
                started = time.monotonic()
                self.recorder.record(path, seconds)
                record_ms = round((time.monotonic() - started) * 1000)
            finally:
                self.speaker.release()
            rms, ratio, voiced = measure(path, self.vad_rms)
            silent = voiced < MIN_VOICED_SECONDS
            transcript, stt_ms = None, 0
            if not silent:
                started = time.monotonic()
                transcript = self.stt.transcribe(path)
                stt_ms = round((time.monotonic() - started) * 1000)
            logging.info("listened %ss rms %.0f voiced %.2f record %d ms stt %d ms%s: %s", seconds, rms, ratio,
                         record_ms, stt_ms, " (silent)" if silent else "", preview(transcript or "-"))
            if self.keep_audio:
                logging.info("kept %s", path)
            return 200, {"ok": True, "transcript": transcript, "silent": silent, "rms": round(rms, 1),
                         "voiced_ratio": round(ratio, 3), "engine": self.stt.name, "record_ms": record_ms,
                         "stt_ms": stt_ms, "stt_unavailable": self.stt.name == "none"}
        except Exception as exc:
            logging.error("listen failed: %s: %s", type(exc).__name__, exc)
            return 500, {"ok": False, "error": f"listen failed: {str(exc)[:200]}"}
        finally:
            if directory and not self.keep_audio:
                shutil.rmtree(directory, ignore_errors=True)
            self._busy.release()


class SpeechServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, host, port, speaker, token, listener=None):
        if ":" in host:
            self.address_family = socket.AF_INET6
        self.speaker = speaker
        self.token = token
        self.listener = listener
        super().__init__((host, port), _Handler)

    def server_bind(self):
        # "::" also accepts IPv4, so phones and Macs on the hotspot reach it either way.
        if self.address_family == socket.AF_INET6 and hasattr(socket, "IPV6_V6ONLY"):
            self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        super().server_bind()


USAGE = "use POST /say, POST /stop, POST /listen, GET /health or GET /phrases"


class _Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"
    timeout = 10  # a client that stalls mid-request is dropped

    def log_message(self, fmt, *args):
        logging.info("%s " + fmt, self.address_string(), *args)

    def _drain(self):
        """Read an unread request body (up to DRAIN_LIMIT) before answering. Closing
        with unread bytes makes the OS reset the connection, and the client can
        lose the answer (always on Windows, sometimes on Linux)."""
        if getattr(self, "_consumed", False) or self.command != "POST":
            return
        self._consumed = True
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            return
        if 0 < length <= DRAIN_LIMIT:
            try:
                self.rfile.read(length)
            except OSError:
                pass

    def _json(self, status, body, headers=()):
        self._drain()
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
            health = self.server.speaker.health()
            if self.server.listener is not None:
                health.update(self.server.listener.health())
            self._json(200, health)
        elif path == "/phrases":
            self._json(200, {"ok": True, "phrases": self.server.speaker.phrase_list()})
        else:
            self._fail(404, USAGE)

    def do_POST(self):
        path = self.path.split("?", 1)[0]
        if path not in ("/say", "/stop", "/listen"):
            self._fail(404, USAGE)
            return
        expected = ("Bearer " + self.server.token).encode()
        given = self.headers.get("Authorization", "").encode("utf-8", "replace")
        if not hmac.compare_digest(given, expected):
            self._fail(401, "missing or wrong bearer token", [("WWW-Authenticate", "Bearer")])
            return
        if path == "/stop":
            self._json(200, {"ok": True, "cleared": self.server.speaker.stop()})
            return
        # /listen may have no body at all; anything sent must be JSON.
        optional = path == "/listen" and self.headers.get("Content-Length", "0").strip() == "0"
        if not optional and self.headers.get_content_type() != "application/json":
            self._fail(415, "Content-Type must be application/json")
            return
        try:
            length = int(self.headers.get("Content-Length", "0" if optional else ""))
        except ValueError:
            self._fail(411, "Content-Length required")
            return
        if not 0 <= length <= MAX_BODY:
            self._fail(413, f"body over {MAX_BODY} bytes")
            return
        try:
            self._consumed = True
            raw = self.rfile.read(length).decode("utf-8")
            if path == "/listen":
                seconds = parse_listen(raw)
            else:
                text, interrupt = parse_say(raw)
        except (ValueError, UnicodeError) as exc:
            self._fail(400, str(exc)[:200])
            return
        if path == "/listen":
            if self.server.listener is None:
                self._fail(503, "no microphone")
                return
            self._json(*self.server.listener.listen(seconds))
            return
        self._json(202, self.server.speaker.say(text, interrupt))


def main():
    parser = argparse.ArgumentParser(description="prada speech and listening over HTTP (dry run without piper or espeak-ng)")
    parser.add_argument("--config")
    parser.add_argument("--host", help="bind address for speech (default: config host; :: for IPv4 and IPv6)")
    parser.add_argument("--dry-run", action="store_true",
                        help="log instead of synthesizing and playing; /listen returns a fixed transcript")
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
    if args.dry_run:
        mic, recorder = "dry-run", DryRunRecorder()
    else:
        arecord = shutil.which("arecord")
        if config.speech_mic != "auto":
            mic = config.speech_mic if arecord else None
        else:
            mic = detect_mic(arecord, config.speech_device)
        if mic is None:
            logging.warning("no microphone found (arecord %s); /listen answers 503", "found" if arecord else "missing")
        recorder = ArecordRecorder(mic, arecord)
    stt = choose_stt(config.speech_stt_bin, config.speech_stt_model, args.dry_run)
    listener = Listener(speaker, recorder, stt, mic, config.speech_vad_rms, config.speech_keep_audio)
    host = args.host or config.host
    server = SpeechServer(host, config.speech_port, speaker, token, listener)
    logging.info("speech listening on http://%s:%d (engine %s %s, device %s, mic %s, stt %s)", host,
                 config.speech_port, engine.name, engine.voice, player.device or "none", mic or "none", stt.name)

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
