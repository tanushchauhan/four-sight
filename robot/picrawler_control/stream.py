"""MJPEG camera stream from the robot to the visor, over local Wi-Fi.

The Pi serves HTTP. Video does not go through the control WebSocket or Spacetime.

    GET /stream
        multipart/x-mixed-replace; boundary=frame
        each part is one JPEG:
            --frame
            Content-Type: image/jpeg
            Content-Length: <n>

            <n bytes, starting FF D8 and ending FF D9>
    GET /frame
        the latest JPEG, once

Bind to the Pi's private Wi-Fi address (the same host as the control server).
The port is stream_port in the control config, 8080 unless changed. Without
--camera the server sends a moving test picture. With --camera, camera.py runs
rpicam-vid and calls FrameBuffer.publish with each JPEG; the HTTP shape is the
same either way. --host overrides the config host for the stream only, so the
control socket can stay on loopback while the visor reaches the video.

    python -m picrawler_control.stream --config robot/config/control.toml
    python -m picrawler_control.stream --config robot/prada/prada.toml --camera --host 0.0.0.0
"""

import argparse
import logging
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from .jpeg import is_jpeg, pattern

BOUNDARY = b"frame"


class FrameBuffer:
    """The one JPEG the server sends. publish replaces it; nothing is queued."""

    def __init__(self):
        self._lock = threading.Lock()
        self._jpeg = None
        self._seq = 0
        self._ready = threading.Condition(self._lock)

    def publish(self, jpeg):
        if not is_jpeg(jpeg):
            raise ValueError("frame must be a JPEG")
        with self._ready:
            self._jpeg = bytes(jpeg)
            self._seq += 1
            self._ready.notify_all()

    def latest(self):
        with self._lock:
            return self._seq, self._jpeg

    def wait_newer(self, seq, timeout):
        with self._ready:
            if self._seq == seq:
                self._ready.wait(timeout)
            return self._seq, self._jpeg


class StreamServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, host, port, frames):
        self.frames = frames
        super().__init__((host, port), _Handler)


class _Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    def log_message(self, fmt, *args):
        logging.info("%s " + fmt, self.address_string(), *args)

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/frame":
            self._frame()
        elif path == "/stream":
            self._stream()
        else:
            body = b"use /stream or /frame\n"
            self.send_response(404)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    def _frame(self):
        _, jpeg = self.server.frames.latest()
        if jpeg is None:
            body = b"no frame yet\n"
            self.send_response(503)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_response(200)
        self.send_header("Content-Type", "image/jpeg")
        self.send_header("Content-Length", str(len(jpeg)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(jpeg)

    def _stream(self):
        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        seq = 0
        try:
            while True:
                seq, jpeg = self.server.frames.wait_newer(seq, timeout=1)
                if jpeg is None:
                    continue
                header = (
                    b"--" + BOUNDARY + b"\r\n"
                    b"Content-Type: image/jpeg\r\n"
                    b"Content-Length: " + str(len(jpeg)).encode() + b"\r\n"
                    b"\r\n"
                )
                self.wfile.write(header)
                self.wfile.write(jpeg)
                self.wfile.write(b"\r\n")
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            return


def serve_pattern(frames, width=64, height=48, fps=8, stop=None):
    """Publish a generated picture until stop is set. Stand-in for the camera."""
    period = 1 / fps
    tick = 0
    while stop is None or not stop.is_set():
        started = time.monotonic()
        frames.publish(pattern(width, height, tick))
        tick += max(1, width // 16)
        remaining = period - (time.monotonic() - started)
        if stop is None:
            if remaining > 0:
                time.sleep(remaining)
        elif stop.wait(max(0, remaining)):
            return


def main():
    parser = argparse.ArgumentParser(description="PiCrawler MJPEG stream (test pattern until a camera publishes)")
    parser.add_argument("--config")
    parser.add_argument("--no-pattern", action="store_true", help="serve only frames published by a camera")
    parser.add_argument("--camera", action="store_true", help="publish frames from rpicam-vid (implies --no-pattern)")
    parser.add_argument("--host", help="bind address for the stream (default: config host)")
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=480)
    parser.add_argument("--fps", type=int, default=20)
    args = parser.parse_args()
    from .config import load
    config = load(args.config)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    frames = FrameBuffer()
    stop = threading.Event()
    if args.camera:
        from . import camera
        camera.start(frames, stop, width=args.width, height=args.height, fps=args.fps)
    elif not args.no_pattern:
        threading.Thread(target=serve_pattern, args=(frames,), kwargs={"stop": stop}, daemon=True).start()
    host = args.host or config.host
    server = StreamServer(host, config.stream_port, frames)
    logging.info("mjpeg listening on http://%s:%d/stream", host, config.stream_port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        server.server_close()


if __name__ == "__main__":
    main()
