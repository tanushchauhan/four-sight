import unittest
from picrawler_control.autonomy import Avoidance


class AvoidanceTests(unittest.TestCase):
    def setUp(self):
        self.now = 10.
        self.policy = Avoidance(lambda: self.now)

    def sample(self, cm, age=0, speed=.5):
        return self.policy.choose(cm, self.now-age, speed)

    def test_invalid_or_stale_never_moves(self):
        for cm in (None, -1, 0, 1, 401, float('nan'), float('inf'), True):
            self.assertEqual(self.sample(cm)[:2], (0, 0))
        self.assertEqual(self.sample(100, age=.5)[:2], (0, 0))
        self.assertEqual(self.sample(100, age=-1)[:2], (0, 0))

    def test_speed_and_obstacle_turn(self):
        self.assertEqual(self.sample(100, speed=.75)[:2], (.75, 0))
        self.assertEqual(self.sample(30, speed=.75)[:2], (0, .5))
        self.now += 4.1
        self.assertEqual(self.sample(30)[:2], (0, 0))
        self.assertIsNotNone(self.policy.fault)
        self.assertEqual(self.sample(200)[:2], (0, 0))

    def test_close_obstacle_latches_stop(self):
        self.assertEqual(self.sample(10)[:2], (0, 0))
        self.assertEqual(self.sample(200)[:2], (0, 0))

    def test_hysteresis_prevents_chatter(self):
        self.sample(30)
        self.assertGreater(self.sample(45)[1], 0)
        self.sample(55)
        self.now += .1
        self.sample(55)
        self.now += .1
        self.assertEqual(self.sample(55)[:2], (0, 0))
        self.assertEqual(self.sample(55)[:2], (.5, 0))

    def test_loss_after_movement_requires_restart(self):
        self.assertEqual(self.sample(100)[:2], (.5, 0))
        self.assertEqual(self.sample(None)[:2], (0, 0))
        self.assertEqual(self.sample(100)[:2], (0, 0))
        self.assertIn('lost', self.policy.fault)
