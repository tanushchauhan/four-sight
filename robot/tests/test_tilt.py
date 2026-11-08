import unittest
from unittest.mock import Mock
from picrawler_control.tilt import release_test, MAX_TILT


class TiltTests(unittest.TestCase):
    def robot(self):
        r = Mock()
        r.move_list.rotate_body_absolute_y.side_effect = lambda d: [[d, 0, 0]] * 4
        r.coord2polar.return_value = (0, 0, 0)
        r.limit_angle.return_value = (False, [0, 0, 0])
        return r

    def test_returns_level_and_only_holds_once(self):
        r = self.robot(); sleep = Mock()
        release_test(r, sleep)
        pitch = [c.args[0][0][0] for c in r.do_step.call_args_list]
        self.assertEqual(pitch, list(range(0, -MAX_TILT-1, -1)) + list(range(-MAX_TILT+1, 1)))
        sleep.assert_called_once_with(3)
        self.assertTrue(all(c.kwargs['israise'] for c in r.do_step.call_args_list))

    def test_invalid_pose_prevents_all_motion(self):
        r = self.robot(); r.limit_angle.return_value = (True, [])
        with self.assertRaises(ValueError): release_test(r)
        r.do_action.assert_not_called(); r.do_step.assert_not_called()

    def test_interrupted_hold_returns_level(self):
        r = self.robot()
        with self.assertRaises(RuntimeError):
            release_test(r, Mock(side_effect=RuntimeError('interrupted')))
        self.assertEqual(r.do_step.call_args.args[0][0][0], 0)
