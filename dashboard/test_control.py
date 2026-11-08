import time
import unittest
from unittest.mock import patch
from control import DebugControl


class DebugControlTests(unittest.TestCase):
    def setUp(self):
        self.c = DebugControl('prada')
        self.c.owner = 'owner'
        self.c.status = {'connection':'connected', 'enabled':False}
        self.c.received = time.monotonic()

    def packet(self, seq, action='heartbeat', **kw):
        return dict(owner='owner', seq=seq, action=action, **kw)

    def test_starts_disarmed_and_requires_explicit_enable(self):
        self.c.update(self.packet(0, forward=1))
        self.assertEqual(self.c.desired()[:2], (False,(0.,0.)))
        self.c.update(self.packet(1,'enable'))
        self.c.update(self.packet(2,forward=.5))
        self.assertEqual(self.c.desired()[:2], (True,(.5,0.)))

    def test_deadman_requires_new_enable(self):
        self.c.update(self.packet(0,'enable'))
        self.c.received -= .31
        self.assertFalse(self.c.desired()[0])
        self.c.update(self.packet(1,forward=1))
        self.assertFalse(self.c.desired()[0])
        self.c.update(self.packet(2,'enable'))
        self.assertTrue(self.c.desired()[0])

    def test_stop_release_and_disconnect(self):
        self.c.update(self.packet(0,'enable'))
        self.c.update(self.packet(1,'stop'))
        self.assertFalse(self.c.desired()[0])
        self.c.update(self.packet(2,'release'))
        self.assertTrue(self.c.closing.is_set())

    def test_ownership_replay_invalid_axes(self):
        for packet in [dict(owner='wrong',seq=0,action='enable'),self.packet(0,forward=float('nan')),self.packet(0,turn=2),self.packet(0,forward=True)]:
            with self.assertRaises(ValueError): self.c.update(packet)
        self.c.update(self.packet(0))
        with self.assertRaises(ValueError): self.c.update(self.packet(0))
        with self.assertRaises(ValueError): self.c.acquire()

    def test_busy_fault_and_disconnected_cannot_enable(self):
        for status in [dict(connection='connected',busy=True),dict(connection='connected',fault='motor fault'),dict(connection='connecting')]:
            self.c.status=status
            with self.assertRaises(ValueError): self.c.update(self.packet(0,'enable'))

    def test_stale_owner_eventually_releases_glove(self):
        self.c.received -= 16
        self.c.desired()
        self.assertTrue(self.c.closing.is_set())


if __name__ == '__main__': unittest.main()

class ServoZeroTests(unittest.TestCase):
    def test_control_is_blocked_during_maintenance(self):
        c = DebugControl('prada'); c.maintenance=True
        with self.assertRaises(ValueError): c.acquire()
        with self.assertRaises(ValueError): c.update({'owner':'owner','seq':0,'action':'enable'})

    @patch('control.subprocess.run')
    def test_failed_service_stop_never_touches_hardware(self, run):
        import subprocess
        c = DebugControl('prada'); c.maintenance=True
        run.side_effect=[subprocess.CompletedProcess([],0,'active',''),subprocess.TimeoutExpired('stop',30)]
        c.run_zero()
        self.assertEqual(run.call_count,2)
        self.assertEqual(c.status['connection'],'error')
        self.assertFalse(c.maintenance)
        self.assertFalse(any('Hardware' in str(call) for call in run.call_args_list))

class TiltInterlockTests(unittest.TestCase):
    @patch('control.subprocess.run')
    def test_tilt_never_runs_if_motor_service_stop_fails(self, run):
        import subprocess
        c = DebugControl('prada'); c.maintenance = True
        run.side_effect = [subprocess.CompletedProcess([], 0, 'active', ''),
                           subprocess.TimeoutExpired('stop', 30)]
        c.run_zero('tilt')
        self.assertEqual(run.call_count, 2)
        self.assertEqual(c.status['connection'], 'error')
        self.assertFalse(c.maintenance)
        self.assertFalse(any('picrawler_control.tilt' in str(call) for call in run.call_args_list))
