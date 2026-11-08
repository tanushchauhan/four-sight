import unittest
from unittest.mock import Mock, call
from picrawler_control.gestures import perform

class GestureTests(unittest.TestCase):
    def test_hello_returns_to_stand_after_wave_failure(self):
        r = Mock(); r.do_action.side_effect = [None, RuntimeError('wave'), None]
        with self.assertRaises(RuntimeError): perform(r, 'hello')
        self.assertEqual([c.args[0] for c in r.do_action.call_args_list], ['stand', 'wave', 'stand'])

    def test_unknown_gesture_never_moves(self):
        r = Mock()
        with self.assertRaises(ValueError): perform(r, 'anything')
        self.assertEqual(r.mock_calls, [])

    def test_dance_validates_before_moving_and_is_bounded(self):
        r = Mock(); r.move_list.move_body_absolute.return_value = [[45,45,-40]]*4
        r.coord2polar.return_value = (0,0,0); r.limit_angle.return_value = (True, [])
        with self.assertRaises(ValueError): perform(r, 'dance')
        r.do_action.assert_not_called(); r.do_step.assert_not_called()
        r.limit_angle.return_value = (False, [])
        perform(r, 'dance')
        self.assertEqual(r.do_step.call_count, 10)
        self.assertEqual(r.do_action.call_args.args[0], 'stand')
