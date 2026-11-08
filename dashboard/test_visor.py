import io
import unittest
from unittest.mock import Mock, patch
import server


class VisorMirrorTests(unittest.TestCase):
    def handler(self, body=b'', path='/api/visor/frame'):
        h = object.__new__(server.Handler)
        h.path = path
        h.headers = {'Content-Length': str(len(body)), 'Content-Type': 'image/jpeg'}
        h.rfile = io.BytesIO(body)
        h.connection = Mock()
        h.send_body = Mock()
        return h

    def test_upload_and_read_latest_frame(self):
        with patch.object(server, 'VISOR_FRAMES', server.VisorFrames()):
            frame = b'\xff\xd8test\xff\xd9'
            h = self.handler(frame)
            h.do_POST()
            self.assertEqual(h.send_body.call_args.args[0], 200)
            h.do_GET()
            self.assertEqual(h.send_body.call_args.args, (200, 'image/jpeg', frame))

    def test_rejects_invalid_or_oversized_uploads(self):
        for frame in (b'bad', b'\xff\xd8' + b'x' * 400_000 + b'\xff\xd9'):
            h = self.handler(frame)
            h.do_POST()
            self.assertEqual(h.send_body.call_args.args[0], 400)

    def test_expired_frames_are_not_served(self):
        frames = server.VisorFrames()
        with patch('server.time.monotonic', return_value=100):
            frames.put(b'\xff\xd8test\xff\xd9')
        with patch.object(server, 'VISOR_FRAMES', frames), patch('server.time.monotonic', return_value=113):
            h = self.handler()
            h.do_GET()
            self.assertEqual(h.send_body.call_args.args[0], 404)

    def test_reading_before_first_frame(self):
        with patch.object(server, 'VISOR_FRAMES', server.VisorFrames()):
            h = self.handler(path='/api/visor/status')
            h.do_GET()
            self.assertIn(b'"connected": false', h.send_body.call_args.args[2])
