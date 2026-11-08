"""Terminal test joystick: momentary WASD impulses, explicit enable, space stop."""
import argparse
import asyncio
import contextlib
import json
import os
import sys
import termios
import time
import tty

from websockets.asyncio.client import connect


async def control(uri, token):
    if not sys.stdin.isatty():
        raise RuntimeError("controller needs an interactive terminal")
    loop = asyncio.get_running_loop()
    keys = asyncio.Queue()
    old = termios.tcgetattr(sys.stdin.fileno())
    seq = 0
    axes = (0.0, 0.0)
    until = 0.0
    enabled = False
    print("e enable | w/s forward/back | a/d left/right | space stop | q quit")
    print("Tap/repeat direction to move (0.2 s pulse); watch server status. Starts disabled.")
    async with connect(uri, max_size=4096, max_queue=1, close_timeout=1) as socket:
        await socket.send(json.dumps({"type": "hello", "token": token}))
        print(await socket.recv())

        async def send(kind, **extra):
            nonlocal seq, enabled
            seq += 1
            await socket.send(json.dumps({"type": kind, "seq": seq, **extra}))
            reply = json.loads(await asyncio.wait_for(socket.recv(), 2))
            if reply.get("type") == "error":
                raise RuntimeError(reply["message"])
            enabled = reply["enabled"]
            if kind != "drive" or not enabled:
                print("\r" + json.dumps(reply) + "\r")

        try:
            tty.setcbreak(sys.stdin.fileno())
            loop.add_reader(sys.stdin.fileno(), lambda: keys.put_nowait(sys.stdin.read(1)))
            while True:
                try:
                    key = await asyncio.wait_for(keys.get(), 0.05)
                except TimeoutError:
                    key = ""
                if key in ("q", "\x03", "\x04"):
                    break
                if key == "e":
                    axes = (0.0, 0.0)
                    await send("enable")
                elif key == " ":
                    axes = (0.0, 0.0)
                    await send("stop")
                elif key in {"w", "s", "a", "d"} and enabled:
                    axes = {"w": (0.5, 0.0), "s": (-0.5, 0.0),
                            "a": (0.0, 0.5), "d": (0.0, -0.5)}[key]
                    until = time.monotonic() + 0.2
                if time.monotonic() >= until:
                    axes = (0.0, 0.0)
                if enabled:
                    await send("drive", forward=axes[0], turn=axes[1])
        finally:
            loop.remove_reader(sys.stdin.fileno())
            termios.tcsetattr(sys.stdin.fileno(), termios.TCSADRAIN, old)
            with contextlib.suppress(Exception):
                await send("release")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("uri", nargs="?", default="ws://127.0.0.1:8765")
    args = parser.parse_args()
    token = os.environ.get("ROBOT_CONTROL_TOKEN", "")
    if len(token) < 16:
        parser.error("set ROBOT_CONTROL_TOKEN (same secret as robot)")
    asyncio.run(control(args.uri, token))


if __name__ == "__main__":
    main()
