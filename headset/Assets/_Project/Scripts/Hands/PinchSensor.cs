using System;
using System.Diagnostics;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace CardboardXR.Hands
{
    // A pinch as a click, for 3DoF, without hand tracking's cost. Where HandTracker places the hand
    // in 3D (which needs the AR session running, depth, and the landmarker on every image), this only
    // asks whether the thumb and index fingertips are touching, which the camera image alone answers:
    // their gap over the palm's width there, about 0.23 pinched and 1.8-2.1 open in the pinch study.
    // So it opens the rear camera directly at a low resolution and rate (no ARKit or ARCore), runs the
    // same MediaPipe models on those images (the palm detector only while no hand is followed), and a
    // pinch clicks whatever the gaze is on, as a screen tap does. A click wants ~100 ms, not the
    // dragging that made a slower landmarker feel laggy. A hand has to be seen in k_SteadyResults
    // results in a row before its pinch counts, so one passing through view doesn't click. It can't
    // run alongside the AR session, which holds the camera, so it stays off in 6DoF. Enable it to
    // start. Logs [Pinch] every five seconds, with what it costs.
    public class PinchSensor : MonoBehaviour
    {
        const int k_LandmarkerSize = 224;
        static readonly (string name, int index) k_BoxesOutput = ("Identity", 0);
        static readonly (string name, int index) k_ScoresOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_LandmarksOutput = ("Identity", 0);
        static readonly (string name, int index) k_PresenceOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_HandednessOutput = ("Identity_2", 2);
        // The fingertip gap over the palm's width in the image: closer than k_CloseRatio is a pinch,
        // and it lasts until they're farther apart than k_OpenRatio.
        public const float k_CloseRatio = 0.4f;
        public const float k_OpenRatio = 0.8f;
        // A hand must have been seen in more than this many results in a row before a pinch counts.
        public const int k_SteadyResults = 2;
        const float k_StatusSeconds = 5f;
        // No frame for this long, in seconds, and the camera is opened again: on Android it can come
        // up silent when the AR session has only just let go of it.
        const float k_NoFrameSeconds = 3f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] GazePointer m_Gaze;
        [SerializeField] ModelAsset m_Detector;
        [SerializeField] ModelAsset m_Landmarker;
        [Tooltip("The camera's requested size and rate. Lower costs less; the models see at most 640 wide anyway.")]
        [SerializeField] Vector2Int m_CameraSize = new(640, 480);
        [SerializeField] int m_CameraFps = 15;
        [Tooltip("While no hand is followed, the palm detector runs at most this often, in seconds.")]
        [SerializeField] float m_SearchInterval = 0.2f;
        [SerializeField, Range(0f, 1f)] float m_MinPalmScore = 0.5f;
        [SerializeField, Range(0f, 1f)] float m_MinHandPresence = 0.5f;

        WebCamTexture m_Camera;
        Worker m_DetectorWorker;
        Worker m_LandmarkerWorker;
        int m_BoxesIndex, m_ScoresIndex, m_LandmarksIndex, m_PresenceIndex, m_HandednessIndex;
        Tensor<float> m_DetectorInput;
        Tensor<float> m_LandmarkerInput;
        float[] m_DetectorPixels;
        float[] m_LandmarkerPixels;
        float[] m_Boxes;
        float[] m_Scores;
        readonly float[] m_LandmarkValues = new float[HandGeometry.JointCount * 3];
        readonly Vector2[] m_ImageLandmarks = new Vector2[HandGeometry.JointCount];
        Vector2[] m_Anchors;
        Color32[] m_Pixels;
        // The frame the models are working on, and the newest one read since, which they swap in when
        // they're done (see Update and Step): reading the camera never waits for the models, nor
        // they for it. On a Pixel 9 Pro, taking turns left 2-5 frames a second analyzed.
        NativeArray<byte> m_Image;
        NativeArray<byte> m_NextImage;
        int m_Width, m_Height;
        int m_NextWidth, m_NextHeight;
        bool m_HasNextImage;
        bool m_ReadbackInFlight;
        bool m_Running;
        bool m_LoopActive;
        bool m_LoggedCamera;
        // Frames are read back from the GPU without stalling the main thread where the camera's pixel
        // format allows it (the Pixel's does); otherwise GetPixels32, which does stall (~4 ms on the
        // Pixel, 0.3-0.9 ms on the iPhone). The iPhone's camera texture is B8G8R8A8_SRGB, which can't
        // be read back: Unity logs an error per frame but doesn't flag the request, and hands blank
        // frames to the models, so pinches silently stopped working. Hence asking first.
        bool m_AsyncReadback = true;
        bool m_CheckedFormat;
        float m_LastFrameTime;

        readonly PinchRule m_Rule = new();
        readonly HandTurnGate m_Turn = new();
        HandRegion? m_Region;
        float m_NextSearch;

        // Statistics for the status line.
        int m_Reads, m_Frames, m_Detections, m_Landmarks, m_Losses, m_Pinches;
        double m_ReadMs, m_DetectMs, m_LandmarkMs;
        float m_MinRatio = float.PositiveInfinity, m_MaxRatio;
        float m_StatusStart;

        public bool IsPinched => m_Rule.IsPinched;
        public bool SeesHand => m_Region != null;

        // The latest result's landmarks in the camera image, in pixels (x right, y down, upright on the
        // screen), the image's size, and the chance it's a right hand: for which way the palm faces
        // (PalmGesture) and where the hand is.
        public System.Collections.Generic.IReadOnlyList<Vector2> ImageLandmarks => m_ImageLandmarks;
        public Vector2 ImageSize => new(m_Width, m_Height);
        public float RightHandScore { get; private set; } = 0.5f;

        void OnEnable()
        {
            if (m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
            {
                Debug.LogWarning("[Pinch] Not starting in 6DoF: the AR session has the camera. Hand tracking covers pinches there");
                enabled = false;
                return;
            }
            m_Running = true;
            m_StatusStart = Time.unscaledTime;
            if (!m_LoopActive)
                RunLoop();
        }

        void OnDisable()
        {
            m_Running = false;
            // Now, not when the loop next wakes: the AR session may be about to open the camera.
            if (m_Camera != null)
                m_Camera.Stop();
            if (m_Rule.IsPinched)
            {
                m_Rule.Reset();
                PinchRouter.End();
            }
            m_Region = null;
        }

        async void RunLoop()
        {
            m_LoopActive = true;
            try
            {
                if (!StartCamera())
                    return;
                Setup();
                Debug.Log($"[Pinch] On: rear camera asked for {m_CameraSize.x}x{m_CameraSize.y} at {m_CameraFps} fps; " +
                          $"a pinch closes below {k_CloseRatio:F2} palm widths and opens above {k_OpenRatio:F2}");
                while (m_Running)
                {
                    // Also leaves if something turned 6DoF on, which wants the camera.
                    if (m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
                    {
                        Debug.Log("[Pinch] Off: 6DoF wants the camera");
                        enabled = false;
                        break;
                    }
                    if (!await Step())
                        await Awaitable.NextFrameAsync();
                    LogStatus();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                Teardown();
                m_LoopActive = false;
                Debug.Log("[Pinch] Off");
            }
        }

        bool StartCamera()
        {
            string name = null;
            foreach (var device in WebCamTexture.devices)
            {
                if (!device.isFrontFacing)
                {
                    name = device.name;
                    break;
                }
            }
            if (name == null)
            {
                Debug.LogWarning("[Pinch] No rear camera");
                enabled = false;
                return false;
            }
            m_Camera = new WebCamTexture(name, m_CameraSize.x, m_CameraSize.y, m_CameraFps);
            m_Camera.Play();
            m_LastFrameTime = Time.unscaledTime;
            m_CheckedFormat = false;
            return true;
        }

        void Setup()
        {
            m_Anchors = PalmDecoder.CreateAnchors();
            var detector = ModelLoader.Load(m_Detector);
            var landmarker = ModelLoader.Load(m_Landmarker);
            m_BoxesIndex = OutputIndex(detector, k_BoxesOutput);
            m_ScoresIndex = OutputIndex(detector, k_ScoresOutput);
            m_LandmarksIndex = OutputIndex(landmarker, k_LandmarksOutput);
            m_PresenceIndex = OutputIndex(landmarker, k_PresenceOutput);
            m_HandednessIndex = OutputIndex(landmarker, k_HandednessOutput);
            m_DetectorWorker = new Worker(detector, BackendType.CPU);
            m_LandmarkerWorker = new Worker(landmarker, BackendType.CPU);
            m_DetectorInput = new Tensor<float>(new TensorShape(1, PalmDecoder.InputSize, PalmDecoder.InputSize, 3));
            m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerSize, k_LandmarkerSize, 3));
            m_DetectorPixels = new float[PalmDecoder.InputSize * PalmDecoder.InputSize * 3];
            m_LandmarkerPixels = new float[k_LandmarkerSize * k_LandmarkerSize * 3];
            m_Boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            m_Scores = new float[PalmDecoder.AnchorCount];
        }

        void Teardown()
        {
            if (m_Camera != null)
            {
                m_Camera.Stop();
                Destroy(m_Camera);
                m_Camera = null;
            }
            m_DetectorWorker?.Dispose();
            m_LandmarkerWorker?.Dispose();
            m_DetectorInput?.Dispose();
            m_LandmarkerInput?.Dispose();
            m_DetectorWorker = m_LandmarkerWorker = null;
            m_DetectorInput = m_LandmarkerInput = null;
            if (m_Image.IsCreated)
                m_Image.Dispose();
            if (m_NextImage.IsCreated)
                m_NextImage.Dispose();
            m_HasNextImage = false;
            m_ReadbackInFlight = false;
        }

        static int OutputIndex(Model model, (string name, int index) output)
        {
            for (var i = 0; i < model.outputs.Count; i++)
            {
                if (model.outputs[i].name == output.name)
                    return i;
            }
            return output.index;
        }

        // Runs the newest camera frame through the models. False if there was no new frame, or it
        // isn't time to look for a hand yet.
        async Awaitable<bool> Step()
        {
            if (!m_HasNextImage)
                return false;
            if (m_Region == null && Time.unscaledTime < m_NextSearch)
                return false;

            // The newest frame becomes the one worked on; the old buffer takes the next read.
            (m_Image, m_NextImage) = (m_NextImage, m_Image);
            m_Width = m_NextWidth;
            m_Height = m_NextHeight;
            m_HasNextImage = false;
            m_Frames++;
            var stopwatch = Stopwatch.StartNew();

            if (m_Region == null)
            {
                stopwatch.Restart();
                var letterbox = Letterbox.Fit(m_Width, m_Height, PalmDecoder.InputSize);
                ImageSampler.SampleLetterbox(m_Image, m_Width, m_Height, letterbox, m_DetectorPixels);
                m_DetectorInput.Upload(m_DetectorPixels);
                m_DetectorWorker.Schedule(m_DetectorInput);
                using (var scores = await (m_DetectorWorker.PeekOutput(m_ScoresIndex) as Tensor<float>).ReadbackAndCloneAsync())
                    CopyTo(scores, m_Scores);
                using (var boxes = (m_DetectorWorker.PeekOutput(m_BoxesIndex) as Tensor<float>).ReadbackAndClone())
                    CopyTo(boxes, m_Boxes);
                m_DetectMs += stopwatch.Elapsed.TotalMilliseconds;
                m_Detections++;
                if (!m_Running)
                    return true;
                if (!PalmDecoder.TryDecodeBest(m_Boxes, m_Scores, m_Anchors, m_MinPalmScore, out var palm))
                {
                    m_NextSearch = Time.unscaledTime + m_SearchInterval;
                    return true;
                }
                m_Region = HandRegion.FromPalm(palm, letterbox);
            }

            stopwatch.Restart();
            var region = m_Region.Value;
            ImageSampler.SampleRegion(m_Image, m_Width, m_Height, region, k_LandmarkerSize, m_LandmarkerPixels);
            m_LandmarkerInput.Upload(m_LandmarkerPixels);
            m_LandmarkerWorker.Schedule(m_LandmarkerInput);
            using (var landmarks = await (m_LandmarkerWorker.PeekOutput(m_LandmarksIndex) as Tensor<float>).ReadbackAndCloneAsync())
                CopyTo(landmarks, m_LandmarkValues);
            float presence, handedness;
            using (var output = (m_LandmarkerWorker.PeekOutput(m_PresenceIndex) as Tensor<float>).ReadbackAndClone())
                presence = output[0];
            using (var output = (m_LandmarkerWorker.PeekOutput(m_HandednessIndex) as Tensor<float>).ReadbackAndClone())
                handedness = output[0];
            // The model may give a probability or a logit.
            RightHandScore = handedness < 0f || handedness > 1f ? PalmDecoder.Sigmoid(handedness) : handedness;
            m_LandmarkMs += stopwatch.Elapsed.TotalMilliseconds;
            m_Landmarks++;
            if (!m_Running)
                return true;

            if (presence < 0f || presence > 1f)
                presence = PalmDecoder.Sigmoid(presence);
            if (presence < m_MinHandPresence)
            {
                Lose();
                return true;
            }

            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var crop = new Vector2(m_LandmarkValues[i * 3], m_LandmarkValues[i * 3 + 1]) / k_LandmarkerSize;
                m_ImageLandmarks[i] = region.CropToImage(crop);
            }
            m_Region = HandRegion.FromLandmarks(m_ImageLandmarks);
            var ratio = Vector2.Distance(m_ImageLandmarks[HandGeometry.ThumbTip], m_ImageLandmarks[HandGeometry.IndexTip]) /
                        Mathf.Max(1f, HandGeometry.PalmSpan(m_ImageLandmarks));
            m_MinRatio = Mathf.Min(m_MinRatio, ratio);
            m_MaxRatio = Mathf.Max(m_MaxRatio, ratio);
            // No pinch starts while the hand turns over (HandTurnGate): edge-on, the fingertips meet in
            // the image too.
            var turning = m_Turn.Update(PalmGesture.Facing(m_ImageLandmarks[PalmGesture.k_Wrist],
                m_ImageLandmarks[PalmGesture.k_IndexKnuckle], m_ImageLandmarks[PalmGesture.k_PinkyKnuckle], RightHandScore >= 0.5f),
                Time.unscaledTime);
            if (turning && !m_Rule.IsPinched && ratio < k_CloseRatio)
            {
                Debug.Log($"[Pinch] Fingertips {ratio:F2} palm widths apart while the hand turned over; not a pinch");
                ratio = k_CloseRatio;
            }
            var change = m_Rule.Update(true, ratio);
            if (change == PinchRule.Change.Closed)
            {
                m_Pinches++;
                // At the moment of the pinch, as the viewer's button clicks: PinchRouter decides what
                // it does, from opening settings to clicking what the gaze is on.
                Debug.Log($"[Pinch] Pinched ({ratio:F2} palm widths)");
                PinchRouter.Begin(PinchSource.Camera, m_Gaze, false);
            }
            else if (change == PinchRule.Change.Opened)
            {
                PinchRouter.End();
            }
            return true;
        }

        void Lose()
        {
            if (m_Region != null)
                m_Losses++;
            m_Region = null;
            m_Turn.Reset();
            if (m_Rule.Update(false, 0f) == PinchRule.Change.Opened)
                PinchRouter.End();
            m_NextSearch = Time.unscaledTime + m_SearchInterval;
        }

        // Reads each new camera frame into m_NextImage as RGBA with the top row first, as the
        // samplers expect, turned upright on the screen, while the models work on m_Image. At most
        // one read back from the GPU at a time; a newer frame replaces one not yet taken.
        void Update()
        {
            if (!m_Running || m_Camera == null)
                return;
            // Stopped by OnDisable and enabled again before the loop noticed, or silent.
            if (!m_Camera.isPlaying || Time.unscaledTime - m_LastFrameTime > k_NoFrameSeconds)
            {
                if (m_Camera.isPlaying)
                {
                    Debug.Log($"[Pinch] No camera frames for {k_NoFrameSeconds:F0} s: opening the camera again");
                    m_Camera.Stop();
                }
                m_Camera.Play();
                m_LastFrameTime = Time.unscaledTime;
                return;
            }
            if (!m_Camera.didUpdateThisFrame || m_Camera.width < 32)
                return;
            m_LastFrameTime = Time.unscaledTime;

            if (!m_CheckedFormat)
            {
                m_CheckedFormat = true;
                var format = m_Camera.graphicsFormat;
                m_AsyncReadback = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels);
                Debug.Log($"[Pinch] Camera texture {format}: " +
                          (m_AsyncReadback ? "read back without waiting" : "can't be read back, so read with GetPixels32"));
            }

            var stopwatch = Stopwatch.StartNew();
            if (m_AsyncReadback)
            {
                if (m_ReadbackInFlight)
                    return;
                m_ReadbackInFlight = true;
                AsyncGPUReadback.Request(m_Camera, 0, TextureFormat.RGBA32, OnReadback);
            }
            else
            {
                var width = m_Camera.width;
                var height = m_Camera.height;
                if (m_Pixels == null || m_Pixels.Length != width * height)
                    m_Pixels = new Color32[width * height];
                m_Camera.GetPixels32(m_Pixels);
                using (var pixels = new NativeArray<Color32>(m_Pixels, Allocator.Temp))
                    TakeFrame(pixels, width, height, "read with GetPixels32");
            }
            m_ReadMs += stopwatch.Elapsed.TotalMilliseconds;
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            m_ReadbackInFlight = false;
            if (!m_Running || m_Camera == null)
                return;
            if (request.hasError)
            {
                Debug.LogWarning("[Pinch] The GPU couldn't read the camera frame back; reading it the slower way from now on");
                m_AsyncReadback = false;
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            TakeFrame(request.GetData<Color32>(), request.width, request.height, "read back without waiting");
            m_ReadMs += stopwatch.Elapsed.TotalMilliseconds;
        }

        void TakeFrame(NativeArray<Color32> source, int width, int height, string how)
        {
            if (!m_NextImage.IsCreated || m_NextImage.Length != width * height * 4)
            {
                if (m_NextImage.IsCreated)
                    m_NextImage.Dispose();
                m_NextImage = new NativeArray<byte>(width * height * 4, Allocator.Persistent);
            }
            CopyRows(source, m_NextImage, width, height);
            m_NextWidth = width;
            m_NextHeight = height;
            m_HasNextImage = true;
            m_Reads++;
            LogCamera(width, height, how);
        }

        // Color32 is laid out as RGBA bytes, which is what the samplers read, so rows copy whole.
        // Upside down on the screen when the camera sensor's rotation is 180.
        void CopyRows(NativeArray<Color32> source, NativeArray<byte> target, int width, int height)
        {
            // Both readbacks give the bottom row first, so the rows are always turned over, whatever
            // videoVerticallyMirrored says: on the iPhone it says true, and trusting it left the image
            // mirrored top to bottom. That went unnoticed at first because a mirrored hand also reads as
            // the other hand, which cancelled out in the palm test; it showed as the settings button
            // moving down when the hand moved up (SettingsSummoner).
            var turned = Mathf.RoundToInt(m_Camera.videoRotationAngle) % 360 == 180;
            var flipRows = !turned;
            var pixels = target.Reinterpret<Color32>(1);
            for (var y = 0; y < height; y++)
            {
                var row = (flipRows ? height - 1 - y : y) * width;
                NativeArray<Color32>.Copy(source, row, pixels, y * width, width);
                if (!turned)
                    continue;
                for (int left = y * width, right = left + width - 1; left < right; left++, right--)
                    (pixels[left], pixels[right]) = (pixels[right], pixels[left]);
            }
        }

        void LogCamera(int width, int height, string how)
        {
            if (m_LoggedCamera)
                return;
            m_LoggedCamera = true;
            Debug.Log($"[Pinch] Camera gives {width}x{height}, turned {m_Camera.videoRotationAngle:F0} deg, " +
                      $"mirrored {m_Camera.videoVerticallyMirrored}; frames {how}");
        }

        static void CopyTo(Tensor<float> tensor, float[] values)
        {
            var count = Mathf.Min(values.Length, tensor.shape.length);
            for (var i = 0; i < count; i++)
                values[i] = tensor[i];
        }

        // What it cost, main thread and models, so it can be judged against a video playing.
        void LogStatus()
        {
            var seconds = Time.unscaledTime - m_StatusStart;
            if (seconds < k_StatusSeconds)
                return;
            Debug.Log($"[Pinch] {m_Reads / seconds:F1} frames/s read ({m_ReadMs / Mathf.Max(1, m_Reads):F1} ms each on the main thread), " +
                      $"{m_Frames / seconds:F1} analyzed, " +
                      $"detector {m_Detections} runs at {m_DetectMs / Mathf.Max(1, m_Detections):F0} ms, " +
                      $"landmarker {m_Landmarks} runs at {m_LandmarkMs / Mathf.Max(1, m_Landmarks):F0} ms (including waits); " +
                      $"hand {(SeesHand ? "in view" : "not in view")}, {m_Losses} lost, {m_Pinches} pinches, " +
                      (float.IsPositiveInfinity(m_MinRatio) ? "no fingertips seen" : $"fingertip gap {m_MinRatio:F2}-{m_MaxRatio:F2} palm widths"));
            m_Reads = m_Frames = m_Detections = m_Landmarks = m_Losses = m_Pinches = 0;
            m_ReadMs = m_DetectMs = m_LandmarkMs = 0;
            m_MinRatio = float.PositiveInfinity;
            m_MaxRatio = 0f;
            m_StatusStart = Time.unscaledTime;
        }
    }

    // When the fingertip gap makes a pinch: closing below PinchSensor.k_CloseRatio palm widths,
    // opening above k_OpenRatio, and only for a hand seen in more than k_SteadyResults results in a row.
    public class PinchRule
    {
        public enum Change
        {
            None,
            Closed,
            Opened,
        }

        int m_Seen;

        public bool IsPinched { get; private set; }

        // One result: whether a hand was seen, and if so its fingertip gap in palm widths.
        public Change Update(bool handSeen, float ratio)
        {
            if (!handSeen)
            {
                m_Seen = 0;
                var was = IsPinched;
                IsPinched = false;
                return was ? Change.Opened : Change.None;
            }
            m_Seen++;
            if (!IsPinched && ratio < PinchSensor.k_CloseRatio && m_Seen > PinchSensor.k_SteadyResults)
            {
                IsPinched = true;
                return Change.Closed;
            }
            if (IsPinched && ratio > PinchSensor.k_OpenRatio)
            {
                IsPinched = false;
                return Change.Opened;
            }
            return Change.None;
        }

        public void Reset()
        {
            m_Seen = 0;
            IsPinched = false;
        }
    }
}
