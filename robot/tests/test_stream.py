import threading
import unittest
import urllib.request

from picrawler_control.jpeg import is_jpeg, pattern
from picrawler_control.stream import FrameBuffer, StreamServer


def _read_part(response):
    line = response.readline()
    if not line.startswith(b"--frame"):
        raise AssertionError(line)
    length = None
    while True:
        header = response.readline()
        if header in (b"\r\n", b"\n"):
            break
        if header.lower().startswith(b"content-length:"):
            length = int(header.split(b":", 1)[1].strip())
    jpeg = response.read(length)
    response.readline()
    return jpeg


class StreamTests(unittest.TestCase):
    def test_pattern_is_a_jpeg_and_changes(self):
        first = pattern(64, 48, 0)
        second = pattern(64, 48, 8)
        self.assertTrue(is_jpeg(first))
        self.assertNotEqual(first, second)

    def test_publish_rejects_a_non_jpeg(self):
        with self.assertRaises(ValueError):
            FrameBuffer().publish(b"not a jpeg")

    def test_http_serves_one_frame_and_a_multipart_stream(self):
        frames = FrameBuffer()
        frames.publish(pattern(64, 48, 0))
        server = StreamServer("127.0.0.1", 0, frames)
        port = server.server_address[1]
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frame", timeout=2) as response:
                self.assertEqual(response.headers["Content-Type"], "image/jpeg")
                body = response.read()
            self.assertTrue(is_jpeg(body))

            request = urllib.request.Request(f"http://127.0.0.1:{port}/stream")
            with urllib.request.urlopen(request, timeout=2) as response:
                self.assertIn("multipart/x-mixed-replace", response.headers["Content-Type"])
                first = _read_part(response)
                frames.publish(pattern(64, 48, 16))
                second = _read_part(response)
            self.assertTrue(is_jpeg(first) and is_jpeg(second))
            self.assertNotEqual(first, second)
        finally:
            server.shutdown()
            server.server_close()


class CameraSplitTests(unittest.TestCase):
    def test_split_frames_keeps_whole_jpegs_and_leftover(self):
        from picrawler_control.camera import split_frames
        a, b = pattern(16, 16, 0), pattern(16, 16, 4)
        frames, rest = split_frames(b"junk" + a + b + b[:10])
        self.assertEqual(frames, [a, b])
        self.assertEqual(rest, b[:10])
        frames, rest = split_frames(rest + b[10:])
        self.assertEqual(frames, [])
        self.assertEqual(rest, b)
