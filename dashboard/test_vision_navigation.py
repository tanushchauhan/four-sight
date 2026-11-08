import threading
import unittest
from vision_navigation import CameraGuide

class NavigationTests(unittest.TestCase):
    def setUp(self):
        self.now=10
        self.guide=CameraGuide({},clock=lambda:self.now)
    def result(self,action,started=10):
        self.guide.result=(action,'evidence',started)
    def test_bounded_pulse_and_turn_direction(self):
        self.result('left')
        self.assertEqual(self.guide.command(1,False)[:2],(0,.3))
        self.now+=.2
        self.assertEqual(self.guide.command(1,True)[:2],(0,0))
    def test_stale_wait_and_busy_never_move(self):
        for action,started,busy in [('forward',0,False),('wait',10,False),('forward',10,True)]:
            self.guide.reset();self.result(action,started)
            self.assertEqual(self.guide.command(1,busy)[:2],(0,0))
    def test_reset_discards_inflight_reply(self):
        entered,release=threading.Event(),threading.Event()
        def analyze(config):
            entered.set();release.wait(1);return 'forward','clear'
        self.guide.analyze=analyze
        worker=threading.Thread(target=self.guide._look,args=(self.guide.epoch,))
        worker.start();self.assertTrue(entered.wait(1))
        self.guide.reset();release.set();worker.join(1)
        self.assertIsNone(self.guide.result)
    def test_provider_failure_holds(self):
        self.guide.analyze=lambda _: (_ for _ in ()).throw(TimeoutError())
        self.guide._look(self.guide.epoch)
        self.assertEqual(self.guide.command(1,False)[:2],(0,0))
        self.assertIn('retrying',self.guide.reason)
