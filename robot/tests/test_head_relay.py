import asyncio
import json
import time
import unittest
from websockets.asyncio.client import connect
from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed
from picrawler_control.ble_relay import HeadInput, Stick, drive_axes


class HeadRelayTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.head = HeadInput()
        self.server = await serve(self.head.handle, '127.0.0.1', 0)
        self.uri = f'ws://127.0.0.1:{self.server.sockets[0].getsockname()[1]}'

    async def asyncTearDown(self):
        self.server.close()
        await self.server.wait_closed()

    async def test_current_visor_protocol_and_disconnect(self):
        async with connect(self.uri, proxy=None) as ws:
            await ws.send('{"turn":0}')
            self.assertEqual(json.loads(await ws.recv()), {'received': True})
            await ws.send('{"turn":0.25}')
            await ws.recv()
            self.assertEqual(self.head.value(), .25)
            self.head.received = time.monotonic() - .31
            self.assertEqual(self.head.value(), 0)
        await asyncio.sleep(.01)
        self.assertIsNone(self.head.owner)
        self.assertEqual(self.head.value(), 0)

    async def test_invalid_input_closes_and_clears_turn(self):
        async with connect(self.uri, proxy=None) as ws:
            await ws.send('{"turn":0.3}')
            await ws.recv()
            await ws.send('{"turn":0.8}')
            with self.assertRaises(ConnectionClosed): await ws.recv()
        self.assertEqual(self.head.value(), 0)

    def test_centered_glove_allows_head_and_stick_overrides(self):
        stick = Stick(False,False)
        self.head.turn=.3
        self.head.received=time.monotonic()
        self.assertEqual(drive_axes(stick,self.head),(0,.3))
        stick.forward=.5
        self.assertEqual(drive_axes(stick,self.head),(.5,0))
