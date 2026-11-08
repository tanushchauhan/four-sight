"""Camera source for the MJPEG stream: rpicam-vid frames into a FrameBuffer.

prada has a Camera Module 3 (IMX708) on CAM0. rpicam-vid encodes MJPEG and
writes the JPEGs back to back on stdout; this splits them and publishes each
one, so stream.py and its clients don't change. If rpicam-vid exits (camera
unplugged, libcamera error), it is restarted after a short pause.
"""

import logging
import subprocess
import threading
import time

SOI = b"\xff\xd8"
EOI_SOI = b"\xff\xd9\xff\xd8"  # end of one frame immediately followed by the next


def split_frames(buffer):
    """Return (complete JPEGs, leftover bytes) from concatenated MJPEG output."""
    frames = []
    start = buffer.find(SOI)
    if start < 0:
        return frames, b""
    while True:
        boundary = buffer.find(EOI_SOI, start + 2)
        if boundary < 0:
            return frames, buffer[start:]
        frames.append(bytes(buffer[start:boundary + 2]))
        start = boundary + 2


def rpicam_command(width, height, fps, quality, rotation=0, sensor_mode=None):
    if rotation not in (0, 180):
        raise ValueError("rotation must be 0 or 180")  # what the IMX708 can do in hardware
    return ["rpicam-vid", "--nopreview", "--timeout", "0", "--codec", "mjpeg",
            "--width", str(width), "--height", str(height), "--framerate", str(fps),
            "--quality", str(quality), "--rotation", str(rotation),
            "--autofocus-mode", "continuous", "--output", "-"] + (
            ["--mode", sensor_mode] if sensor_mode else [])


def run_camera(frames, width=640, height=480, fps=20, quality=70, rotation=0, sensor_mode=None,
               stop=None):
    """Publish camera JPEGs into frames until stop is set."""
    command = rpicam_command(width, height, fps, quality, rotation, sensor_mode)
    while stop is None or not stop.is_set():
        logging.info("starting camera: %s", " ".join(command))
        try:
            process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        except FileNotFoundError:
            logging.error("rpicam-vid not found; run robot/prada/build-camera.sh")
            return
        pending = b""
        published = 0
        window_start, window_frames = time.monotonic(), 0
        try:
            while stop is None or not stop.is_set():
                chunk = process.stdout.read1(65536)
                if not chunk:
                    break
                complete, pending = split_frames(pending + chunk)
                for jpeg in complete:
                    frames.publish(jpeg)
                    published += 1
                    window_frames += 1
                    now = time.monotonic()
                    if now - window_start >= 10:
                        logging.info("camera %.1f fps", window_frames / (now - window_start))
                        window_start, window_frames = now, 0
                    if published == 1:
                        logging.info("camera streaming (%d bytes first frame)", len(jpeg))
        finally:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
        if stop is not None and stop.is_set():
            return
        logging.warning("camera stopped (exit %s); restarting in 2 s", process.returncode)
        time.sleep(2)


def start(frames, stop, **settings):
    thread = threading.Thread(target=run_camera, args=(frames,), kwargs={"stop": stop, **settings},
                              daemon=True)
    thread.start()
    return thread
