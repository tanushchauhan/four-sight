"""One-shot AirTag release test. Invoke only with motor services stopped."""
import time

MAX_TILT = 17  # Largest whole-degree pitch without clipping on installed Picrawler.
SPEED = 35


def poses(robot):
    """Validate every pose before commanding motion; negative Y pitch raises front."""
    result = []
    for degree in range(MAX_TILT + 1):
        pose = robot.move_list.rotate_body_absolute_y(-degree)
        for coord in pose:
            alpha, beta, gamma = robot.coord2polar(coord)
            clipped, _ = robot.limit_angle([beta, alpha, gamma])
            if clipped:
                raise ValueError(f'Backward tilt {degree} exceeds joint limits')
        result.append(pose)
    return result


def release_test(robot, sleep=time.sleep):
    sequence = poses(robot)
    # Start in the vendor standing stance, then symmetric stance for body rotation.
    robot.do_action('stand', step=1, speed=SPEED)
    robot.do_step(sequence[0], speed=SPEED, israise=True)
    reached = 0
    try:
        for reached, pose in enumerate(sequence[1:], 1):
            robot.do_step(pose, speed=SPEED, israise=True)
        print('Holding nominal 17-degree backward pitch for 3 seconds', flush=True)
        sleep(3)
    finally:
        for pose in reversed(sequence[:reached]):
            robot.do_step(pose, speed=SPEED, israise=True)
        robot.do_action('stand', step=1, speed=SPEED)
    print('Tilt test complete; standing. AirTag release requires visual confirmation.', flush=True)


def main():
    from .backend import Hardware
    h = Hardware()
    try:
        h.initialize()
        release_test(h.robot)
    finally:
        h.close()


if __name__ == '__main__':
    main()
