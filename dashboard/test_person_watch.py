import time
import unittest
from unittest.mock import Mock
from person_watch import PersonWatch

class PersonWatchTests(unittest.TestCase):
    def setUp(self):
        self.watch=PersonWatch(Mock(),Mock())
        self.state=dict(session_id='first',person_detection=True,mode='watching')
    def frame(self,number,people=True):
        return dict(ready=True,frame_id=number,captured_at=time.time(),action='wait',objects=[dict(label='person',confidence=.9,box=[.1,.2,.3,.8])] if people else [])
    def test_three_distinct_frames_and_no_duplicate(self):
        self.assertIsNone(self.watch.sighting(self.frame(1),self.state))
        self.assertIsNone(self.watch.sighting(self.frame(1),self.state))
        self.assertIsNone(self.watch.sighting(self.frame(2),self.state))
        self.assertEqual(self.watch.sighting(self.frame(3),self.state)['count'],1)
        self.assertIsNone(self.watch.sighting(self.frame(4),self.state))
    def test_manual_disabled_and_stale_never_report(self):
        for mode in ('manual','stopped'):
            self.state['mode']=mode
            for i in range(5):self.assertIsNone(self.watch.sighting(self.frame(i),self.state))
        self.state['mode']='watching';self.state['person_detection']=False
        self.assertIsNone(self.watch.sighting(self.frame(9),self.state))
        self.state['person_detection']=True
        frame=self.frame(10);frame['captured_at']-=5
        self.assertIsNone(self.watch.sighting(frame,self.state))
