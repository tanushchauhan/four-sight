import asyncio
import json
import time
import unittest

from websockets.asyncio.client import connect
from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed

from picrawler_control.backend import Worker
from picrawler_control.config import Config
from picrawler_control.control import Control
from picrawler_control.server import Bridge
from test_control import FakeBackend

TOKEN = "test-secret-at-least-16"


class TransportTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.c = Control(Config(timeout=0.1))
        self.b = FakeBackend()
        self.worker = Worker(self.c, self.b)
        self.bridge = Bridge(self.c, TOKEN)
        self.server = await serve(self.bridge.handle, "127.0.0.1", 0, max_size=1024, max_queue=1)
        self.worker.start()
        self.watchdog = asyncio.create_task(self.bridge.watchdog())
        self.uri = f"ws://127.0.0.1:{self.server.sockets[0].getsockname()[1]}"

    async def asyncTearDown(self):
        self.watchdog.cancel()
        await asyncio.gather(self.watchdog, return_exceptions=True)
        self.server.close()
        await self.server.wait_closed()
        self.b.release.set()
        await asyncio.to_thread(self.worker.close)

    async def session(self, token=TOKEN):
        ws = await connect(self.uri, proxy=None)
        await ws.send(json.dumps(dict(type="hello", token=token)))
        return ws

    async def send(self, ws, message):
        await ws.send(json.dumps(message))
        return json.loads(await asyncio.wait_for(ws.recv(), 1))

    async def test_auth_and_exclusive_owner(self):
        async with await self.session("wrong") as ws:
            with self.assertRaises(ConnectionClosed):
                await ws.recv()
        async with await self.session() as first:
            self.assertFalse(json.loads(await first.recv())["enabled"])
            async with await self.session() as second:
                with self.assertRaises(ConnectionClosed):
                    await second.recv()
            self.assertTrue(self.c.snapshot()["connected"])
            self.assertEqual(self.b.initialized, 0)

    async def test_responsive_stop_during_blocked_gait(self):
        async with await self.session() as ws:
            await ws.recv()
            await self.send(ws, dict(type="enable", seq=1))
            await self.send(ws, dict(type="drive", seq=2, forward=1, turn=0))
            self.assertTrue(await asyncio.to_thread(self.b.started.wait, 1))
            status = await self.send(ws, dict(type="stop", seq=3))
            self.assertFalse(status["enabled"])
            self.assertTrue(status["busy"])
            self.b.release.set()
            await asyncio.sleep(0.05)
            self.assertEqual(len(self.b.calls), 1)

    async def test_disconnect_timeout_reconnect_and_replay(self):
        ws = await self.session()
        await ws.recv()
        await self.send(ws, dict(type="enable", seq=1))
        await self.send(ws, dict(type="drive", seq=2, forward=1, turn=0))
        self.assertTrue(await asyncio.to_thread(self.b.started.wait, 1))
        await asyncio.sleep(0.15)
        self.assertFalse(self.c.enabled)  # independent watchdog while backend is blocked
        reply = await self.send(ws, dict(type="drive", seq=3, forward=1, turn=0))
        self.assertEqual(reply["type"], "error")
        await ws.wait_closed()
        self.b.release.set()
        await asyncio.sleep(0.05)
        self.assertEqual(len(self.b.calls), 1)
        async with await self.session() as ws:
            self.assertFalse(json.loads(await ws.recv())["enabled"])
            await self.send(ws, dict(type="enable", seq=0))
            reply = await self.send(ws, dict(type="drive", seq=0, forward=1, turn=0))
            self.assertEqual(reply["type"], "error")

    async def test_disconnect_stops_and_requires_new_enable(self):
        ws = await self.session()
        await ws.recv()
        await self.send(ws, dict(type="enable", seq=1))
        await self.send(ws, dict(type="drive", seq=2, forward=1, turn=0))
        self.assertTrue(await asyncio.to_thread(self.b.started.wait, 1))
        await ws.close()
        await asyncio.sleep(0.02)
        self.assertFalse(self.c.snapshot()["enabled"])
        self.b.release.set()
        await asyncio.sleep(0.05)
        self.assertEqual(len(self.b.calls), 1)

    async def test_malformed_binary_and_oversize_never_initialize(self):
        for raw in ('{"type":"drive","seq":1,"forward":NaN,"turn":0}',
                    '{"type":"enable","seq":1,"seq":2}', b'{}', 'x' * 2048):
            async with await self.session() as ws:
                await ws.recv()
                await ws.send(raw)
                try:
                    await ws.recv()  # optional error response before close
                    await ws.recv()
                except ConnectionClosed:
                    pass
            self.assertFalse(self.c.snapshot()["enabled"])
            self.assertEqual(self.b.initialized, 0)
