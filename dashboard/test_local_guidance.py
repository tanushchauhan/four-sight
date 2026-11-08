import threading
import unittest
from local_guidance import LocalGuide, valid

class LocalGuidanceTests(unittest.TestCase):
    def setUp(self):
        self.now=100
        self.guide=LocalGuide(clock=lambda:self.now,wall=lambda:self.now)
        self.guide.pending=True
    def observe(self,action,frame):
        self.guide.latest=dict(action=action,frame_id=frame,captured_at=self.now,ready=True,reason=action)
        self.guide.received=self.now
    def test_two_distinct_frames_then_continuous_even_during_gait(self):
        self.observe('forward',1)
        self.assertEqual(self.guide.command(.5)[:2],(0,0))
        self.assertEqual(self.guide.command(.5)[:2],(0,0))
        self.observe('forward',2)
        for _ in range(5):self.assertEqual(self.guide.command(.5,busy=True)[:2],(.25,0))
    def test_obstacle_and_stale_stop_without_granting_old_motion(self):
        self.observe('forward',1);self.guide.command(1)
        self.observe('forward',2);self.assertGreater(self.guide.command(1)[0],0)
        self.observe('wait',3);self.assertEqual(self.guide.command(1)[:2],(0,0))
        self.observe('forward',4);self.guide.command(1)
        self.observe('forward',5);self.guide.command(1)
        self.now+=2
        self.assertEqual(self.guide.command(1)[:2],(0,0))
    def test_route_change_needs_confirmation(self):
        for i in (1,2):self.observe('forward',i);self.guide.command(1)
        self.observe('left',3);self.assertEqual(self.guide.command(1)[:2],(0,0))
        self.observe('left',4);self.assertEqual(self.guide.command(1)[:2],(0,.3))
    def test_bad_metadata_rejected(self):
        for captured in (float('nan'),True,102,98):
            self.assertFalse(valid(dict(ready=True,captured_at=captured,frame_id=1,action='forward'),100))
    def test_reset_discards_inflight_reply(self):
        entered,release=threading.Event(),threading.Event()
        def read():entered.set();release.wait(1);return dict(ready=True,action='forward')
        self.guide.reader=read
        t=threading.Thread(target=self.guide._look,args=(self.guide.epoch,));t.start();self.assertTrue(entered.wait(1))
        self.guide.reset();release.set();t.join(1)
        self.assertIsNone(self.guide.latest)

    def test_clear_current_route_continues_while_new_preference_is_confirmed(self):
        for i in (1,2):self.observe('forward',i);self.guide.command(1)
        self.observe('left',3)
        self.guide.latest['sectors']={'forward':{'clear':True}}
        self.assertEqual(self.guide.command(1)[:2],(.3,0))
        self.observe('left',4)
        self.assertEqual(self.guide.command(1)[:2],(0,.3))
