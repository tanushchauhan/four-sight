"""Damage routes preserve agent authorization and the dashboard's local-only writes."""
import io
import json
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from server import Handler


class DamageRoutesTests(unittest.TestCase):
    def handler(self, path='/api/damage', data=None):
        h = object.__new__(Handler)
        h.path = path
        h.server = SimpleNamespace(media=SimpleNamespace(agent='http://agent:8788', agent_token='private-agent-key'), nonce='session-nonce')
        h.headers = {'Host': 'localhost:8090', 'Origin': 'http://localhost:8090', 'X-Control-Nonce': 'session-nonce'}
        body = json.dumps(data or {}).encode()
        h.headers['Content-Length'] = str(len(body))
        h.rfile = io.BytesIO(body)
        h.wfile = io.BytesIO()
        h.control_allowed = lambda: True
        h.send_response = lambda status: setattr(h, 'response_status', status)
        h.sent_headers = {}
        h.send_header = lambda key, value: h.sent_headers.update({key: value})
        h.end_headers = lambda: None
        return h

    @patch('server.urllib.request.urlopen')
    def test_export_passes_content_type_and_download_filename(self, open_url):
        response = io.BytesIO(b'{"incidents":[]}')
        response.code = 200
        response.headers = {'Content-Type': 'application/json', 'Content-Disposition': 'attachment; filename="damage-evidence.json"'}
        open_url.return_value = response
        h = self.handler('/api/damage/export.json')
        h.do_GET()
        self.assertEqual(h.response_status, 200)
        self.assertIn('attachment;', h.sent_headers['Content-Disposition'])
        self.assertEqual(h.wfile.getvalue(), b'{"incidents":[]}')
        self.assertIsNone(open_url.call_args.args[0].get_header('Authorization'))

    @patch('server.urllib.request.urlopen')
    def test_local_analysis_forwards_private_token_without_returning_it(self, open_url):
        response = io.BytesIO(b'{"record":null}')
        response.code, response.headers = 200, {'Content-Type': 'application/json'}
        open_url.return_value = response
        h = self.handler('/api/damage/analyze')
        h.do_POST()
        request = open_url.call_args.args[0]
        self.assertEqual(request.get_header('Authorization'), 'Bearer private-agent-key')
        self.assertEqual(h.response_status, 200)
        self.assertNotIn(b'private-agent-key', h.wfile.getvalue())

    @patch('server.urllib.request.urlopen')
    def test_remote_or_cross_origin_changes_never_reach_agent(self, open_url):
        for remote in (True, False):
            h = self.handler('/api/damage/config', {'survey': 'House', 'location': 'Kitchen', 'enabled': True})
            if remote:
                h.control_allowed = lambda: False
            else:
                h.headers['Origin'] = 'http://untrusted.example'
            h.do_POST()
            self.assertEqual(h.response_status, 403)
        open_url.assert_not_called()

    @patch('server.urllib.request.urlopen', side_effect=OSError('offline'))
    def test_agent_failure_is_explicit(self, _):
        h = self.handler()
        h.do_GET()
        self.assertEqual(h.response_status, 503)
        self.assertIn(b'Damage agent unavailable', h.wfile.getvalue())


if __name__ == '__main__':
    unittest.main()
