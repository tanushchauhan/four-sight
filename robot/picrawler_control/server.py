import argparse
import asyncio
import hmac
import json
import logging
import os
import signal

from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed

from .backend import DryRun, Hardware, Worker
from .config import load
from .control import Control, decode


class Bridge:
    def __init__(self, control, token, backend_name="dry-run"):
        self.control = control
        self.token = token
        self.backend_name = backend_name
        self.owner = None

    def status(self):
        return dict(self.control.snapshot(), backend=self.backend_name)

    async def watchdog(self):
        while True:
            self.control.expire()
            await asyncio.sleep(self.control.config.poll_interval)

    async def handle(self, socket):
        owned = False
        try:
            raw = await asyncio.wait_for(socket.recv(), timeout=5)
            hello = decode(raw)
            token = hello.get("token")
            if (hello.keys() != {"type", "token"} or hello.get("type") != "hello"
                    or not isinstance(token, str)
                    or not hmac.compare_digest(token.encode(), self.token.encode())):
                await socket.close(1008, "authentication failed")
                return
            if self.owner is not None:
                await socket.close(1008, "controller already connected")
                return
            self.owner = socket
            owned = True
            self.control.connect()
            await socket.send(json.dumps(self.status()))
            async for raw in socket:
                try:
                    message = decode(raw)
                    self.control.accept(message)
                except ValueError as exc:
                    self.control.stop("invalid command; enable required")
                    await socket.send(json.dumps({"type": "error", "message": str(exc)}))
                    await socket.close(1008, "invalid command")
                    break
                await socket.send(json.dumps(self.status()))
                if message["type"] == "release":
                    await socket.close(1000, "released")
                    break
        except (ConnectionClosed, TimeoutError, ValueError):
            pass
        finally:
            if owned:
                self.control.disconnect()
                self.owner = None


async def run(config, token, hardware=False, ros=False):
    control = Control(config)
    backend = Hardware() if hardware else DryRun(config.dry_gait_seconds)
    worker = Worker(control, backend)
    bridge = Bridge(control, token, "hardware" if hardware else "dry-run")
    ros_bridge = None
    watchdog = None
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)
    try:
        if ros:
            from .ros import RosTelemetry
            ros_bridge = RosTelemetry(bridge.status)
        worker.start()
        watchdog = asyncio.create_task(bridge.watchdog())
        async with serve(bridge.handle, config.host, config.port,
                         max_size=1024, max_queue=1, compression=None,
                         ping_interval=5, ping_timeout=5, close_timeout=1):
            logging.info("%s listening on %s:%d", bridge.backend_name, config.host, config.port)
            await stop.wait()
            control.stop("shutdown")
    finally:
        if watchdog:
            watchdog.cancel()
            await asyncio.gather(watchdog, return_exceptions=True)
        if worker.thread.ident is not None:
            await asyncio.to_thread(worker.close)
        else:
            close = getattr(backend, "close", None)
            if close:
                close()
        if ros_bridge:
            ros_bridge.close()


def main():
    parser = argparse.ArgumentParser(description="PiCrawler local joystick bridge (dry run by default)")
    parser.add_argument("--config")
    parser.add_argument("--hardware", action="store_true", help="allow servo initialization/motion on enabled drive")
    parser.add_argument("--ros", action="store_true", help="publish ROS 2 status and normalized command telemetry")
    args = parser.parse_args()
    config = load(args.config)
    token = os.environ.get("ROBOT_CONTROL_TOKEN", "")
    if len(token) < 16:
        parser.error("set ROBOT_CONTROL_TOKEN to a secret of at least 16 characters")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    asyncio.run(run(config, token, args.hardware, args.ros))


if __name__ == "__main__":
    main()
