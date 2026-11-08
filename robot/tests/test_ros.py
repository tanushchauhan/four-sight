"""ROS adapter contract test with fakes; not a substitute for a Jazzy runtime test."""
import threading
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from picrawler_control.ros import RosTelemetry


class RosContractTests(unittest.TestCase):
    def test_publishes_axes_and_preserves_server_signal_handlers(self):
        stopped = threading.Event()
        entered = threading.Event()
        status_pub, axes_pub = Mock(), Mock()
        node = Mock()
        node.create_publisher.side_effect = [status_pub, axes_pub]
        rclpy = ModuleType("rclpy")
        rclpy.init = Mock()
        rclpy.shutdown = Mock(side_effect=lambda **kw: stopped.set())
        def spin(_):
            entered.set()
            stopped.wait(1)
        rclpy.spin = spin
        node_module = ModuleType("rclpy.node")
        node_module.Node = Mock(return_value=node)
        signals = ModuleType("rclpy.signals")
        no_signals = object()
        signals.SignalHandlerOptions = SimpleNamespace(NO=no_signals)
        executors = ModuleType("rclpy.executors")
        executors.ExternalShutdownException = type("ExternalShutdownException", (Exception,), {})
        std = ModuleType("std_msgs.msg")
        geometry = ModuleType("geometry_msgs.msg")
        std.String = type("String", (SimpleNamespace,), {})
        geometry.Vector3 = type("Vector3", (SimpleNamespace,), {})
        modules = {"rclpy": rclpy, "rclpy.node": node_module, "rclpy.signals": signals,
                   "rclpy.executors": executors, "std_msgs": ModuleType("std_msgs"),
                   "std_msgs.msg": std, "geometry_msgs": ModuleType("geometry_msgs"),
                   "geometry_msgs.msg": geometry}
        state = {"forward": 0.6, "turn": -0.2, "enabled": True, "backend": "dry-run"}
        with patch.dict("sys.modules", modules):
            bridge = RosTelemetry(lambda: dict(state))
            try:
                self.assertTrue(entered.wait(1))
                rclpy.init.assert_called_once_with(args=[], signal_handler_options=no_signals)
                self.assertEqual([call.args[1] for call in node.create_publisher.call_args_list],
                                 ["/picrawler/status", "/picrawler/joystick"])
                interval, publish = node.create_timer.call_args.args
                self.assertEqual(interval, 0.1)
                publish()
                import json
                self.assertEqual(json.loads(status_pub.publish.call_args.args[0].data), state)
                axes = axes_pub.publish.call_args.args[0]
                self.assertEqual((axes.x, axes.y, axes.z), (0.6, 0.0, -0.2))
            finally:
                bridge.close()
            rclpy.shutdown.assert_called_once_with(uninstall_handlers=False)
            self.assertFalse(bridge.thread.is_alive())
            node.destroy_node.assert_called_once()
