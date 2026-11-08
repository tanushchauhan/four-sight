import time
import unittest
from unittest.mock import Mock
from picrawler_control.config import Config
from picrawler_control.control import Control
from picrawler_control.backend import Worker, CheckedRobot, GestureCancelled


def wait(predicate):
    end = time.monotonic() + 2
    while not predicate():
        if time.monotonic() > end: raise AssertionError('timed out')
        time.sleep(.005)


class GestureControlTests(unittest.TestCase):
    def test_multiple_sessions_reuse_initialized_hardware_and_run_once(self):
        c = Control(Config()); b = Mock(); w = Worker(c, b)
        w.start()
        try:
            for name in ('hello', 'dance', 'tilt'):
                c.connect()
                c.accept(dict(type='gesture', seq=0, name=name))
                wait(lambda: c.gesture is None and not c.busy)
                self.assertFalse(c.enabled)
                c.disconnect()
            self.assertEqual(b.initialize.call_count, 1)
            self.assertEqual([x.args[0] for x in b.gesture.call_args_list], ['hello','dance','tilt'])
            b.execute.assert_not_called()
        finally: w.close()

    def test_expiry_and_stop_cancel_pending_gesture(self):
        now = [10.]
        c = Control(Config(), lambda: now[0]); c.connect()
        c.accept(dict(type='gesture',seq=0,name='tilt'))
        now[0] += 1
        self.assertIsNone(c.choice())
        self.assertIsNone(c.gesture)
        c.accept(dict(type='gesture',seq=1,name='hello'))
        c.accept(dict(type='stop',seq=2))
        self.assertIsNone(c.choice())

    def test_drive_cannot_interrupt_gesture_and_reenable_is_explicit(self):
        c=Control(Config()); c.connect()
        c.accept(dict(type='gesture',seq=0,name='dance'))
        with self.assertRaises(ValueError): c.accept(dict(type='enable',seq=1))
        self.assertIsNone(c.gesture)
        self.assertFalse(c.enabled)

    def test_cancelled_pose_never_moves_even_recovery(self):
        robot=Mock(); checked=CheckedRobot(robot,lambda:True)
        with self.assertRaises(GestureCancelled): checked.do_step([[1,2,3]]*4)
        robot.do_step.assert_not_called()

    def test_disconnect_cancels_running_sequence_without_backend_fault(self):
        c=Control(Config()); b=Mock(); calls=[]
        def gesture(name, cancelled):
            calls.append(name)
            while not cancelled(): time.sleep(.005)
            raise GestureCancelled()
        b.gesture.side_effect=gesture
        w=Worker(c,b); c.connect(); w.start()
        try:
            c.accept(dict(type='gesture',seq=0,name='dance'))
            wait(lambda: bool(calls))
            c.disconnect()
            wait(lambda: not c.busy)
            self.assertIsNone(c.fault)
            self.assertIsNone(c.gesture)
            self.assertFalse(c.enabled)
        finally: w.close()
