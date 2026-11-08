"""Hardware-free integration of keyboard + headset through the actual remote bridge."""
import asyncio
import json
import os
from pathlib import Path
import socket
import sys
import tempfile
import unittest
try:
    from websockets.asyncio.client import connect
    from websockets.asyncio.server import serve
    from picrawler_control.config import Config
    from picrawler_control.control import Control
    from picrawler_control.server import Bridge
    HAS_PROTOCOL = True
except ImportError:
    HAS_PROTOCOL = False
from control import REMOTE

@unittest.skipUnless(HAS_PROTOCOL, 'Run with robot/.venv and PYTHONPATH=robot:dashboard for protocol integration')
class CombinedBridgeTests(unittest.IsolatedAsyncioTestCase):
    async def test_keyboard_and_head_share_enable_and_stop(self):
        control=Control(Config())
        bridge=Bridge(control,'test-secret-for-bridge')
        server=await serve(bridge.handle,'127.0.0.1',0)
        motor_port=server.sockets[0].getsockname()[1]
        with socket.socket() as sock:
            sock.bind(('127.0.0.1',0)); head_port=sock.getsockname()[1]
        with tempfile.TemporaryDirectory() as tmp:
            token=Path(tmp)/'token.env';token.write_text('ROBOT_CONTROL_TOKEN=test-secret-for-bridge\n')
            source=REMOTE.replace("'/etc/picrawler-control.env'",repr(str(token))).replace('HEAD_PORT = 8766',f'HEAD_PORT = {head_port}').replace('127.0.0.1:8765',f'127.0.0.1:{motor_port}')
            process=await asyncio.create_subprocess_exec(sys.executable,'-u','-c',source,stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,env=dict(os.environ,PYTHONPATH=str(Path(__file__).resolve().parents[1]/'robot')))
            async def receive():
                raw=await asyncio.wait_for(process.stdout.readline(),5)
                self.assertTrue(raw,'Bridge terminated')
                return json.loads(raw)
            async def send(kind,**axes):
                process.stdin.write((json.dumps(dict(type=kind,**axes))+'\n').encode());await process.stdin.drain()
                return await receive()
            try:
                self.assertFalse((await receive())['enabled'])
                async with connect(f'ws://127.0.0.1:{head_port}',proxy=None) as head:
                    await head.send('{"turn":0.3}');await head.recv()
                    await send('enable')
                    reply=await send('drive',forward=0,turn=0)
                    self.assertEqual(reply['turn'],.3);self.assertEqual(reply['input_source'],'headset')
                    reply=await send('drive',forward=.5,turn=0)
                    self.assertEqual((reply['forward'],reply['turn']),(.5,0))
                    self.assertEqual(reply['input_source'],'keyboard')
                    await asyncio.sleep(.32)
                    reply=await send('drive',forward=0,turn=0)
                    self.assertEqual(reply['turn'],0)
                    reply=await send('stop')
                    self.assertFalse(reply['enabled']);self.assertEqual(reply['turn'],0)
                await send('release')
                await asyncio.wait_for(process.wait(),3)
            finally:
                if process.returncode is None: process.kill();await process.wait()
                server.close();await server.wait_closed()
