import time
import unittest
from unittest.mock import Mock
from mission import Mission
from control import DebugControl


class MissionTests(unittest.TestCase):
    def setUp(self):
        self.control = DebugControl('not-a-robot')
        self.control.acquire = Mock(side_effect=self.acquire)
        self.media = Mock()
        self.mission = Mission(self.control, self.media)
        self.mission.halt.set()
        self.mission.worker.join(1)

    def acquire(self, mode):
        self.control.owner = 'test'
        self.control.mode = mode
        return 'test'

    def test_starts_only_explicitly_and_validates_settings(self):
        self.assertEqual(self.mission.mode, 'manual')
        for value in (.1, 2, True, float('nan')):
            with self.assertRaises(ValueError): self.mission.configure({'speed': value})
        self.mission.start()
        self.assertEqual(self.control.mode, 'auto')
        with self.assertRaises(ValueError): self.mission.start()

    def test_detection_requires_agent_and_can_be_disabled_live(self):
        self.mission.configure({'person_detection': True})
        with self.assertRaises(ValueError): self.mission.start()
        self.mission.agent_seen = time.monotonic()
        self.mission.start()
        self.media.request.assert_called_with('/session', {'mode': 'agent'})
        self.mission.configure({'person_detection': False})
        self.media.request.assert_called_with('/session', {'mode': 'manual'})
        self.assertFalse(self.mission.person({'confidence': .9, 'count': 1})['accepted'])
        self.assertEqual(self.mission.mode, 'autonomous')

    def test_person_stops_and_manual_blocks_new_cases(self):
        self.mission.detection = True
        self.mission.agent_seen = time.monotonic()
        self.mission.start()
        self.control.armed = True
        original_release = self.control.release
        def release():
            original_release()
            self.control.owner = None
        self.control.release = release
        self.assertTrue(self.mission.person({'confidence': .9, 'count': 1})['accepted'])
        self.assertFalse(self.control.armed)
        self.assertTrue(self.control.closing.is_set())
        self.assertEqual(self.mission.mode, 'person_found')
        self.mission.stop(mode='manual')
        self.assertFalse(self.mission.person({'confidence': .9, 'count': 1})['accepted'])

    def test_invalid_sighting_does_not_open_case(self):
        for data in ({}, {'confidence': float('nan'), 'count': 1}, {'confidence': .2, 'count': 1}):
            self.assertFalse(self.mission.person(data)['accepted'])

class MissionWorkerTests(unittest.TestCase):
    def test_external_stop_never_reenables(self):
        control=DebugControl('not-a-robot')
        def acquire(mode):
            control.owner='owner';control.mode=mode
            control.status={'connection':'connected','enabled':False}
            return 'owner'
        control.acquire=acquire
        mission=Mission(control,Mock())
        try:
            mission.start()
            end=time.monotonic()+1
            while not control.armed and time.monotonic()<end: time.sleep(.01)
            self.assertTrue(control.armed)
            control.stop_motion()
            end=time.monotonic()+1
            while mission.mode=='autonomous' and time.monotonic()<end: time.sleep(.01)
            self.assertEqual(mission.mode,'stopped')
            self.assertFalse(control.armed)
        finally: mission.close()

    def test_slow_detection_toggle_does_not_block_drive_heartbeats(self):
        import threading
        control=DebugControl('not-a-robot')
        def acquire(mode):
            control.owner='owner';control.mode=mode
            control.status={'connection':'connected','enabled':False}
            return 'owner'
        control.acquire=acquire
        entered, release = threading.Event(), threading.Event()
        media=Mock()
        def request(*args):
            entered.set();release.wait(2)
        media.request.side_effect=request
        mission=Mission(control,media)
        try:
            mission.start()
            end=time.monotonic()+1
            while not control.armed and time.monotonic()<end: time.sleep(.01)
            mission.agent_seen=time.monotonic()
            worker=threading.Thread(target=lambda:mission.configure({'person_detection':True}))
            worker.start();self.assertTrue(entered.wait(1))
            before=control.received
            time.sleep(.15)
            self.assertGreater(control.received,before)
            self.assertTrue(control.armed)
            release.set();worker.join(1)
        finally:
            release.set();mission.close()

    def test_local_vision_hold_keeps_owner_and_enabled(self):
        control=DebugControl('not-a-robot')
        def acquire(mode):
            control.owner='owner';control.mode=mode
            control.status={'connection':'connected','enabled':False}
            return 'owner'
        control.acquire=acquire
        mission=Mission(control,Mock())
        mission.guide=Mock()
        mission.guide.command.return_value=(.25,0,'Clear floor')
        try:
            mission.start()
            deadline=time.monotonic()+1
            while control.axes[0]==0 and time.monotonic()<deadline:time.sleep(.01)
            self.assertEqual(control.axes,(.25,0))
            mission.guide.command.return_value=(0,0,'Obstacle')
            deadline=time.monotonic()+1
            while control.axes[0]!=0 and time.monotonic()<deadline:time.sleep(.01)
            self.assertEqual(control.axes,(0,0))
            self.assertTrue(control.armed)
            self.assertEqual(control.owner,'owner')
            self.assertEqual(mission.mode,'autonomous')
        finally:mission.close()

class MissionRouteTests(unittest.TestCase):
    def test_global_stop_cancels_person_found_admission(self):
        import json, threading, urllib.request
        from server import Handler, ThreadingHTTPServer
        server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        server.control=DebugControl('not-a-robot')
        server.media=Mock()
        server.mission=Mission(server.control,server.media)
        server.mission.mode='person_found'
        server.mission.detection=True
        server.nonce='test-nonce'
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        base='http://127.0.0.1:'+str(server.server_port)
        try:
            request=urllib.request.Request(base+'/api/control/stop',data=b'{}',headers={
                'Origin':base,'Content-Type':'application/json','X-Control-Nonce':server.nonce})
            with urllib.request.urlopen(request,timeout=2) as r:self.assertEqual(r.status,200)
            self.assertEqual(server.mission.mode,'manual')
            self.assertFalse(server.mission.person({'count':1,'confidence':.9})['accepted'])
        finally:
            server.shutdown();server.server_close();server.mission.close()
