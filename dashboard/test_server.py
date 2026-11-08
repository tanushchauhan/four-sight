import base64
import json
import subprocess
import unittest
from unittest.mock import patch
from server import collect, Monitor, PROBE


class MonitorTests(unittest.TestCase):
    @patch('server.subprocess.run')
    def test_success_keeps_image_out_of_status_and_never_sends_control(self, run):
        image = b'\xff\xd8image\xff\xd9'
        run.return_value = subprocess.CompletedProcess([], 0, json.dumps({
            'camera': {'ok': True, 'image': base64.b64encode(image).decode()},
            'joystick': True, 'headset': False, 'services': {}}), '')
        data, frame = collect('prada')
        self.assertTrue(data['reachable'])
        self.assertEqual(frame, image)
        self.assertNotIn('image', data['camera'])
        self.assertNotIn('8765', PROBE)
        self.assertNotIn('picrawler-control.env', PROBE)
        self.assertEqual(run.call_args.args[0][-3:], ['prada', 'python3', '-'])

    @patch('server.subprocess.run')
    def test_ssh_failure_clears_connection_claims(self, run):
        run.return_value = subprocess.CompletedProcess([], 255, '', 'ssh: No route to host')
        data, image = collect('prada')
        self.assertFalse(data['reachable'])
        self.assertIsNone(data['joystick'])
        self.assertIsNone(data['headset'])
        self.assertIsNone(image)
        self.assertEqual(data['services'], {})

    @patch('server.subprocess.run')
    def test_timeout_and_bad_response_are_reported(self, run):
        for outcome in [subprocess.TimeoutExpired('ssh', 18), subprocess.CompletedProcess([], 0, 'not json', '')]:
            run.side_effect = outcome if isinstance(outcome, Exception) else None
            run.return_value = outcome
            data, image = collect('prada')
            self.assertFalse(data['reachable'])
            self.assertTrue(data['error'])
            self.assertIsNone(image)

    def test_initial_state_is_unknown_not_offline(self):
        data = Monitor('prada', 5).snapshot()
        self.assertIsNone(data['reachable'])
        self.assertIsNone(data['checked_at'])
        self.assertEqual(data['stale_after'], 30)


if __name__ == '__main__':
    unittest.main()

class LocalControlAccessTests(unittest.TestCase):
    def test_own_lan_address_and_loopback_can_control(self):
        from server import local_control_client
        self.assertTrue(local_control_client('192.168.2.5', '192.168.2.5', '192.168.2.5'))
        self.assertTrue(local_control_client('127.0.0.1', '127.0.0.1', 'localhost'))

    def test_remote_lan_and_unrecognized_host_cannot_control(self):
        from server import local_control_client
        self.assertFalse(local_control_client('192.168.2.2', '192.168.2.5', '192.168.2.5'))
        self.assertFalse(local_control_client('127.0.0.1', '127.0.0.1', 'untrusted.example'))
