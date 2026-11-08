# Known issues

## Both platforms

### Hands leave the camera's view

The hands are tracked in the rear camera's image, which covers about 68° across and only 42° up
and down (±21°). A hand reaching for something, or carrying it, often leaves the frame, usually
at a side edge: in one test on the iPhone, 4 of 8 cube drags ended that way. A carried cube stays
where it was while the hand is out of view, rather than dropping.

For the same reason nothing is poked with a fingertip: anything within reach would have to sit
inside that narrow cone, which was too cramped to use.

### Hands very close to the camera

At about 15 cm the camera sees a window only about 11 cm tall, so the hand only partly fits and
the hand model's 3D shape collapses on most results (up to 140 of 150 in five seconds). The hand's
depth there comes from the last shape that fit.

### Passthrough is flat

There's one camera, so passthrough is one image, the same in both eyes, drawn at one distance. It
gives no sense of depth, and how far the hand is from a virtual object can't be judged by eye. A
cube turns cyan when a pinch would grab it, to make up for that.

### Surfaces found after the floor are missed

Surface detection runs until the floor is found and a few seconds more, then stops to leave the
CPU to the hand models. The Cube Sandbox scans the whole room instead, until it finds nothing new;
elsewhere, a table first looked at later isn't a surface until 6DoF starts again.
ARKit took about 45 s to find its first surface in one plain room; ARCore can take minutes.

### Hand size, and so hand depth, is estimated

A hand's distance, joint positions and pinch gap all scale with the size assumed for the palm.

- **iPhone Pro**: LiDAR depth teaches the palm size, from hands 25–70 cm away with a close fit.
  At 30 cm a hand then read 30.5 cm against a ruler. The size learned varies by about ±6% between
  runs with how the hand is held, and LiDAR isn't used directly under 25 cm: at 15 cm it read 7–11%
  off, long in some runs and short in others.
- **Android**: there's no depth sensor, so the palm size comes from the hand model's own
  estimates. On the iPhone, before LiDAR taught it, the palm size left hands 8–12% nearer than a
  ruler said.

### Stray hand detections far away

The palm detector sometimes finds "hands" 0.9–1.7 m away. A hand first found beyond 80 cm is
ignored, since it can't be the user's; one being followed may go farther, since a real hand at
full stretch can. A real hand that first comes into view beyond 80 cm isn't picked up until it's
nearer.

### A missed grab can place a cube

A pinch that doesn't grab clicks whatever the head is looking at, and on a surface that places a
cube. No cube is placed within 15 cm of another, which stops most of these, but a missed grab
farther out still places one.

### Launch options need a launch from the Mac

They're passed by the scripts (intent extras on Android, environment variables on iOS), so opening
the app from the phone's home screen uses the defaults.

## iPhone 15 Pro

### 120 fps doesn't hold with hand tracking, or when the phone is hot

The app renders at 120 fps, in stereo and in 6DoF, but running the hand models needs 7–31 ms of
main-thread time a frame, more than the 8.3 ms a 120 Hz frame allows, so with a hand in view it
runs at 52–103 fps. Heat matters too: Unity's iOS player lowers the frame rate by itself when
the thermal state gets serious or critical, to 60 and 30 fps as this project sets it. From a
cooled phone, the Cube Sandbox reached iOS's "fair" thermal state in about 3½ minutes; a phone
already warm from testing reached "serious" in about 70 s. The `[Thermal]` log lines time each
change, and say when a run didn't start cold.

## Pixel 9 Pro

### The display drops to 60 Hz while the camera runs

Android holds the display at 60 Hz whenever the camera is in use, and the app can't override it,
so 6DoF, passthrough and hand tracking all run at 60 fps.

### Some screen taps freeze the view for 75–85 ms

About one in eight real finger taps (including a viewer's button) holds up the next frame for
75–85 ms, which shows as a visible hitch in the viewer. The app's own work stays at 3–6 ms per
frame. The time is spent waiting for the display.

What's known:

- Each freeze lines up with the display driver switching its TE2 rate (`hwc-display`
  `DisplayTe2Manager::setTe2Rate` writes 30 Hz, then 120 Hz within ~25 ms).
- The driver's TE2 is in its "changeable" mode (`adb shell dumpsys SurfaceFlinger`), because a
  system client keeps the proximity sensor active. In that mode it follows the display's refresh
  rate, so the pairs suggest the display drops briefly to its 30 Hz idle rate.
- Taps injected with `adb shell input tap` don't trigger it. Only physical touches do.
- No effect: turning off `Window.setFrameRateBoostOnTouchEnabled` and
  `setFrameRatePowerSavingsBalanced`, or turning on the proximity sensor from the app.
- The 120 Hz display mode on a 240 Hz vsync (what `DisplayRefreshRate` picks) avoids the same stall
  when there's no touch. The default 120 Hz mode stalled every 5–25 seconds.

Next step if it matters: record a perfetto trace during real taps to see what drops the refresh
rate.
