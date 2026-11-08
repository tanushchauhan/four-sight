# Third-party notices

## Google Cardboard SDK

Parts of `Assets/_Project/Scripts/Stereo/` are C# ports of the Cardboard SDK
(https://github.com/googlevr/cardboard), licensed under the Apache License 2.0:

- `PolynomialRadialDistortion.cs` — from `sdk/polynomial_radial_distortion.cc`
- `LensDistortion.cs` — from `sdk/lens_distortion.cc` and `sdk/distortion_mesh.cc`
- `ViewerParams.cs` — field layout from `proto/cardboard_device.proto`, Cardboard v1 values from
  `sdk/qrcode/cardboard_v1/cardboard_v1.cc`

Parts of `Assets/_Project/Scripts/Tracking/` port the SDK's head tracking, under the same license:

- `SensorFusionEkf.cs` — from `sdk/sensors/sensor_fusion_ekf.cc`
- `GyroscopeBiasEstimator.cs` — from `sdk/sensors/gyroscope_bias_estimator.cc`
- `SensorFilters.cs` — from `sdk/sensors/lowpass_filter.cc`, `mean_filter.cc`, `median_filter.cc`
- `Rotation.cs`, `Vector3d.cs`, `Matrix3x3d.cs` — from `sdk/util/rotation.cc`, `vector.h`,
  `vectorutils.cc`, `matrix_3x3.cc`, `matrixutils.cc`

`Assets/Plugins/Android/CardboardXRImu.cpp` follows the SDK's Android sensor readers
(`sdk/sensors/android/device_gyroscope_sensor.cc`, `device_accelerometer_sensor.cc` and
`sensor_event_producer.cc`), rewritten to read both sensors on one thread into a buffer.

Changes from the original: translated from C++ to C#, triangle strips replaced with a triangle list,
an iteration cap added to the inverse distortion solver, the sensor fusion's mutex removed (samples
arrive on one thread), and `acos` clamped when reading a rotation's angle.

Copyright 2019 Google LLC. Licensed under the Apache License, Version 2.0:
http://www.apache.org/licenses/LICENSE-2.0

## ZXing.Net

`Assets/Plugins/ZXing/zxing.dll` is ZXing.Net 0.16.11 (https://github.com/micjahn/ZXing.Net),
the `lib/netstandard2.0` build from the NuGet package `ZXing.Net` 0.16.11, unmodified. Used to
decode viewer QR codes. Licensed under the Apache License 2.0.

- Package SHA-256: `7d39234d668e558b3d374d18116ed57021d7c0df482801f1e1db4f1db9314ec3`
- `zxing.dll` SHA-256: `f3b823b6fd6492525a7547989056883def5d43be1e12c4f63fa54df73e3c5cfc`

## BlazeHand models

`Assets/_Project/Hands/Models/hand_detector.onnx` and `hand_landmarks_detector.onnx` come from
https://huggingface.co/unity/inference-engine-blaze-hand, which converts Google's MediaPipe hand
models to ONNX. Licensed under the Apache License 2.0.

## Headset photos

The photos in `docs/photos/`, used by the headset timeline page, are from Wikimedia Commons. Each
keeps its own license (CC BY, CC BY-SA, CC0 or public domain), and the page credits its author and
license and links to its Commons page beside it.

## Phone door generator libraries

The phone door generator page bundles three libraries, unmodified, in `docs/door-generator/vendor/`. Their
licenses are in `vendor/LICENSES.txt` beside them.

- `manifold.js` and `manifold.wasm`: Manifold 3.5.4 (https://github.com/elalish/manifold), for the
  door's geometry. Licensed under the Apache License 2.0.
- `three.module.js`, `three.core.js` and `OrbitControls.js`: three.js r187 (https://threejs.org), for
  the 3D preview. MIT License.
- `fflate.js`: fflate 0.8.3 (https://github.com/101arrowz/fflate), for writing the 3MF and zip files.
  MIT License.

## Roboto and Material Icons

`Assets/_Project/UI/Fonts/Roboto-Regular.ttf` and `Roboto-Medium.ttf` are from
https://github.com/googlefonts/roboto (`src/hinted/`), and `MaterialIconsRound-Regular.otf` with its
`.codepoints` list from https://github.com/google/material-design-icons (`font/`), unmodified. The
SDF font assets beside them are generated from these. Copyright Google LLC. Licensed under the
Apache License 2.0.
