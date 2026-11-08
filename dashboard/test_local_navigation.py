import unittest
try:
    import numpy as np
    import cv2
    from local_navigation import evaluate
    AVAILABLE=True
except ImportError:AVAILABLE=False

@unittest.skipUnless(AVAILABLE,'Run in dashboard/.venv-vision for model policy tests')
class FloorPolicyTests(unittest.TestCase):
    def test_open_floor_moves_forward(self):
        result,_=evaluate(np.ones((108,192),bool),[])
        self.assertEqual(result['action'],'forward')
    def test_no_detected_objects_does_not_mean_clear(self):
        result,_=evaluate(np.zeros((108,192),bool),[])
        self.assertEqual(result['action'],'wait')
    def test_near_object_blocks_even_if_floor_model_misses_it(self):
        result,_=evaluate(np.ones((108,192),bool),[dict(box=[.25,.6,.75,.99],confidence=.9,label='person')])
        self.assertEqual(result['action'],'wait')
    def test_center_obstacle_selects_clear_side(self):
        floor=np.ones((108,192),bool);floor[52:60,90:102]=False
        result,_=evaluate(floor,[dict(box=[.47,.49,.53,.58],confidence=.9,label='chair')])
        self.assertIn(result['action'],('left','right'))
    def test_near_unknown_floor_blocks_all_routes(self):
        floor=np.ones((108,192),bool);floor[88:104,64:128]=False
        result,_=evaluate(floor,[])
        self.assertEqual(result['action'],'wait')
