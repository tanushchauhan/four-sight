"""Exercise the shipped CLI as a real hardware-free child process."""
import asyncio
import json
import os
from pathlib import Path
import signal
import socket
import sys
import tempfile
import unittest

from websockets.asyncio.client import connect


class ProcessTests(unittest.IsolatedAsyncioTestCase):
    async def test_default_backend_and_sigterm_shutdown(self):
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            port = reserve.getsockname()[1]
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "control.toml"
            config.write_text(f'host="127.0.0.1"\nport={port}\n')
            token = "process-test-secret-123"
            process = await asyncio.create_subprocess_exec(
                sys.executable, "-m", "picrawler_control.server", "--config", str(config),
                env=dict(os.environ, ROBOT_CONTROL_TOKEN=token),
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
            try:
                # Logging is flushed immediately; wait for actual listener startup.
                async def wait_log(expected):
                    while True:
                        line = await process.stdout.readline()
                        self.assertTrue(line, f"process exited before log: {expected!r}")
                        if expected in line:
                            return
                await asyncio.wait_for(wait_log(b"dry-run listening"), 5)
                async with connect(f"ws://127.0.0.1:{port}", proxy=None) as ws:
                    await ws.send(json.dumps(dict(type="hello", token=token)))
                    status = json.loads(await ws.recv())
                    self.assertEqual(status["backend"], "dry-run")
                    self.assertFalse(status["enabled"])
                    for message in (dict(type="enable", seq=1),
                                    dict(type="drive", seq=2, forward=1, turn=0)):
                        await ws.send(json.dumps(message))
                        self.assertTrue(json.loads(await ws.recv())["enabled"])
                    await asyncio.wait_for(wait_log(b"DRY RUN: forward"), 2)
                    process.send_signal(signal.SIGTERM)
                    await asyncio.wait_for(ws.wait_closed(), 3)
                output, _ = await asyncio.wait_for(process.communicate(), 3)
                self.assertEqual(process.returncode, 0, output.decode())
                self.assertNotIn(b"Traceback", output)
            finally:
                if process.returncode is None:
                    process.kill()
                    await process.wait()

    async def test_missing_secret_fails_before_startup(self):
        env = dict(os.environ)
        env.pop("ROBOT_CONTROL_TOKEN", None)
        process = await asyncio.create_subprocess_exec(
            sys.executable, "-m", "picrawler_control.server", env=env,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
        output, _ = await asyncio.wait_for(process.communicate(), 3)
        self.assertEqual(process.returncode, 2)
        self.assertIn(b"ROBOT_CONTROL_TOKEN", output)
        self.assertNotIn(b"listening", output)
