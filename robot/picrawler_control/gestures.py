"""Exclusive one-shot greeting and short dance; motor services must be stopped."""
import argparse


def perform(robot, name):
    if name not in ('hello', 'dance'):
        raise ValueError('Unknown gesture')
    if name == 'hello':
        robot.do_action('stand', step=1, speed=40)
        try:
            robot.do_action('wave', step=1, speed=55)
        finally:
            robot.do_action('stand', step=1, speed=40)
        return
    # A short body sway rather than the vendor's several-hundred-step dance.
    offsets = [(0, 0, 0), (-10, 0, 0), (10, 0, 0), (0, 0, 8), (0, 0, 0)]
    poses = [robot.move_list.move_body_absolute(*p) for p in offsets]
    for pose in poses:
        for coord in pose:
            a, b, g = robot.coord2polar(coord)
            clipped, _ = robot.limit_angle([b, a, g])
            if clipped:
                raise ValueError('Dance pose exceeds joint limits')
    robot.do_action('stand', step=1, speed=40)
    try:
        for _ in range(2):
            for pose in poses:
                robot.do_step(pose, speed=60, israise=True)
    finally:
        robot.do_action('stand', step=1, speed=40)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('gesture', choices=('hello', 'dance'))
    args = parser.parse_args()
    from .backend import Hardware
    h = Hardware()
    try:
        h.initialize()
        perform(h.robot, args.gesture)
    finally:
        h.close()


if __name__ == '__main__':
    main()
