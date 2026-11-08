import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch

from picrawler_control import speech
from picrawler_control.config import Config, load
from picrawler_control.speech import (AplayPlayer, ArecordRecorder, DryRunEngine, DryRunRecorder, DryRunSTT,
                                      EspeakEngine, Listener, NoSTT, PiperEngine, Speaker, SpeechServer,
                                      WhisperEngine, choose_engine, choose_stt, clean_text, clean_transcript,
                                      detect_mic, measure, parse_listen, parse_say, pick_mic, write_silence,
                                      write_tone)

TOKEN = "test-token-0123456789"
ROBOT = Path(__file__).resolve().parents[1]


def wait_for(predicate, timeout=2):
    end = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() > end:
            raise AssertionError("condition timed out")
        time.sleep(0.005)


class CountingEngine(DryRunEngine):
    def __init__(self):
        super().__init__(seconds_per_char=0.001)
        self.texts = []

    def synth(self, text, path):
        self.texts.append(text)
        super().synth(text, path)


class GatePlayer:
    """Blocks each play until released or stopped, and records what played."""
    device = "test-device"

    def __init__(self):
        self.lock = threading.Lock()
        self.started = []
        self.finished = []
        self.stops = 0
        self.gate = None

    def play(self, path, still_wanted):
        with self.lock:
            if not still_wanted():
                return False
            self.gate = gate = threading.Event()
            self.stopped = False
            self.started.append(path)
        gate.wait(5)
        with self.lock:
            stopped = self.stopped
            if not stopped:
                self.finished.append(path)
        return not stopped

    def release(self):
        with self.lock:
            if self.gate:
                self.gate.set()

    def stop(self):
        with self.lock:
            self.stops += 1
            if self.gate and not self.gate.is_set():
                self.stopped = True
                self.gate.set()


class TextTests(unittest.TestCase):
    def test_clean_text_strips_control_characters(self):
        self.assertEqual(clean_text("Can\x00 you\x1b\nhear​ me?\t"), "Can you hear me?")

    def test_parse_say_accepts_valid(self):
        self.assertEqual(parse_say('{"text":"Hello"}'), ("Hello", False))
        self.assertEqual(parse_say('{"text":"Hello","interrupt":true}'), ("Hello", True))
        self.assertEqual(parse_say(json.dumps({"text": "x" * 300})), ("x" * 300, False))

    def test_parse_say_rejections(self):
        for raw in ('{"text":""}', '{"text":"  \\u0007 "}', json.dumps({"text": "x" * 301}),
                    '{"text":"hi","volume":3}', '{"interrupt":true}', '{"text":5}', '{"text":null}',
                    '{"text":["hi"]}', '{"text":"hi","interrupt":1}', '{"text":"hi","interrupt":"yes"}',
                    '{"text":"a","text":"b"}', '[]', '"hi"', '{'):
            with self.assertRaises(ValueError, msg=raw):
                parse_say(raw)


class SpeakerTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.engine = CountingEngine()
        self.player = GatePlayer()
        self.speaker = Speaker(self.engine, self.player, self.dir.name)

    def tearDown(self):
        self.speaker.close()
        self.dir.cleanup()

    def test_plays_in_order(self):
        for text in ("one", "two", "three"):
            self.speaker.say(text)
        for count in (1, 2, 3):
            wait_for(lambda: len(self.player.started) == count)
            self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        self.assertEqual(self.engine.texts, ["one", "two", "three"])
        self.assertEqual(self.player.finished, [self.speaker.cache_path(t) for t in ("one", "two", "three")])

    def test_interrupt_stops_current_and_clears_queue(self):
        self.speaker.say("first")
        wait_for(lambda: len(self.player.started) == 1)
        self.speaker.say("second")
        self.speaker.say("third")
        result = self.speaker.say("urgent", interrupt=True)
        self.assertEqual(result["queued"], 1)
        wait_for(lambda: len(self.player.started) == 2)
        self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        self.assertEqual(self.player.finished, [self.speaker.cache_path("urgent")])
        self.assertNotIn("second", self.engine.texts)

    def test_stop_clears_and_kills(self):
        self.speaker.say("first")
        wait_for(lambda: len(self.player.started) == 1)
        self.speaker.say("second")
        self.assertEqual(self.speaker.stop(), 1)
        self.assertTrue(self.speaker.wait_idle(2))
        self.assertEqual(self.player.finished, [])
        self.assertEqual(len(self.player.started), 1)

    def test_queue_limit_drops_oldest_non_interrupt(self):
        self.speaker.say("playing")
        wait_for(lambda: len(self.player.started) == 1)
        self.speaker.say("urgent", interrupt=True)  # replaces "playing", waits until released
        wait_for(lambda: len(self.player.started) == 2)
        ids = [self.speaker.say(f"line {n}")["queued"] for n in range(6)]
        self.assertEqual(ids, [1, 2, 3, 4, 5, 5])
        for count in range(2, 8):
            wait_for(lambda: len(self.player.started) == count)
            self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        played = [self.speaker.cache_path(t) for t in ["urgent"] + [f"line {n}" for n in range(1, 6)]]
        self.assertEqual(self.player.finished, played)

    def test_cache_hit_skips_synthesis(self):
        first = self.speaker.say("Stay calm.")
        self.assertFalse(first["cached"])
        wait_for(lambda: self.player.started)
        self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        second = self.speaker.say("Stay calm.")
        self.assertTrue(second["cached"])
        wait_for(lambda: len(self.player.started) == 2)
        self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        self.assertEqual(self.engine.texts, ["Stay calm."])

    def test_prerender_lists_phrases(self):
        self.assertFalse(any(p["cached"] for p in self.speaker.phrase_list()))
        self.speaker.prerender()
        phrases = self.speaker.phrase_list()
        self.assertEqual([p["text"] for p in phrases], list(speech.PHRASES))
        self.assertTrue(all(p["cached"] for p in phrases))
        self.assertTrue(self.speaker.say(speech.PHRASES[1])["cached"])

    def test_engine_failure_is_reported(self):
        def broken(text, path):
            raise RuntimeError("synth broke")
        self.engine.synth = broken
        self.speaker.say("hello")
        wait_for(lambda: self.speaker.health()["last_error"])
        self.assertIn("synth broke", self.speaker.health()["last_error"])


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.player = GatePlayer()
        self.speaker = Speaker(CountingEngine(), self.player, self.dir.name)
        self.server = SpeechServer("127.0.0.1", 0, self.speaker, TOKEN)
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.speaker.close()
        self.dir.cleanup()

    def request(self, method, path, body=None, token=TOKEN, content_type="application/json"):
        headers = {}
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        data = None
        if body is not None:
            data = body if isinstance(body, bytes) else json.dumps(body).encode()
            headers["Content-Type"] = content_type
        req = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=2) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            with error:
                return error.code, json.loads(error.read())

    def test_auth_required(self):
        for token in (None, "wrong-token-0123456789", TOKEN + "x", ""):
            status, body = self.request("POST", "/say", {"text": "hello"}, token=token)
            self.assertEqual(status, 401, token)
            self.assertFalse(body["ok"])
            self.assertEqual(self.request("POST", "/stop", b"", token=token)[0], 401)
        self.assertEqual(self.speaker.health()["queue"] + len(self.player.started), 0)

    def test_say_accepted(self):
        status, body = self.request("POST", "/say", {"text": "Can you hear me?"})
        self.assertEqual(status, 202)
        self.assertEqual(body, {"ok": True, "id": 1, "queued": body["queued"], "engine": "dry-run", "cached": False})
        wait_for(lambda: self.player.started)

    def test_validation(self):
        for payload in ({"text": ""}, {"text": "x" * 301}, {"text": "hi", "extra": 1}, {"text": 3},
                        {"text": "hi", "interrupt": "true"}, {"interrupt": True}, [], b"not json"):
            status, body = self.request("POST", "/say", payload)
            self.assertEqual(status, 400, payload)
            self.assertFalse(body["ok"])
        self.assertEqual(self.request("POST", "/say", {"text": "hi"}, content_type="text/plain")[0], 415)
        self.assertEqual(self.request("POST", "/say", b"x" * 5000)[0], 413)
        self.assertEqual(self.request("GET", "/nope", token=None)[0], 404)

    def test_stop_and_health(self):
        status, health = self.request("GET", "/health", token=None)
        self.assertEqual(status, 200)
        self.assertEqual(set(health), {"ok", "engine", "device", "queue", "speaking", "last_error"})
        self.assertEqual((health["engine"], health["queue"], health["speaking"], health["last_error"]),
                         ("dry-run", 0, False, None))
        self.assertNotIn(TOKEN, json.dumps(health))
        self.request("POST", "/say", {"text": "one"})
        wait_for(lambda: self.player.started)
        self.request("POST", "/say", {"text": "two"})
        health = self.request("GET", "/health", token=None)[1]
        self.assertEqual((health["queue"], health["speaking"]), (1, True))
        self.assertEqual(self.request("POST", "/stop", b""), (200, {"ok": True, "cleared": 1}))
        self.assertTrue(self.speaker.wait_idle(2))
        self.assertEqual(self.player.finished, [])

    def test_interrupt_over_http(self):
        self.request("POST", "/say", {"text": "one"})
        wait_for(lambda: self.player.started)
        self.request("POST", "/say", {"text": "two"})
        status, body = self.request("POST", "/say", {"text": "Responders have been notified.", "interrupt": True})
        self.assertEqual((status, body["queued"]), (202, 1))
        wait_for(lambda: len(self.player.started) == 2)
        self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        self.assertEqual(self.player.finished, [self.speaker.cache_path("Responders have been notified.")])

    def test_phrases(self):
        self.speaker.prerender()
        status, body = self.request("GET", "/phrases", token=None)
        self.assertEqual(status, 200)
        self.assertEqual(body["phrases"], [{"text": p, "cached": True} for p in speech.PHRASES])


def _fake_run(args, **kwargs):
    """Stand-in for subprocess.run: writes a WAV to the output path argument."""
    flag = "-f" if "-f" in args else "-w"
    write_silence(args[args.index(flag) + 1], 0.1)
    return subprocess.CompletedProcess(args, 0, b"", b"")


class SubprocessTests(unittest.TestCase):
    """Real engines and aplay, with subprocess mocked: argument lists only, text on stdin."""

    def check_call(self, call, text):
        args, kwargs = call.args[0], call.kwargs
        self.assertIsInstance(args, list)
        self.assertTrue(all(isinstance(a, str) for a in args))
        self.assertNotIn(text, args)
        self.assertFalse(kwargs.get("shell", False))
        self.assertEqual(kwargs["input"], (text + "\n").encode())
        self.assertIn("timeout", kwargs)

    def test_piper_command(self):
        with tempfile.TemporaryDirectory() as d, patch("subprocess.run", side_effect=_fake_run) as run:
            path = os.path.join(d, "a.wav")
            PiperEngine(["piper"], "/voices/v.onnx").synth("-rf; $(reboot)", path)
            self.check_call(run.call_args, "-rf; $(reboot)")
            self.assertEqual(run.call_args.args[0], ["piper", "-m", "/voices/v.onnx", "-f", path])

    def test_espeak_command(self):
        with tempfile.TemporaryDirectory() as d, patch("subprocess.run", side_effect=_fake_run) as run:
            path = os.path.join(d, "a.wav")
            EspeakEngine("espeak-ng").synth("Are you hurt?", path)
            self.check_call(run.call_args, "Are you hurt?")
            self.assertEqual(run.call_args.args[0], ["espeak-ng", "-v", "en-us", "-s", "150", "-w", path, "--stdin"])

    def test_engine_failure_raises(self):
        failed = subprocess.CompletedProcess([], 1, b"", b"voice not found\n")
        with patch("subprocess.run", return_value=failed):
            with self.assertRaisesRegex(RuntimeError, "voice not found"):
                EspeakEngine().synth("hi", "out.wav")

    def test_choose_engine_chain(self):
        with tempfile.TemporaryDirectory() as d:
            model = os.path.join(d, "v.onnx")
            for name in (model, model + ".json"):
                Path(name).write_text("{}")
            ok = lambda engine: True
            def which(found):
                return lambda name: f"/usr/bin/{name}" if name in found else None
            no_spec = lambda name: None
            self.assertEqual(choose_engine(model, which=which({"piper", "espeak-ng"}), find_spec=no_spec,
                                           probe=ok).command, ["/usr/bin/piper"])
            engine = choose_engine(model, which=which({"espeak-ng"}), find_spec=lambda n: object(), probe=ok)
            self.assertEqual((engine.name, engine.command[1:]), ("piper", ["-m", "piper"]))
            missing = os.path.join(d, "missing.onnx")
            self.assertEqual(choose_engine(missing, which=which({"piper", "espeak-ng"}), find_spec=no_spec,
                                           probe=ok).name, "espeak-ng")
            # Piper installed but broken: fall back to espeak-ng.
            self.assertEqual(choose_engine(model, which=which({"piper", "espeak-ng"}), find_spec=no_spec,
                                           probe=lambda e: e.name != "piper").name, "espeak-ng")
            self.assertEqual(choose_engine(model, which=which(set()), find_spec=no_spec, probe=ok).name, "dry-run")
            self.assertEqual(choose_engine(model, dry_run=True, which=which({"piper"}), probe=ok).name, "dry-run")

    def test_aplay_command_and_stop_kills(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "a.wav")
            write_silence(path, 0.1)
            proc = unittest.mock.MagicMock()
            proc.poll.return_value = None
            killed = threading.Event()
            proc.kill.side_effect = killed.set
            def communicate(timeout=None):
                killed.wait(timeout)
                proc.returncode = -9
                return b"", b""
            proc.communicate.side_effect = communicate
            player = AplayPlayer("plughw:sndrpihifiberry")
            with patch("subprocess.Popen", return_value=proc) as popen:
                result = []
                thread = threading.Thread(target=lambda: result.append(player.play(path, lambda: True)))
                thread.start()
                wait_for(lambda: popen.called)
                player.stop()
                thread.join(2)
            self.assertEqual(result, [False])
            self.assertTrue(proc.kill.called)
            args, kwargs = popen.call_args
            self.assertEqual(args[0], ["aplay", "-q", "-D", "plughw:sndrpihifiberry", path])
            self.assertFalse(kwargs.get("shell", False))
            with patch("subprocess.Popen") as popen:
                self.assertFalse(player.play(path, lambda: False))
                self.assertFalse(popen.called)

    def test_source_never_uses_a_shell(self):
        source = (ROBOT / "picrawler_control" / "speech.py").read_text(encoding="utf-8")
        self.assertNotIn("shell=", source)
        self.assertNotIn("os.system", source)
        self.assertNotIn("os.popen", source)


class ConfigTests(unittest.TestCase):
    def test_speech_defaults_and_prada_config(self):
        config = Config()
        self.assertEqual((config.speech_port, config.speech_device), (8081, "plughw:sndrpihifiberry"))
        for name in ("prada/prada.toml", "config/control.toml"):
            self.assertEqual(load(ROBOT / name).speech_port, 8081)

    def test_speech_config_rejections(self):
        for kwargs in ({"speech_port": 8080}, {"speech_port": 8765}, {"speech_port": 0},
                       {"speech_port": True}, {"speech_device": ""}, {"speech_device": "-Dnull"},
                       {"speech_voice": 3}):
            with self.assertRaises(ValueError, msg=kwargs):
                Config(**kwargs)


# ---- Listening ----

ARECORD_USB = """**** List of CAPTURE Hardware Devices ****
card 0: sndrpihifiberry [snd_rpi_hifiberry_dac], device 0: HifiBerry DAC HiFi pcm5102a-hifi-0 [HifiBerry DAC HiFi pcm5102a-hifi-0]
  Subdevices: 1/1
  Subdevice #0: subdevice #0
card 2: Camera [HD Webcam C270], device 0: Webcam Audio [Webcam Audio]
  Subdevices: 1/1
  Subdevice #0: subdevice #0
card 3: Device [USB PnP Sound Device], device 0: USB Audio [USB Audio]
  Subdevices: 1/1
  Subdevice #0: subdevice #0
"""
ARECORD_HAT_ONLY = """**** List of CAPTURE Hardware Devices ****
card 1: sndrpihifiberry [snd_rpi_hifiberry_dac], device 0: HifiBerry DAC HiFi pcm5102a-hifi-0 [HifiBerry DAC HiFi pcm5102a-hifi-0]
  Subdevices: 1/1
  Subdevice #0: subdevice #0
"""
ARECORD_NONE = "**** List of CAPTURE Hardware Devices ****\n"
WHISPER_OUT = b" Help me, I'm under the table.\n [BLANK_AUDIO]\n"


def write_noise(path, seconds, amplitude=100, rate=16000):
    """Low noise (a room hum), well under the default threshold."""
    import random
    from array import array
    rng = random.Random(1)
    samples = array("h", (rng.randint(-amplitude, amplitude) for _ in range(int(rate * seconds))))
    import wave
    with wave.open(path, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(samples.tobytes())


class FakeProcesses:
    """Stand-in for subprocess.run that plays arecord and whisper-cli."""

    def __init__(self, audio="voice", stdout=WHISPER_OUT, gate=None):
        self.audio = audio
        self.stdout = stdout
        self.gate = gate  # when set, arecord blocks until it is set
        self.calls = []
        self.recording = threading.Event()
        self.lock = threading.Lock()

    def names(self):
        with self.lock:
            return [os.path.basename(args[0]) for args, _ in self.calls]

    def __call__(self, args, **kwargs):
        with self.lock:
            self.calls.append((args, kwargs))
        name = os.path.basename(args[0])
        if name == "arecord":
            self.recording.set()
            if self.gate is not None:
                self.gate.wait(5)
            path = args[-1]
            if self.audio == "voice":
                write_tone(path, 1)
            elif self.audio == "noise":
                write_noise(path, 1)
            else:
                write_silence(path, 1, rate=16000)
            return subprocess.CompletedProcess(args, 0, b"", b"")
        if name == "whisper-cli":
            return subprocess.CompletedProcess(args, 0, self.stdout, b"")
        raise AssertionError(f"unexpected command {args}")


class ListenerTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.player = GatePlayer()
        self.speaker = Speaker(CountingEngine(), self.player, self.dir.name)

    def tearDown(self):
        self.player.release()
        self.speaker.close()
        self.dir.cleanup()

    def listener(self, stt=None, mic="plughw:3,0", **kwargs):
        kwargs.setdefault("settle", 0)
        stt = stt or WhisperEngine("/opt/whisper/whisper-cli", "/models/ggml-base.en.bin")
        return Listener(self.speaker, ArecordRecorder(mic, "arecord"), stt, mic, **kwargs)

    def test_voiced_runs_arecord_then_whisper(self):
        fake = FakeProcesses("voice")
        with patch("subprocess.run", side_effect=fake):
            status, body = self.listener().listen(5)
        self.assertEqual(status, 200, body)
        (rec, rec_kw), (stt, stt_kw) = fake.calls
        path = rec[-1]
        self.assertEqual(rec, ["arecord", "-q", "-D", "plughw:3,0", "-f", "S16_LE", "-r", "16000", "-c", "1",
                               "-d", "5", path])
        self.assertEqual(rec_kw["timeout"], 10)
        self.assertEqual(stt, ["/opt/whisper/whisper-cli", "-m", "/models/ggml-base.en.bin", "-f", path,
                               "-nt", "-np", "-l", "en", "-t", "4"])
        self.assertIn("timeout", stt_kw)
        for kwargs in (rec_kw, stt_kw):
            self.assertFalse(kwargs.get("shell", False))
        self.assertEqual(set(body), {"ok", "transcript", "silent", "rms", "voiced_ratio", "engine", "record_ms",
                                     "stt_ms", "stt_unavailable"})
        self.assertEqual((body["ok"], body["transcript"], body["silent"], body["engine"], body["stt_unavailable"]),
                         (True, "Help me, I'm under the table.", False, "whisper.cpp", False))
        self.assertGreater(body["rms"], 500)
        self.assertAlmostEqual(body["voiced_ratio"], 0.5, delta=0.05)
        self.assertFalse(os.path.exists(path))
        self.assertFalse(os.path.exists(os.path.dirname(path)))

    def test_fractional_seconds_round_up(self):
        fake = FakeProcesses("voice")
        with patch("subprocess.run", side_effect=fake):
            self.listener().listen(2.5)
        args, kwargs = fake.calls[0]
        self.assertEqual(args[args.index("-d") + 1], "3")
        self.assertEqual(kwargs["timeout"], 8)

    def test_silence_skips_stt(self):
        for audio in ("silence", "noise"):
            fake = FakeProcesses(audio)
            with patch("subprocess.run", side_effect=fake):
                status, body = self.listener().listen(3)
            self.assertEqual(status, 200)
            self.assertEqual(fake.names(), ["arecord"], audio)
            self.assertEqual((body["transcript"], body["silent"], body["stt_ms"]), (None, True, 0), audio)
            self.assertLess(body["voiced_ratio"], 0.01)
            self.assertFalse(os.path.exists(fake.calls[0][0][-1]))

    def test_stt_unavailable(self):
        fake = FakeProcesses("voice")
        with patch("subprocess.run", side_effect=fake):
            status, body = self.listener(stt=NoSTT()).listen(3)
        self.assertEqual(status, 200)
        self.assertEqual((body["transcript"], body["silent"], body["engine"], body["stt_unavailable"]),
                         (None, False, "none", True))
        self.assertEqual(fake.names(), ["arecord"])

    def test_no_mic(self):
        with patch("subprocess.run") as run:
            self.assertEqual(self.listener(mic=None).listen(3), (503, {"ok": False, "error": "no microphone"}))
            self.assertFalse(run.called)

    def test_waits_for_playback_and_holds_the_speaker(self):
        gate = threading.Event()
        fake = FakeProcesses("voice", gate=gate)
        listener = self.listener()
        self.speaker.say("Are you hurt?")
        wait_for(lambda: self.player.started)
        result = []
        with patch("subprocess.run", side_effect=fake):
            thread = threading.Thread(target=lambda: result.append(listener.listen(3)))
            thread.start()
            time.sleep(0.15)
            self.assertEqual(fake.calls, [], "recorded while the speaker was playing")
            self.player.release()
            self.assertTrue(fake.recording.wait(2))
            # A line asked for while the microphone is open waits until recording ends.
            self.speaker.say("Stay still.")
            time.sleep(0.1)
            self.assertEqual(len(self.player.started), 1)
            self.assertTrue(listener.health()["listening"])
            gate.set()
            thread.join(2)
        self.assertEqual(result[0][0], 200)
        wait_for(lambda: len(self.player.started) == 2)
        self.player.release()
        self.assertFalse(listener.health()["listening"])

    def test_concurrent_listen_conflicts(self):
        gate = threading.Event()
        fake = FakeProcesses("voice", gate=gate)
        listener = self.listener()
        result = []
        with patch("subprocess.run", side_effect=fake):
            thread = threading.Thread(target=lambda: result.append(listener.listen(3)))
            thread.start()
            self.assertTrue(fake.recording.wait(2))
            self.assertEqual(listener.listen(3), (409, {"ok": False, "error": "already listening"}))
            gate.set()
            thread.join(2)
        self.assertEqual(result[0][0], 200)
        self.assertEqual(fake.names().count("arecord"), 1)

    def test_speaker_busy_times_out(self):
        listener = self.listener(speaker_wait=0.1)
        self.speaker.say("A long line.")
        wait_for(lambda: self.player.started)
        with patch("subprocess.run") as run:
            self.assertEqual(listener.listen(3), (409, {"ok": False, "error": "speaker busy"}))
            self.assertFalse(run.called)
        self.player.release()
        self.assertTrue(self.speaker.wait_idle(2))
        # The speaker was not left held.
        self.speaker.say("Next.")
        wait_for(lambda: len(self.player.started) == 2)

    def test_record_failure_releases_speaker(self):
        failed = subprocess.CompletedProcess([], 1, b"", b"arecord: main:850: audio open error: Device or resource busy\n")
        with patch("subprocess.run", return_value=failed):
            status, body = self.listener().listen(3)
        self.assertEqual(status, 500)
        self.assertIn("resource busy", body["error"])
        self.speaker.say("Hello?")
        wait_for(lambda: self.player.started)
        self.assertFalse(self.listener().health()["listening"])

    def test_keep_audio(self):
        fake = FakeProcesses("voice")
        with patch("subprocess.run", side_effect=fake):
            self.assertEqual(self.listener(keep_audio=True).listen(3)[0], 200)
        path = fake.calls[0][0][-1]
        try:
            self.assertTrue(os.path.isfile(path))
        finally:
            import shutil
            shutil.rmtree(os.path.dirname(path), ignore_errors=True)


class ListenHelperTests(unittest.TestCase):
    def test_clean_transcript(self):
        self.assertEqual(clean_transcript(" Help me.\n [BLANK_AUDIO]\n"), "Help me.")
        self.assertEqual(clean_transcript("(silence) I'm here [Music] *coughs*\n"), "I'm here")
        for output in ("", " [BLANK_AUDIO]\n", "(silence)", " ♪♪\n", " ...\n", "[ Silence ]"):
            self.assertIsNone(clean_transcript(output), output)

    def test_pick_mic(self):
        self.assertEqual(pick_mic(ARECORD_USB), "plughw:3,0")
        no_usb = ARECORD_USB.split("card 3:")[0]
        self.assertEqual(pick_mic(no_usb), "plughw:2,0")
        self.assertIsNone(pick_mic(ARECORD_HAT_ONLY))
        self.assertIsNone(pick_mic(ARECORD_NONE))
        self.assertIsNone(pick_mic(""))

    def test_detect_mic_runs_arecord_l(self):
        def run(args, **kwargs):
            self.assertEqual(args, ["/usr/bin/arecord", "-l"])
            self.assertIn("timeout", kwargs)
            self.assertFalse(kwargs.get("shell", False))
            return subprocess.CompletedProcess(args, 0, ARECORD_USB.encode(), b"")
        self.assertEqual(detect_mic("/usr/bin/arecord", "plughw:sndrpihifiberry", run=run), "plughw:3,0")
        self.assertIsNone(detect_mic(None, "plughw:sndrpihifiberry", run=run))
        def broken(args, **kwargs):
            raise subprocess.TimeoutExpired(args, 5)
        self.assertIsNone(detect_mic("/usr/bin/arecord", "plughw:sndrpihifiberry", run=broken))

    def test_measure(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "t.wav")
            write_silence(path, 1, rate=16000)
            self.assertEqual(measure(path, 500), (0.0, 0.0, 0.0))
            write_tone(path, 2)
            rms, ratio, voiced = measure(path, 500)
            self.assertGreater(rms, 2000)
            self.assertAlmostEqual(ratio, 0.5, delta=0.03)
            self.assertAlmostEqual(voiced, 1.0, delta=0.06)

    def test_parse_listen(self):
        self.assertEqual(parse_listen(""), 5)
        self.assertEqual(parse_listen("{}"), 5)
        self.assertEqual(parse_listen('{"seconds":1}'), 1)
        self.assertEqual(parse_listen('{"seconds":10}'), 10)
        self.assertEqual(parse_listen('{"seconds":2.5}'), 2.5)
        for raw in ('{"seconds":0}', '{"seconds":0.5}', '{"seconds":11}', '{"seconds":"5"}', '{"seconds":true}',
                    '{"seconds":null}', '{"seconds":NaN}', '{"seconds":Infinity}', '{"seconds":5,"lang":"en"}',
                    '{"seconds":5,"seconds":6}', '[5]', '5', '{'):
            with self.assertRaises(ValueError, msg=raw):
                parse_listen(raw)

    def test_choose_stt_chain(self):
        with tempfile.TemporaryDirectory() as d:
            model = os.path.join(d, "ggml-base.en.bin")
            Path(model).write_bytes(b"x")
            exists = lambda path: path in ("/usr/bin/whisper-cli", "/opt/w/main", model)
            with patch("os.access", return_value=True):
                engine = choose_stt("auto", model, which=lambda n: "/usr/bin/whisper-cli", isfile=exists)
                self.assertEqual((engine.name, engine.command, engine.model), ("whisper.cpp", "/usr/bin/whisper-cli",
                                                                              model))
                self.assertEqual(choose_stt("/opt/w/main", model, which=lambda n: None, isfile=exists).command,
                                 "/opt/w/main")
                self.assertEqual(choose_stt("auto", model, which=lambda n: None, isfile=exists).name, "none")
                self.assertEqual(choose_stt("auto", os.path.join(d, "missing.bin"),
                                            which=lambda n: "/usr/bin/whisper-cli", isfile=exists).name, "none")
                self.assertEqual(choose_stt("auto", model, dry_run=True).name, "dry-run")


class ListenHttpTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.player = GatePlayer()
        self.speaker = Speaker(CountingEngine(), self.player, self.dir.name)
        self.listener = Listener(self.speaker, DryRunRecorder(wait=False), DryRunSTT("I can hear you."),
                                 "plughw:3,0", settle=0)
        self.server = SpeechServer("127.0.0.1", 0, self.speaker, TOKEN, self.listener)
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.speaker.close()
        self.dir.cleanup()

    request = HttpTests.request

    def test_listen(self):
        for body in (b"", {}, {"seconds": 2}):
            status, answer = self.request("POST", "/listen", body)
            self.assertEqual(status, 200, body)
            self.assertEqual((answer["ok"], answer["transcript"], answer["silent"], answer["engine"],
                              answer["stt_unavailable"]), (True, "I can hear you.", False, "dry-run", False))

    def test_listen_auth_and_validation(self):
        for token in (None, "wrong-token-0123456789", TOKEN + "x", ""):
            self.assertEqual(self.request("POST", "/listen", {"seconds": 3}, token=token)[0], 401, token)
        for payload in ({"seconds": 0}, {"seconds": 11}, {"seconds": "3"}, {"seconds": True}, {"seconds": 3, "x": 1},
                        [], b"not json", b'{"seconds":NaN}'):
            status, body = self.request("POST", "/listen", payload)
            self.assertEqual(status, 400, payload)
            self.assertFalse(body["ok"])
        self.assertEqual(self.request("POST", "/listen", b'{"seconds":3}', content_type="text/plain")[0], 415)
        self.assertEqual(self.request("POST", "/listen", b"x" * 5000)[0], 413)
        self.assertEqual(self.request("GET", "/listen", token=None)[0], 404)

    def test_health_shows_mic_and_stt(self):
        status, health = self.request("GET", "/health", token=None)
        self.assertEqual(status, 200)
        self.assertEqual((health["mic"], health["stt"], health["listening"]), ("plughw:3,0", "dry-run", False))
        self.assertNotIn(TOKEN, json.dumps(health))

    def test_no_microphone_503(self):
        self.listener.mic = None
        self.assertEqual(self.request("POST", "/listen", {}), (503, {"ok": False, "error": "no microphone"}))
        self.server.listener = None
        self.assertEqual(self.request("POST", "/listen", {})[0], 503)


class ListenConfigTests(unittest.TestCase):
    def test_defaults_and_prada_config(self):
        config = Config()
        self.assertEqual((config.speech_mic, config.speech_stt_bin, config.speech_vad_rms, config.speech_keep_audio),
                         ("auto", "auto", 500, False))
        self.assertTrue(config.speech_stt_model.endswith("ggml-base.en.bin"))
        prada = load(ROBOT / "prada" / "prada.toml")
        self.assertEqual((prada.speech_mic, prada.speech_vad_rms), ("auto", 500))

    def test_rejections(self):
        for kwargs in ({"speech_mic": ""}, {"speech_mic": "-Dnull"}, {"speech_mic": 3}, {"speech_stt_bin": "-h"},
                       {"speech_stt_model": ""}, {"speech_vad_rms": 49}, {"speech_vad_rms": 20001},
                       {"speech_vad_rms": 500.0}, {"speech_vad_rms": True}, {"speech_keep_audio": 1},
                       {"speech_keep_audio": "yes"}):
            with self.assertRaises(ValueError, msg=kwargs):
                Config(**kwargs)
        self.assertEqual(Config(speech_mic="plughw:2,0", speech_vad_rms=50).speech_mic, "plughw:2,0")
