"""Optional ROS 2 Jazzy telemetry; WebSocket remains the only motion authority."""
import json
import threading


class RosTelemetry:
    def __init__(self, snapshot):
        import rclpy
        from rclpy.node import Node
        from rclpy.signals import SignalHandlerOptions
        from std_msgs.msg import String
        from geometry_msgs.msg import Vector3
        self.rclpy = rclpy
        # The asyncio server owns SIGINT/SIGTERM and must stop gait scheduling
        # before shutting down ROS. Do not let rclpy replace those handlers.
        rclpy.init(args=[], signal_handler_options=SignalHandlerOptions.NO)
        self.node = Node("picrawler_control")
        status_pub = self.node.create_publisher(String, "/picrawler/status", 1)
        axes_pub = self.node.create_publisher(Vector3, "/picrawler/joystick", 1)

        def publish():
            state = snapshot()
            status_pub.publish(String(data=json.dumps(state)))
            # Normalized axes, NOT m/s or rad/s. Not measured odometry.
            axes_pub.publish(Vector3(x=state["forward"], y=0.0, z=state["turn"]))
        self.timer = self.node.create_timer(0.1, publish)

        def spin():
            from rclpy.executors import ExternalShutdownException
            try:
                rclpy.spin(self.node)
            except ExternalShutdownException:
                pass
        self.thread = threading.Thread(target=spin, daemon=True)
        self.thread.start()

    def close(self):
        self.rclpy.shutdown(uninstall_handlers=False)
        self.thread.join()
        self.node.destroy_node()
