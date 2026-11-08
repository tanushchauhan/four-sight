# Native Prada visor

Open Visor.xcodeproj, choose your signing team and iPhone, and run Visor.
Connect the phone to the same network as Prada and allow Local Network access.
The default camera URL is http://prada.local:8080/stream. If name resolution fails,
enter http://<Pi-IP>:8080/stream and tap Connect. Hide controls before wearing it.
The address persists between launches. The Pi camera service must be running.

The receiver reads the MJPEG stream directly and keeps only the latest JPEG.
Both eyes show the same image with its aspect ratio preserved and lens distortion applied.
Disconnected streams retry automatically; images older than two seconds are cleared.
The iPhone camera is not started. Head motion does not yet control the robot mount.

The existing Cardboard v1 lens profile is retained; physical alignment and distortion
must still be checked in the printed viewer. This is not a calibrated v2 lens profile.
