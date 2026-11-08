import json
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch
from picrawler_control.live_audio import radio

class RadioTests(unittest.IsolatedAsyncioTestCase):
    def server(self):
        return SimpleNamespace(token='secret', session_mode='manual',session_generation=1,
            listener=SimpleNamespace(mic='test',_busy=threading.Lock()),
            speaker=Mock(hold=Mock(return_value=True)))
    def socket(self,token='secret'):
        return SimpleNamespace(recv=AsyncMock(return_value=json.dumps({'token':token})),send=AsyncMock(),close=AsyncMock())
    async def test_wrong_token_never_opens_audio(self):
        server=self.server();ws=self.socket('wrong')
        await radio(ws,server)
        server.speaker.stop.assert_not_called()
        self.assertFalse(server.listener._busy.locked())
    async def test_agent_ownership_rejects_live_audio(self):
        server=self.server();server.session_mode='agent';ws=self.socket()
        await radio(ws,server)
        server.speaker.hold.assert_not_called()
        self.assertIn('Take responder',ws.send.call_args.args[0])
    async def test_device_failure_releases_ownership(self):
        server=self.server();ws=self.socket()
        with patch('picrawler_control.live_audio.asyncio.create_subprocess_exec',side_effect=OSError('no audio device')):
            await radio(ws,server)
        server.speaker.release.assert_called_once()
        self.assertFalse(server.listener._busy.locked())
