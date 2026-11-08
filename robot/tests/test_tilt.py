import unittest
from unittest.mock import Mock
from picrawler_control.tilt import release_test, MAX_TILT, ROCK_LOW, ROCK_CYCLES, SPEED


class TiltTests(unittest.TestCase):
    def robot(self):
        r = Mock()
        r.move_list.rotate_body_absolute_y.side_effect = lambda d: [[d, 0, 0]] * 4
        r.coord2polar.return_value = (0, 0, 0)
        r.limit_angle.return_value = (False, [0, 0, 0])
        return r

    def test_bounded_rocking_and_return_to_level(self):
        r = self.robot(); sleep = Mock()
        release_test(r, sleep)
        pitch = [c.args[0][0][0] for c in r.do_step.call_args_list]
        self.assertEqual(pitch, list(range(0, -MAX_TILT-1, -1)) + [-ROCK_LOW, -MAX_TILT] * ROCK_CYCLES + list(range(-MAX_TILT+1, 1)))
        self.assertEqual(sleep.call_count, 2 + 2 * ROCK_CYCLES)
        self.assertTrue(all(c.kwargs["speed"] == SPEED for c in r.do_step.call_args_list))
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
