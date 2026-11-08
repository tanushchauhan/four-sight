import io
import json
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import wave
from picrawler_control.speech import (Speaker, SpeechServer, DryRunEngine, Listener,
                                      DryRunRecorder, DryRunSTT)


class Player:
    device = 'mock'
    def __init__(self): self.played = 0
    def play(self, path, wanted):
        if not wanted(): return False
        self.played += 1
        return True
    def stop(self): pass


class RadioTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.player = Player()
        self.speaker = Speaker(DryRunEngine(), self.player, self.temp.name)
        self.listener = Listener(self.speaker, DryRunRecorder(wait=False), DryRunSTT(), 'test', settle=0)
        self.server = SpeechServer('127.0.0.1', 0, self.speaker, 'test-token', self.listener)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.speaker.close()
        self.temp.cleanup()

    def post(self, path, data, source='agent', token='test-token'):
        headers = {'Authorization': 'Bearer '+token, 'X-Speech-Source': source,
                   'Content-Type': 'audio/wav' if isinstance(data, bytes) else 'application/json'}
        request = urllib.request.Request('http://127.0.0.1:%d%s' % (self.server.server_port, path),
                                         data=data if isinstance(data, bytes) else json.dumps(data).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=3) as r: return r.status, r.read()
        except urllib.error.HTTPError as r: return r.code, r.read()

    def test_takeover_blocks_agent_but_operator_can_speak(self):
        self.assertEqual(self.post('/session', {'mode':'manual'})[0], 200)
        self.assertEqual(self.post('/say', {'text':'AI message'})[0], 409)
        self.assertEqual(self.post('/listen', {'seconds':1})[0], 409)
        self.assertEqual(self.post('/say', {'text':'Responder message'}, source='operator')[0], 202)
        self.assertEqual(self.post('/session', {'mode':'agent'})[0], 200)
        self.assertEqual(self.post('/say', {'text':'AI resumed'})[0], 202)

    def test_audio_and_reply_require_manual_session_and_valid_wav(self):
        b=io.BytesIO()
        with wave.open(b,'wb') as w:
            w.setnchannels(1);w.setsampwidth(2);w.setframerate(16000);w.writeframes(b'\0\0'*1600)
        self.assertEqual(self.post('/audio',b.getvalue())[0],409)
        self.post('/session',{'mode':'manual'})
        self.assertEqual(self.post('/audio',b.getvalue())[0],200)
        self.assertEqual(self.player.played,1)
        self.assertEqual(self.post('/audio',b'not a wav')[0],400)
        status, body=self.post('/record',{'seconds':1})
        self.assertEqual(status,200)
        with wave.open(io.BytesIO(body)) as w: self.assertEqual(w.getframerate(),16000)
        self.assertEqual(self.post('/session',{'mode':'agent'},token='wrong')[0],401)
