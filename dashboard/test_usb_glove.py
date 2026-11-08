import time
import unittest
from unittest.mock import patch
from control import DebugControl
from usb_glove import USBGlove, parse_packet

class USBGloveTests(unittest.TestCase):
    def setUp(self):
        self.c=DebugControl('prada');self.c.owner='usb-owner';self.c.mode='usb'
        self.c.status={'connection':'connected','enabled':False}
        self.g=USBGlove(self.c);self.g.owner='usb-owner';self.g.received=time.monotonic()

    def test_packet_signs_and_validation(self):
        self.assertEqual(parse_packet(b'J,1,127,-127,5\r\n'),(1,-1,-1,5))
        for line in (b'junk',b'J,1,128,0,0',b'J,1,0,0,256',b'J,1,0,nan,0'):
            with self.assertRaises(ValueError):parse_packet(line)

    def test_first_click_baseline_never_enables(self):
        self.g.accept((1,0,0,30));self.assertFalse(self.c.armed)
        self.g.accept((2,0,0,31));self.assertTrue(self.c.armed)
        self.g.accept((3,.5,0,31));self.assertEqual(self.c.axes,(0,.5))
        self.g.accept((4,0,0,32));self.assertFalse(self.c.armed)

    def test_deflected_or_missed_clicks_cannot_enable(self):
        self.g.accept((1,0,0,0))
        self.g.accept((2,.5,0,1));self.assertFalse(self.c.armed)
        self.g.accept((3,0,0,3));self.assertFalse(self.c.armed)

    def test_reset_or_stale_packets_release_without_auto_reenable(self):
        self.g.accept((10,0,0,0));self.g.accept((11,0,0,1));self.assertTrue(self.c.armed)
        self.g.accept((0,0,0,0));self.assertFalse(self.c.armed);self.assertTrue(self.c.closing.is_set())
        self.assertIsNone(self.g.owner)

    def test_stale_gap_disables_even_before_worker_watchdog(self):
        self.g.accept((1,0,0,0));self.g.accept((2,0,0,1));self.assertTrue(self.c.armed)
        self.g.received-=.31
        self.g.accept((3,0,0,2));self.assertFalse(self.c.armed)
        self.assertIsNone(self.g.owner)

    def test_centered_click_stops_auto_before_async_takeover(self):
        import threading
        called=threading.Event()
        self.c.mode='auto';self.c.armed=True
        self.g.owner=None
        self.g.can_takeover=lambda:True
        self.g.takeover=called.set
        self.g.accept((1,0,0,10))
        self.assertFalse(called.is_set())
        self.g.accept((2,.7,0,11))
        self.assertFalse(called.is_set())
        self.g.accept((3,0,0,12))
        self.assertFalse(self.c.armed)
        self.assertTrue(called.wait(1))
