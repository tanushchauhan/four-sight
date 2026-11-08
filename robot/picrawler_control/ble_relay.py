"""Bluetooth joystick relay: Arduino 101 (controller/joystick_ble) to the local bridge.

Runs on the Pi next to the server. It reads joystick notifications over BLE and
speaks protocol v1 to the server on loopback, so the control port stays private.
Each click toggles enable/stop; enable is refused (error beep) unless the stick
reads centered, so a bad center can never start the robot moving by itself.
Hold the click 1.5 s on the joystick to re-center it. If joystick data goes stale or Bluetooth
drops, the relay stops sending drive packets and the server's 0.5 s deadline
disables movement. Requires `bleak` (pip install bleak) and BlueZ.
"""
import argparse
import asyncio
import contextlib
import json
import logging
import math
import os
import struct
import time

from websockets.asyncio.client import connect
from websockets.asyncio.server import serve

SERVICE = "6b3f0001-8c1a-4d3e-9a0b-7d1f2c5e4a10"
JOYSTICK = "6b3f0002-8c1a-4d3e-9a0b-7d1f2c5e4a10"
STATE = "6b3f0003-8c1a-4d3e-9a0b-7d1f2c5e4a10"
STALE_SECONDS = 0.3
CENTERED = 0.15  # both axes must be inside this to enable (same as the server deadzone)
PERIOD = 0.05


class HeadInput:
    def __init__(self):
        self.turn = 0.0
        self.received = 0.0
        self.owner = None

    def value(self):
        return self.turn if time.monotonic() - self.received < 0.3 else 0.0

    async def handle(self, socket):
        owned = False
        try:
            if self.owner is not None:
                await socket.close(1008, "headset already connected")
                return
            self.owner = socket
            owned = True
            logging.info("headset steering connected")
            last_log = 0.0
            async for raw in socket:
                msg = json.loads(raw)
                turn = msg.get("turn") if isinstance(msg, dict) else None
                if type(turn) not in (float, int) or not math.isfinite(turn) or abs(turn) > 0.4:
                    self.turn, self.received = 0.0, 0.0
                    await socket.close(1008, "invalid head turn")
                    break
                self.turn, self.received = turn, time.monotonic()
                if self.received - last_log >= 2:
                    logging.info("headset command turn=%.3f", turn)
                    last_log = self.received
                await socket.send('{"received":true}')
        except Exception as exc:
            logging.info("headset connection ended: %s", exc)
        finally:
            if owned:
                self.turn = 0.0
                self.received = 0.0
                self.owner = None
                logging.info("headset steering disconnected")


def drive_axes(stick, head):
    # Joystick owns enable/stop and overrides head input on either axis.
    if not stick.centered():
        return stick.forward, stick.turn
    return 0.0, head.value()


class Stick:
    def __init__(self, invert_forward, invert_turn):
        self.forward = 0.0
        self.turn = 0.0
        self.clicks = None  # firmware click counter; None until the first packet
        self.received = 0.0
        self.sign_f = -1.0 if invert_forward else 1.0
        self.sign_t = -1.0 if invert_turn else 1.0

    def update(self, _sender, data):
        if len(data) != 3:
            return
        turn, forward, clicks = struct.unpack("bbB", bytes(data))
        # Server convention: positive turn is left. Stick right reads positive X.
        self.turn = max(-1.0, min(1.0, -self.sign_t * turn / 127))
        self.forward = max(-1.0, min(1.0, self.sign_f * forward / 127))
        self.clicks = clicks
        self.received = time.monotonic()

    def fresh(self):
        return time.monotonic() - self.received < STALE_SECONDS

    def centered(self):
        return abs(self.forward) < CENTERED and abs(self.turn) < CENTERED


async def release_stale_links():
    """Disconnect joysticks BlueZ still holds from a relay that was killed.

    A connected peripheral stops advertising, so scanning would never find it again.
    """
    async def bluetoothctl(*args):
        proc = await asyncio.create_subprocess_exec(
            "bluetoothctl", *args, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
        out, _ = await proc.communicate()
        return out.decode(errors="replace")

    with contextlib.suppress(FileNotFoundError):
        for line in (await bluetoothctl("devices", "Connected")).splitlines():
            parts = line.split()
            if len(parts) >= 2 and parts[0] == "Device":
                if SERVICE in (await bluetoothctl("info", parts[1])).lower():
                    logging.info("releasing stale joystick link %s", parts[1])
                    await bluetoothctl("disconnect", parts[1])


async def find_device(address):
    from bleak import BleakScanner
    await release_stale_links()
    if address:
        return address
    while True:
        logging.info("scanning for joystick")
        device = await BleakScanner.find_device_by_filter(
            lambda d, adv: SERVICE in [u.lower() for u in adv.service_uuids], timeout=10)
        if device:
            logging.info("found joystick %s", device.address)
            return device


async def session(device, uri, token, stick, head):
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
        last_clicks = None

        async def send(kind, **extra):
            nonlocal seq
            await socket.send(json.dumps({"type": kind, "seq": seq, **extra}))
            seq += 1
            reply = json.loads(await asyncio.wait_for(socket.recv(), 2))
            if reply.get("type") == "error":
                raise RuntimeError(reply.get("message"))
            return reply

        async def tell_joystick(state):
            with contextlib.suppress(Exception):
                await ble.write_gatt_char(STATE, bytes([state]), response=False)

        async def set_enabled(value):
            nonlocal enabled
            if value != enabled:
                logging.info("enabled" if value else "disabled")
                await tell_joystick(1 if value else 0)
            enabled = value

        try:
            while ble.is_connected:
                started = time.monotonic()
                if stick.fresh() and stick.clicks is not None:
                    if last_clicks is not None and stick.clicks != last_clicks:
                        if enabled:
                            await set_enabled((await send("stop"))["enabled"])
                        elif stick.centered():
                            await set_enabled((await send("enable"))["enabled"])
                        else:
                            logging.warning("enable refused: stick reads forward=%.2f turn=%.2f, "
                                            "hold the click 1.5 s to re-center", stick.forward, stick.turn)
                            await tell_joystick(2)
                    last_clicks = stick.clicks
                if enabled:
                    if not stick.fresh():
                        await set_enabled((await send("stop"))["enabled"])
                    else:
                        forward, turn = drive_axes(stick, head)
                        reply = await send("drive", forward=round(forward, 3),
                                           turn=round(turn, 3))
                        await set_enabled(reply["enabled"])
                await asyncio.sleep(max(0.0, PERIOD - (time.monotonic() - started)))
        finally:
            with contextlib.suppress(Exception):
                await send("release")
            with contextlib.suppress(Exception):
                await ble.write_gatt_char(STATE, b"\x00", response=False)


async def run(uri, token, address, invert_forward, invert_turn):
    # On SIGTERM (systemctl stop/restart), cancel so the BLE link closes cleanly.
    import signal
    task = asyncio.current_task()
    asyncio.get_running_loop().add_signal_handler(signal.SIGTERM, task.cancel)
    stick = Stick(invert_forward, invert_turn)
    head = HeadInput()
    async with serve(head.handle, "0.0.0.0", 8766,
                     max_size=1024, max_queue=1, compression=None):
        await relay_loop(uri, token, address, stick, head)


async def relay_loop(uri, token, address, stick, head):
    while True:
        try:
            device = await find_device(address)
            await session(device, uri, token, stick, head)
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
    with contextlib.suppress(asyncio.CancelledError):
        asyncio.run(run(args.uri, token, args.address, args.invert_forward, args.invert_turn))
    logging.info("relay stopped")


if __name__ == "__main__":
    main()
