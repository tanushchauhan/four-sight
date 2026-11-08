"""One-shot AirTag release test. Invoke only with motor services stopped."""
import time

MAX_TILT = 17  # Largest whole-degree pitch without clipping on installed Picrawler.
SPEED = 60
STAND_SPEED = 35
ROCK_LOW = 13
ROCK_CYCLES = 3


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
    robot.do_action('stand', step=1, speed=STAND_SPEED)
    robot.do_step(sequence[0], speed=SPEED, israise=True)
    reached = 0
    try:
        for reached, pose in enumerate(sequence[1:], 1):
            robot.do_step(pose, speed=SPEED, israise=True)
        print('Rocking between nominal 13 and 17 degrees, three cycles', flush=True)
        sleep(.25)
        for _ in range(ROCK_CYCLES):
            for reached in (ROCK_LOW, MAX_TILT):
                robot.do_step(sequence[reached], speed=SPEED, israise=True)
                sleep(.15)
        sleep(.5)
    finally:
        for pose in reversed(sequence[:reached]):
            robot.do_step(pose, speed=SPEED, israise=True)
        robot.do_action('stand', step=1, speed=STAND_SPEED)
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
