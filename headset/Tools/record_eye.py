#!/usr/bin/env python3
"""Turns a screen recording of the app in viewer mode into a flat clip of one eye's view.

The phone shows each eye's image pre-distorted for the viewer's lens, side by side. This takes a
recording of that screen (QuickTime's New Movie Recording of an iPhone over USB, scrcpy --record
for Android, or the phone's own screen recorder), crops one eye, undoes the lens distortion and
writes an ordinary perspective video, 16:9 by default, for editing and YouTube.

The undistortion is the app's own lens math (LensDistortion.cs and PolynomialRadialDistortion.cs,
from the Cardboard SDK) ported to numpy: for each output pixel, the direction it looks in, then
where the app drew that direction on the screen. ffmpeg's remap filter only samples the nearest
pixel, so the map is made at twice the output size over an input scaled up twice, and the result
scaled down: that smooths it.

The phone's screen size and the viewer's lens come from the app's session log, which records them
at startup ("[Stereo] Cardboard v1: screen ..." and "[Stereo] Viewer lens: ..."); without a log,
--phone picks a known phone with the Cardboard v1 viewer.

  python3 Tools/record_eye.py clip.mov --log session.log
  python3 Tools/record_eye.py clip.mov --phone iphone -o clip-eye.mp4 --fov 70
  python3 Tools/record_eye.py clip.mp4 --phone pixel --start 12 --duration 20

Needs ffmpeg and ffprobe on the PATH, and numpy.
"""

import argparse
import math
import os
import re
import subprocess
import sys
import tempfile

import numpy as np

# Cardboard v1, as ViewerParams.CardboardV1: what the app uses until a viewer's QR code is scanned.
CARDBOARD_V1 = dict(screen_to_lens=0.042, inter_lens=0.06, tray_to_lens=0.035, alignment=0,
                    fov=(40.0, 40.0, 40.0, 40.0), k=(0.441, 0.156))
# The test phones' screens in landscape, as the app measured them (meters, pixels).
PHONES = {
    "iphone": dict(width_m=0.1411, height_m=0.0651, width_px=2556, height_px=1179),
    "pixel": dict(width_m=0.1473, height_m=0.0659, width_px=2142, height_px=960),
}
BORDER = 0.003  # LensDistortion's assumed bezel between the tray and the lit screen.
SUPERSAMPLE = 2


def parse_log(path):
    """The screen and viewer from a session log's [Stereo] startup lines."""
    screen = viewer = None
    model = None
    with open(path, encoding="utf-8", errors="replace") as log:
        for line in log:
            m = re.search(r"\[Stereo\] (.+?): screen ([\d.]+) x ([\d.]+) m \(\D*(\d+)x(\d+) px", line)
            if m:
                model = m.group(1)
                screen = dict(width_m=float(m.group(2)), height_m=float(m.group(3)),
                              width_px=int(m.group(4)), height_px=int(m.group(5)))
            m = re.search(r"\[Stereo\] Viewer lens: screenToLens ([\d.]+) interLens ([\d.]+) trayToLens ([\d.]+) "
                          r"alignment (\d) fov ([\d.,]+) k ([-\d.,]*)", line)
            if m:
                viewer = dict(screen_to_lens=float(m.group(1)), inter_lens=float(m.group(2)),
                              tray_to_lens=float(m.group(3)), alignment=int(m.group(4)),
                              fov=tuple(float(v) for v in m.group(5).split(",")),
                              k=tuple(float(v) for v in m.group(6).split(",") if v))
    if screen is None:
        sys.exit(f"No '[Stereo] ...: screen' line in {path}: is it the app's session log?")
    if viewer is None:
        if model != "Cardboard v1":
            sys.exit(f"The log's viewer is {model}, but it has no 'Viewer lens' line (an older build): "
                     "use --phone, which assumes Cardboard v1")
        viewer = CARDBOARD_V1
    return screen, viewer


def distortion_factor(k, r2):
    factor = np.ones_like(r2)
    power = np.ones_like(r2)
    for coefficient in k:
        power = power * r2
        factor = factor + coefficient * power
    return factor


def distort_radius(k, r):
    return r * distortion_factor(k, r * r)


def distort_inverse_radius(k, radius):
    """Solves distort_radius(r) = radius with the secant method, as the SDK does, for an array."""
    r0 = radius / 2.0
    r1 = radius / 3.0
    dr0 = radius - distort_radius(k, r0)
    for _ in range(30):
        dr1 = radius - distort_radius(k, r1)
        denominator = dr1 - dr0
        moving = np.abs(denominator) > 1e-12
        step = np.where(moving, dr1 * (r1 - r0) / np.where(moving, denominator, 1.0), 0.0)
        r0, r1, dr0 = r1, r1 - step, dr1
    return r1


def y_eye_offset(viewer, screen):
    if viewer["alignment"] == 0:  # bottom
        return viewer["tray_to_lens"] - BORDER
    if viewer["alignment"] == 2:  # top
        return screen["height_m"] - viewer["tray_to_lens"] - BORDER
    return screen["height_m"] / 2.0


def field_of_view(viewer, screen):
    """The left eye's (left, right, bottom, top) angles in radians, as LensDistortion works them out."""
    k, to_lens = viewer["k"], viewer["screen_to_lens"]
    outer = (screen["width_m"] - viewer["inter_lens"]) / 2.0
    inner = viewer["inter_lens"] / 2.0
    bottom = y_eye_offset(viewer, screen)
    top = screen["height_m"] - bottom

    def angle(distance):
        return math.atan(float(distort_radius(k, np.array(distance / to_lens))))

    limits = [math.radians(a) for a in viewer["fov"]]
    return (min(angle(outer), limits[0]), min(angle(inner), limits[1]),
            min(angle(bottom), limits[2]), min(angle(top), limits[3]))


def build_maps(viewer, screen, eye, out_w, out_h, h_fov_deg):
    """Where each pixel of the flat view comes from in the screen recording, in screen pixels."""
    fov = field_of_view(viewer, screen)
    if eye == "right":
        fov = (fov[1], fov[0], fov[2], fov[3])
    # A view centered on the eye's axis, as wide as asked, within what the eye sees.
    half_w = min(math.tan(fov[0]), math.tan(fov[1]))
    half_h = min(math.tan(fov[2]), math.tan(fov[3]))
    if h_fov_deg:
        half_w = min(half_w, math.tan(math.radians(h_fov_deg) / 2.0))
    if half_w * out_h / out_w > half_h:
        half_w = half_h * out_w / out_h
    half_h = half_w * out_h / out_w

    xs = ((np.arange(out_w) + 0.5) / out_w * 2.0 - 1.0) * half_w
    ys = (1.0 - (np.arange(out_h) + 0.5) / out_h * 2.0) * half_h
    tan_x, tan_y = np.meshgrid(xs, ys)
    radius = np.hypot(tan_x, tan_y)
    scale = np.where(radius > 1e-9, distort_inverse_radius(viewer["k"], radius) / np.maximum(radius, 1e-9), 1.0)
    to_lens = viewer["screen_to_lens"]
    center_x = (screen["width_m"] - viewer["inter_lens"]) / 2.0 if eye == "left" else (screen["width_m"] + viewer["inter_lens"]) / 2.0
    screen_x = tan_x * scale * to_lens + center_x
    screen_y = tan_y * scale * to_lens + y_eye_offset(viewer, screen)
    px = screen_x / screen["width_m"] * screen["width_px"]
    py = (1.0 - screen_y / screen["height_m"]) * screen["height_px"]
    return px, py, math.degrees(2 * math.atan(half_w)), math.degrees(2 * math.atan(half_h))


def write_pgm(path, values, limit):
    data = np.clip(np.rint(values), 0, limit - 1).astype(">u2")
    with open(path, "wb") as f:
        f.write(f"P5\n{data.shape[1]} {data.shape[0]}\n65535\n".encode())
        f.write(data.tobytes())


def probe_size(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                          "stream=width,height:stream_side_data=rotation", "-of", "csv=p=0:nk=1", path],
                         capture_output=True, text=True, check=True).stdout.split()
    numbers = [int(float(v)) for v in re.findall(r"-?\d+(?:\.\d+)?", " ".join(out))]
    width, height = numbers[0], numbers[1]
    rotation = numbers[2] if len(numbers) > 2 else 0
    # ffmpeg applies the rotation tag itself, so the frames it decodes are turned by it.
    if abs(rotation) % 180 == 90:
        width, height = height, width
    return width, height


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("input", help="the screen recording")
    parser.add_argument("-o", "--output", help="the clip to write (default: <input>-eye.mp4)")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--log", help="the app's session log from the recorded run (gives screen and viewer)")
    source.add_argument("--phone", choices=sorted(PHONES), help="a known phone, with the Cardboard v1 viewer")
    parser.add_argument("--eye", choices=["left", "right"], default="left")
    parser.add_argument("--size", default="1920x1080", help="output size, WxH (default 1920x1080)")
    parser.add_argument("--fov", type=float, default=0.0,
                        help="horizontal field of view in degrees; narrower is sharper (default: as wide as the eye sees)")
    parser.add_argument("--rotate", choices=["auto", "cw", "ccw", "none"], default="auto",
                        help="turn a portrait recording to landscape (auto: clockwise if it's portrait)")
    parser.add_argument("--start", type=float, help="start this many seconds in")
    parser.add_argument("--duration", type=float, help="keep this many seconds")
    parser.add_argument("--crf", type=int, default=16, help="x264 quality, lower is better (default 16)")
    args = parser.parse_args()

    screen, viewer = parse_log(args.log) if args.log else (PHONES[args.phone], CARDBOARD_V1)
    out_w, out_h = (int(v) for v in args.size.lower().split("x"))
    output = args.output or os.path.splitext(args.input)[0] + "-eye.mp4"

    in_w, in_h = probe_size(args.input)
    rotate = args.rotate
    if rotate == "auto":
        rotate = "cw" if in_h > in_w else "none"
    turn = {"cw": "transpose=1,", "ccw": "transpose=2,", "none": ""}[rotate]

    s = SUPERSAMPLE
    px, py, h_deg, v_deg = build_maps(viewer, screen, args.eye, out_w * s, out_h * s, args.fov)
    src_w, src_h = screen["width_px"] * s, screen["height_px"] * s
    print(f"{args.eye} eye, {h_deg:.0f} x {v_deg:.0f} degrees, from a {screen['width_px']}x{screen['height_px']} screen "
          f"(recording {in_w}x{in_h}{', turned ' + rotate if rotate != 'none' else ''}) to {out_w}x{out_h}")

    with tempfile.TemporaryDirectory() as folder:
        xmap, ymap = os.path.join(folder, "x.pgm"), os.path.join(folder, "y.pgm")
        # Unmapped (off-screen) pixels point at a corner, which the screen leaves black.
        write_pgm(xmap, px * s, src_w)
        write_pgm(ymap, py * s, src_h)
        graph = (f"[0:v]{turn}scale={src_w}:{src_h}:flags=lanczos,format=yuv444p[src];"
                 f"[src][1:v][2:v]remap=fill=black,scale={out_w}:{out_h}:flags=lanczos,format=yuv420p[out]")
        command = ["ffmpeg", "-hide_banner", "-y"]
        if args.start is not None:
            command += ["-ss", str(args.start)]
        if args.duration is not None:
            command += ["-t", str(args.duration)]
        command += ["-i", args.input, "-loop", "1", "-i", xmap, "-loop", "1", "-i", ymap,
                    "-filter_complex", graph, "-map", "[out]", "-map", "0:a?", "-shortest",
                    "-c:v", "libx264", "-preset", "medium", "-crf", str(args.crf), "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output]
        subprocess.run(command, check=True)
    print(f"Wrote {output}")


if __name__ == "__main__":
    main()
