"""Bluetooth joystick relay: Arduino 101 (controller/joystick_ble) to the local bridge.

Runs on the Pi next to the server. It reads joystick notifications over BLE and
speaks protocol v1 to the server on loopback, so the control port stays private.
Clicking the stick toggles enable/stop. If joystick data goes stale or Bluetooth
drops, the relay stops sending drive packets and the server's 0.5 s deadline
disables movement. Requires `bleak` (pip install bleak) and BlueZ.
"""
import argparse
import asyncio
import contextlib
import json
import logging
import os
import struct
import time

from websockets.asyncio.client import connect

SERVICE = "6b3f0001-8c1a-4d3e-9a0b-7d1f2c5e4a10"
JOYSTICK = "6b3f0002-8c1a-4d3e-9a0b-7d1f2c5e4a10"
STATE = "6b3f0003-8c1a-4d3e-9a0b-7d1f2c5e4a10"
STALE_SECONDS = 0.3
PERIOD = 0.05


class Stick:
    def __init__(self, invert_forward, invert_turn):
        self.forward = 0.0
        self.turn = 0.0
        self.click = False
        self.received = 0.0
        self.sign_f = -1.0 if invert_forward else 1.0
        self.sign_t = -1.0 if invert_turn else 1.0

    def update(self, _sender, data):
        if len(data) != 3:
            return
        turn, forward, click = struct.unpack("bbB", bytes(data))
        # Server convention: positive turn is left. Stick right reads positive X.
        self.turn = max(-1.0, min(1.0, -self.sign_t * turn / 127))
        self.forward = max(-1.0, min(1.0, self.sign_f * forward / 127))
        self.click = bool(click)
        self.received = time.monotonic()

    def fresh(self):
        return time.monotonic() - self.received < STALE_SECONDS


async def find_device(address):
    from bleak import BleakScanner
    if address:
        return address
    while True:
        logging.info("scanning for joystick")
        device = await BleakScanner.find_device_by_filter(
            lambda d, adv: SERVICE in [u.lower() for u in adv.service_uuids], timeout=10)
        if device:
            logging.info("found joystick %s", device.address)
            return device


async def session(device, uri, token, stick):
    from bleak import BleakClient
    async with BleakClient(device) as ble, \
            connect(uri, max_size=4096, max_queue=1, close_timeout=1) as socket:
        logging.info("joystick connected")
        await ble.start_notify(JOYSTICK, stick.update)
        await socket.send(json.dumps({"type": "hello", "token": token}))
        status = json.loads(await socket.recv())
        if status.get("type") != "status":
            raise RuntimeError(f"server refused: {status}")
        logging.info("server backend %s", status.get("backend"))
        seq = 0
        enabled = False
        last_click = False

        async def send(kind, **extra):
            nonlocal seq
            await socket.send(json.dumps({"type": kind, "seq": seq, **extra}))
            seq += 1
            reply = json.loads(await asyncio.wait_for(socket.recv(), 2))
            if reply.get("type") == "error":
                raise RuntimeError(reply.get("message"))
            return reply

        async def set_enabled(value):
            nonlocal enabled
            if value != enabled:
                logging.info("enabled" if value else "disabled")
                with contextlib.suppress(Exception):
                    await ble.write_gatt_char(STATE, bytes([1 if value else 0]), response=False)
            enabled = value

        try:
            while ble.is_connected:
                started = time.monotonic()
                click = stick.click and stick.fresh()
                if click and not last_click:
                    reply = await send("stop" if enabled else "enable")
                    await set_enabled(reply["enabled"])
                last_click = click
                if enabled:
                    if not stick.fresh():
                        await set_enabled((await send("stop"))["enabled"])
                    else:
                        reply = await send("drive", forward=round(stick.forward, 3),
                                           turn=round(stick.turn, 3))
                        await set_enabled(reply["enabled"])
                await asyncio.sleep(max(0.0, PERIOD - (time.monotonic() - started)))
        finally:
            with contextlib.suppress(Exception):
                await send("release")
            with contextlib.suppress(Exception):
                await ble.write_gatt_char(STATE, b"\x00", response=False)


async def run(uri, token, address, invert_forward, invert_turn):
    stick = Stick(invert_forward, invert_turn)
    while True:
        try:
            device = await find_device(address)
            await session(device, uri, token, stick)
            logging.info("joystick disconnected")
        except Exception as exc:  # reconnect on any BLE or server failure
            logging.warning("session ended: %s", exc)
        await asyncio.sleep(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("uri", nargs="?", default="ws://127.0.0.1:8765")
    parser.add_argument("--address", help="joystick BLE address (default: scan by service)")
    parser.add_argument("--invert-forward", action="store_true")
    parser.add_argument("--invert-turn", action="store_true")
    args = parser.parse_args()
    token = os.environ.get("ROBOT_CONTROL_TOKEN", "")
    if len(token) < 16:
        parser.error("set ROBOT_CONTROL_TOKEN (same secret as robot)")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    asyncio.run(run(args.uri, token, args.address, args.invert_forward, args.invert_turn))


if __name__ == "__main__":
    main()
