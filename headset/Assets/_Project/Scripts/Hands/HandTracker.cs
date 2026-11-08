using System;
using System.Collections.Generic;
using System.Diagnostics;
using CardboardXR.Stereo;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Debug = UnityEngine.Debug;

namespace CardboardXR.Hands
{
    // Tracks one hand in ARCore's camera images with MediaPipe's BlazeHand models on the CPU, in
    // 6DoF. As in MediaPipe, the palm detector only runs while no hand is tracked; after that, each
    // image's landmarks give the next image's hand region. Results take a few frames to come back
    // and rendering never waits for them. The 21 joints are placed in 3D (see HandGeometry) with the
    // camera pose ARCore had for their image, and into the world through this XR Origin, which
    // RoomBoundary keeps at HeadPoseFusion's session-to-world pose. Put it on the XR Origin.
    // The joints and the hand's depth are smoothed with One Euro filters, and every frame the joints
    // are moved on by the palm's speed for the time since their image (see PredictJoints), which makes up
    // for most of the camera's and the models' delay and keeps them moving between results. It runs
    // before HandSubsystemFeed and HandInteractorRig, which use them, and XRI's interaction manager
    // (−105), which uses the rig's poses. To keep the phone cool, the models don't run on every
    // image: while no hand is tracked the palm detector runs at most every m_SearchInterval, since
    // most of the time there's no hand to find. While one is, m_TrackInterval can hold the landmarker
    // back too, with prediction filling in between; by default it runs on every image, since at 10 a
    // second tracking felt laggy. Tracking pauses while RoomBoundary scans for surfaces, which
    // leaves the CPU to ARCore's plane finding; the hand isn't needed then. Logs [Hands]. The
    // "handAxes" launch option (1 to 8) overrides how the camera image's axes lie on the screen.
    [DefaultExecutionOrder(-120)]
    public class HandTracker : MonoBehaviour
    {
        const int k_LandmarkerSize = 224;
        // The models' outputs, by name: the detector's boxes and scores, and the landmarker's
        // landmarks in crop pixels, hand presence, handedness and landmarks in meters. Found by name
        // because ONNX and LiteRT (.tflite) imports can list them in different orders; the index is
        // the ONNX order, used if the name isn't there.
        static readonly (string name, int index) k_BoxesOutput = ("Identity", 0);
        static readonly (string name, int index) k_ScoresOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_LandmarksOutput = ("Identity", 0);
        static readonly (string name, int index) k_PresenceOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_HandednessOutput = ("Identity_2", 2);
        static readonly (string name, int index) k_MetricOutput = ("Identity_3", 3);
        const int k_PoseHistory = 30;
        // An image and a pose belong together if their timestamps are this close.
        const long k_PoseMatchNs = 1_000_000;
        const float k_StatusSeconds = 5f;
        // A fit closer than this, in meters, is wrong: the hand would fill the image.
        const float k_MinHandDistance = 0.05f;
        // Consecutive results whose distances differ by more than this ratio are logged.
        const float k_JumpRatio = 1.3f;
        // The palm's size in 3D (see HandGeometry.PalmSize) for a typical adult hand, in meters, and
        // the range the user's can be learned within.
        const float k_TypicalPalmSize = 0.055f;
        const float k_MinPalmSize = 0.04f;
        const float k_MaxPalmSize = 0.075f;
        // How much of each well-fitting shape's palm size goes into the learned size.
        const float k_PalmSizeRate = 0.02f;
        // Measured depth teaches the palm size only from this far out, in meters. Against a ruler,
        // the iPhone 15 Pro's LiDAR read 30 cm at 30 cm but 16.7 cm at 15 cm.
        const float k_MinCalibrationDepth = 0.25f;
        // And only within arm's reach: the first test's first "hand" was 1.1 m away, a false detection.
        const float k_MaxCalibrationDepth = 0.7f;
        // A hand the palm detector first finds farther than this from the camera, in meters, isn't
        // taken: in testing it kept finding "hands" 0.9 to 1.7 m away. Only when
        // first found, though. Distance alone can't tell them from the real hand, which at full
        // stretch, the phone held at the chest, was measured 1.03 m away. What does is how they
        // arrive: a stray appears far away, and the real hand gets there by being followed outward.
        const float k_MaxReach = 0.8f;
        // A depth sensor sees the hand's skin; the fit places its joints, about this far inside.
        const float k_SkinToJoints = 0.01f;
        // Shapes that miss their landmarks by more than this, in pixels, are replaced by the last good
        // one; by less than k_GoodFitError they teach the palm size from the model's own estimates
        // (measured depth has its own, k_CalibrationFitError). The model's shape collapses when the
        // hand is edge-on or pinching.
        const float k_MaxFitError = 25f;
        const float k_GoodFitError = 10f;
        // Measured depth teaches the palm size from fits closer than this, in pixels: looser than
        // k_GoodFitError, which left it untaught. Holding still at 30 cm, one hand fitted at 7-10 px
        // and calibrated in 5 s; the same hold angled differently fitted at 12-13 px, so only 12
        // results qualified and the palm stayed 11% off. Those rougher fits still agreed closely
        // (1.14-1.18x) and implied a palm within 5% of the finer ones'.
        const float k_CalibrationFitError = 15f;
        // Poses up to this far apart can be interpolated between, for an image with no pose of its own.
        const long k_MaxPoseGapNs = 100_000_000;
        // One Euro filter settings: the cutoff in Hz when still, and how much it rises per m/s.
        const float k_JointMinCutoff = 1.5f;
        const float k_JointBeta = 8f;
        const float k_DepthMinCutoff = 1f;
        const float k_DepthBeta = 4f;
        // The joints are moved on by this share of their speed times the time since their image, for
        // up to k_MaxPredictionSeconds; less than all of it, so they don't overshoot when the hand stops.
        const float k_PredictionGain = 0.8f;
        const float k_MaxPredictionSeconds = 0.15f;
        // Results further apart than this, in seconds, don't give a speed.
        const double k_MaxSpeedGapSeconds = 0.2;
        // How much of each new speed goes into the smoothed one.
        const float k_SpeedBlend = 0.5f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] ArPoseSource m_PoseSource;
        [SerializeField] ARCameraManager m_CameraManager;
        [Tooltip("Environment depth, measured at the palm to learn the hand's real size. Only turned on " +
                 "where the depth comes from a sensor.")]
        [SerializeField] AROcclusionManager m_Occlusion;
        [Tooltip("Optional: tracking pauses while it scans for surfaces.")]
        [SerializeField] RoomBoundary m_Room;
        [SerializeField] ModelAsset m_Detector;
        [SerializeField] ModelAsset m_Landmarker;
        [Tooltip("Camera images are scaled down to at most this width for the models.")]
        [SerializeField] int m_MaxImageWidth = 640;
        [Tooltip("While no hand is tracked, the palm detector runs at most this often, in seconds.")]
        [SerializeField] float m_SearchInterval = 0.5f;
        [Tooltip("While a hand is tracked, the landmarker runs at most this often, in seconds; 0 runs it on every image.")]
        [SerializeField] float m_TrackInterval;
        [SerializeField, Range(0f, 1f)] float m_MinPalmScore = 0.5f;
        [SerializeField, Range(0f, 1f)] float m_MinHandPresence = 0.5f;

        public bool IsTracking { get; private set; }

        // The 21 joints' world positions, in MediaPipe's order, while IsTracking.
        public Vector3[] Joints { get; } = new Vector3[HandGeometry.JointCount];

        // The palm's distance ahead of the camera, in meters, while IsTracking.
        public float HandDistance { get; private set; }

        // How far apart the thumb and index fingertips are in the model's hand shape, in meters, while
        // IsTracking. The shape doesn't depend on how far away the hand is thought to be.
        public float PinchDistance { get; private set; }

        // Midway between the thumb and index fingertips, in world space, while IsTracking.
        public Vector3 PinchPoint => (Joints[HandGeometry.ThumbTip] + Joints[HandGeometry.IndexTip]) / 2f;

        // Goes up by one with each result placed.
        public int ResultCount { get; private set; }

        // For the latest result: the thumb and index fingertips' distance in the image over the palm's
        // size there, from the model's 2D landmarks, its most reliable output; its hand presence
        // score; and whether its 3D shape fit (if not, PinchDistance is from the last one that did).
        public float PinchRatio { get; private set; }
        public float Presence { get; private set; }
        public bool ShapeFits { get; private set; }

        // For the latest result: the landmarker's handedness, as the chance that it's a right hand
        // (see HandSubsystemFeed).
        public float HandednessScore { get; private set; }

        // The latest result's 21 landmarks in the camera image, in pixels (x right, y down), for 2D
        // tests such as which way the palm faces (PalmGesture).
        public System.Collections.Generic.IReadOnlyList<Vector2> ImageLandmarks => m_ImageLandmarks;

        // How near the hand comes to the camera image's edge, as the smallest of its landmarks' distances
        // to an edge over the image's size there: 0 touching it, 0.5 in the middle.
        public float EdgeDistance
        {
            get
            {
                if (m_ImageWidth <= 0 || m_ImageHeight <= 0)
                    return 0.5f;
                var nearest = 0.5f;
                foreach (var point in m_ImageLandmarks)
                {
                    var x = point.x / m_ImageWidth;
                    var y = point.y / m_ImageHeight;
                    nearest = Mathf.Min(nearest, Mathf.Min(Mathf.Min(x, 1f - x), Mathf.Min(y, 1f - y)));
                }
                return nearest;
            }
        }

        Worker m_DetectorWorker;
        Worker m_LandmarkerWorker;
        // The outputs' indices in the loaded models.
        int m_BoxesIndex;
        int m_ScoresIndex;
        int m_LandmarksIndex;
        int m_PresenceIndex;
        int m_HandednessIndex;
        int m_MetricIndex;
        Tensor<float> m_DetectorInput;
        Tensor<float> m_LandmarkerInput;
        float[] m_DetectorPixels;
        float[] m_LandmarkerPixels;
        float[] m_Boxes;
        float[] m_Scores;
        readonly float[] m_LandmarkValues = new float[HandGeometry.JointCount * 3];
        readonly float[] m_MetricValues = new float[HandGeometry.JointCount * 3];
        Vector2[] m_Anchors;

        NativeArray<byte> m_Image;
        int m_ImageWidth;
        int m_ImageHeight;
        long m_LastImageNs;
        Vector2 m_FocalLength;
        Vector2 m_PrincipalPoint;
        HandRegion? m_Region;
        bool m_Running;
        bool m_LoopActive;
        bool m_WasPaused;
        // When the palm detector may next look for a hand, and the landmarker next run.
        float m_NextSearch;
        float m_NextTrack;

        // Whether tracking is paused while the room is scanned, leaving the CPU to plane detection
        // (RoomBoundary.PausesHands).
        public bool IsPaused => m_Room != null && m_Room.PausesHands;

        // Recent ARCore camera poses, to place each result with its own image's pose.
        readonly List<ArPoseSource.Sample> m_Poses = new();
        // Screen directions of the camera image's x and y axes.
        Vector2 m_ImageXOnScreen;
        Vector2 m_ImageYOnScreen;
        bool m_HasAxes;
        int m_AxesVariant;
        Matrix4x4 m_DisplayMatrix;
        bool m_LoggedSetup;

        readonly Vector2[] m_ImageLandmarks = new Vector2[HandGeometry.JointCount];
        readonly Vector3[] m_MetricLandmarks = new Vector3[HandGeometry.JointCount];
        // The user's palm size in 3D, in meters, learned from shapes that fit well.
        float m_PalmSize = k_TypicalPalmSize;
        readonly OneEuroFilter[] m_JointFilters = CreateJointFilters();
        // The latest result's smoothed joints, their image's time, and the hand's smoothed speed.
        readonly Vector3[] m_ResultJoints = new Vector3[HandGeometry.JointCount];
        // The palm's, which every joint is moved on by. Each joint once had its own, and a pinch's
        // fingertips, hiding each other, jump in the image: their speeds doubled the jitter the eye
        // saw while pinching on the iPhone, 25-38 mm a step against 13-17 held. The palm is what the
        // fit locks on to, and a hand dragging something moves mostly as one piece.
        Vector3 m_HandSpeed;
        long m_ResultImageNs;
        // Tells how old a camera image is. The camera shares the native sensors' clock on Android
        // and stamps on the system clock on iOS, where there are no native sensors; this works out
        // which from the first result rather than assuming a platform.
        readonly CameraClock m_CameraClock = new("[Hands]", "the native sensors' clock");
        // Measured depth at the palm, which teaches the palm size that scales the hand's estimated
        // depth, joints and pinch (see MeasureDepth).
        readonly DepthSampler m_DepthSampler = new();
        bool m_UseDepth;
        bool m_LoggedDepth;
        // Measured over estimated palm depth, split at 20 cm, inside which the LiDAR reads long.
        readonly List<float> m_NearDepthRatios = new();
        readonly List<float> m_FarDepthRatios = new();
        int m_DepthNoPixels;
        int m_DepthCalibrations;
        int m_ImplausibleDepths;
        // Whether measured depth has taken over teaching the palm size.
        bool m_PalmFromDepth;
        // Results dropped as too far away to be the user's hand, and whether the last one was, so
        // each run of them is logged once.
        int m_OutOfReach;
        bool m_DroppingStray;
        // Whether the region comes from the palm detector and no result from it has been taken yet:
        // a new hand, which must be within reach, rather than one being followed.
        bool m_Acquiring;
        // The fingertip's steps as each result arrives, shown and without prediction (see MeasureJump).
        double m_ShownDepthJumps, m_ShownAcrossJumps, m_HeldDepthJumps, m_HeldAcrossJumps, m_ShownPalmJumps, m_HeldPalmJumps;
        int m_Jumps;
        // The last shape that fit its landmarks, and its smoothed depth, for images whose shape
        // collapsed.
        readonly Vector3[] m_GoodShape = new Vector3[HandGeometry.JointCount];
        bool m_HasGoodShape;
        float m_GoodDepth;
        // Smooths the hand's depth before the joints are placed with it.
        readonly OneEuroFilter m_DepthFilter = new(k_DepthMinCutoff, k_DepthBeta);
        readonly Vector3[] m_CameraJoints = new Vector3[HandGeometry.JointCount];

        // Statistics for the [Hands] status line.
        int m_Images;
        int m_Results;
        int m_Detections;
        int m_Losses;
        int m_WithoutPose;
        double m_SampleMs;
        double m_DetectMs;
        double m_LandmarkMs;
        float m_PresenceSum;
        float m_Handedness;
        float m_NearestHand = float.PositiveInfinity;
        float m_FarthestHand;
        int m_Fits;
        int m_BadFits;
        int m_RejectedFits;
        float m_ClosestPinch = float.PositiveInfinity;
        float m_WidestPinch;
        // How old results are when they arrive, and how far ahead they're predicted, in seconds.
        double m_ResultAgeSum;
        int m_ResultAges;
        double m_LeadSum;
        int m_Leads;
        float m_FitErrorSum;
        int m_InterpolatedPoses;
        float m_StatusStart;
        // The previous result, for logging jumps in distance.
        float m_LastDistance;
        float m_LastPalmSpan;
        float m_LastResultTime = float.NegativeInfinity;
        float m_LastJumpLog = float.NegativeInfinity;
        float m_LastLossLog = float.NegativeInfinity;

        void OnEnable()
        {
            m_AxesVariant = LaunchOptions.GetInt("handAxes", 0);
            // The models are the largest part of the app's heat, so their rate is a launch option:
            // "handHz 10" runs the landmarker at most ten times a second, 0 on every image.
            var handHz = LaunchOptions.GetInt("handHz", 0);
            if (handHz > 0)
                m_TrackInterval = 1f / handHz;
            m_CameraManager.frameReceived += OnCameraFrame;
            // Only on iOS for now, where environment depth comes from the Pro models' LiDAR. ARCore
            // offers it through the same API, but a Pixel 9 Pro has no depth sensor, so its depth is
            // inferred from motion, which assumes a still scene and can't follow a moving hand, and
            // costs heat the hand models already strain.
            m_UseDepth = m_Occlusion != null && Application.platform == RuntimePlatform.IPhonePlayer;
            if (m_UseDepth)
                m_Occlusion.enabled = true;
            m_StatusStart = Time.unscaledTime;
            m_Running = true;
            if (!m_LoopActive)
                RunLoop();
        }

        void OnDisable()
        {
            m_Running = false;
            m_CameraManager.frameReceived -= OnCameraFrame;
            if (m_Occlusion != null)
                m_Occlusion.enabled = false;
            Lose();
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (m_HasAxes || !args.displayMatrix.HasValue)
                return;
            m_DisplayMatrix = args.displayMatrix.Value;
            if (m_AxesVariant is >= 1 and <= 8)
                HandGeometry.ImageAxesVariant(m_AxesVariant, out m_ImageXOnScreen, out m_ImageYOnScreen);
            else
                HandGeometry.ImageAxesOnScreen(m_DisplayMatrix, out m_ImageXOnScreen, out m_ImageYOnScreen);
            m_HasAxes = true;
        }

        void Update()
        {
            if (m_PoseSource.isActiveAndEnabled && m_PoseSource.TryGetLatest(out var pose) && pose.timestampNs != 0 &&
                (m_Poses.Count == 0 || m_Poses[^1].timestampNs != pose.timestampNs))
            {
                m_Poses.Add(pose);
                if (m_Poses.Count > k_PoseHistory)
                    m_Poses.RemoveAt(0);
            }
            var paused = IsPaused;
            if (paused != m_WasPaused)
            {
                m_WasPaused = paused;
                Debug.Log(paused ? "[Hands] Paused while the room is scanned" : "[Hands] Resumed after the scan");
            }
            PredictJoints();
            LogStatus();
        }

        // Moves the latest result's joints on by the hand's speed for the time since their image, so
        // they keep up with the hand between results and make up for most of the delay. Only where the
        // image timestamps' clock can be read.
        void PredictJoints()
        {
            if (!IsTracking)
                return;
            var lead = 0f;
            if (TryGetImageAge(m_ResultImageNs, out var sinceImage))
            {
                lead = Mathf.Clamp((float)sinceImage, 0f, k_MaxPredictionSeconds) * k_PredictionGain;
                m_LeadSum += lead;
                m_Leads++;
            }
            for (var i = 0; i < HandGeometry.JointCount; i++)
                Joints[i] = m_ResultJoints[i] + m_HandSpeed * lead;
        }

        static Vector3 PalmCentre(Vector3[] joints)
        {
            var sum = Vector3.zero;
            foreach (var id in HandGeometry.Palm)
                sum += joints[id];
            return sum / HandGeometry.Palm.Length;
        }

        // How far the index fingertip jumps as each result replaces the last: as shown, with
        // prediction, and as it would with the last result simply held. Each result is a step the eye
        // sees, 20-30 times a second, so these are the jitter. Split along the line from the eyes,
        // where the depth and palm size move it, and across it, where the landmarks do. If prediction
        // makes the steps larger it's adding jitter; if they're alike, the tracking is the source.
        // The palm's middle too, which a ray aimed from the knuckles would follow instead: while
        // pinching, the fingertips hide each other and jump, and the palm doesn't.
        void MeasureJump(Vector3 shownBefore, Vector3 shownAfter, Vector3 heldBefore, Vector3 heldAfter,
            Vector3 shownPalmBefore, Vector3 shownPalmAfter, Vector3 heldPalmBefore, Vector3 heldPalmAfter)
        {
            var along = (heldAfter - m_Fusion.transform.position).normalized;
            Accumulate(shownAfter - shownBefore, along, ref m_ShownDepthJumps, ref m_ShownAcrossJumps);
            Accumulate(heldAfter - heldBefore, along, ref m_HeldDepthJumps, ref m_HeldAcrossJumps);
            m_ShownPalmJumps += (shownPalmAfter - shownPalmBefore).sqrMagnitude;
            m_HeldPalmJumps += (heldPalmAfter - heldPalmBefore).sqrMagnitude;
            m_Jumps++;

            static void Accumulate(Vector3 jump, Vector3 along, ref double depthSquares, ref double acrossSquares)
            {
                var depth = Vector3.Dot(jump, along);
                depthSquares += depth * depth;
                acrossSquares += (jump - along * depth).sqrMagnitude;
            }
        }

        string JumpSummary()
        {
            if (m_Jumps == 0)
                return "";
            string Rms(double depthSquares, double acrossSquares) =>
                $"{Math.Sqrt((depthSquares + acrossSquares) / m_Jumps) * 1000:F1} mm " +
                $"({Math.Sqrt(depthSquares / m_Jumps) * 1000:F1} deep, {Math.Sqrt(acrossSquares / m_Jumps) * 1000:F1} across)";
            return $"; fingertip steps between results, rms over {m_Jumps}: shown {Rms(m_ShownDepthJumps, m_ShownAcrossJumps)}, " +
                   $"without prediction {Rms(m_HeldDepthJumps, m_HeldAcrossJumps)}; palm steps shown " +
                   $"{Math.Sqrt(m_ShownPalmJumps / m_Jumps) * 1000:F1} mm, without prediction {Math.Sqrt(m_HeldPalmJumps / m_Jumps) * 1000:F1} mm";
        }

        void TakeDepth(long imageNs)
        {
            if (!m_DepthSampler.TryTake(m_Occlusion, imageNs) || m_LoggedDepth)
                return;
            m_LoggedDepth = true;
            Debug.Log($"[Hands] Environment depth {m_DepthSampler.Width}x{m_DepthSampler.Height}, " +
                      $"{(m_DepthSampler.HasConfidence ? "with" : "without")} confidence, " +
                      $"mode {m_Occlusion.currentEnvironmentDepthMode}");
        }

        // Compares the depth measured at the palm's middle with the estimate from palm size, and from
        // far enough out, where the sensor is accurate, learns the palm size that makes them agree.
        // The palm size then scales the hand at every distance, closer ones included, and its pinch.
        void MeasureDepth(float estimate, float measured, bool fitsForCalibration)
        {
            var joints = measured + k_SkinToJoints;
            (estimate < 0.2f ? m_NearDepthRatios : m_FarDepthRatios).Add(joints / estimate);
            if (!fitsForCalibration || measured < k_MinCalibrationDepth || measured > k_MaxCalibrationDepth)
                return;
            var target = HandGeometry.PalmSizeForDepth(m_PalmSize, estimate, joints);
            // A measurement implying a palm no adult has was of something else.
            if (target < k_MinPalmSize || target > k_MaxPalmSize)
            {
                m_ImplausibleDepths++;
                return;
            }
            var learned = Mathf.Exp(Mathf.Lerp(Mathf.Log(m_PalmSize), Mathf.Log(target), k_PalmSizeRate));
            m_PalmSize = Mathf.Clamp(learned, k_MinPalmSize, k_MaxPalmSize);
            m_DepthCalibrations++;
            if (m_PalmFromDepth)
                return;
            m_PalmFromDepth = true;
            Debug.Log($"[Hands] Palm size now learned from measured depth, starting from {m_PalmSize * 100f:F1} cm " +
                      $"(hand {measured * 100f:F0} cm away, estimated at {estimate * 100f:F0})");
        }

        // The depth sensor's reading at the palm's middle, in meters, for the image just taken.
        bool TryMeasurePalm(out float measured)
        {
            var palm = Vector2.zero;
            foreach (var joint in HandGeometry.Palm)
                palm += m_ImageLandmarks[joint];
            palm /= HandGeometry.Palm.Length;
            var uv = new Vector2(palm.x / m_ImageWidth, palm.y / m_ImageHeight);
            // Two depth pixels either side stays inside a palm at arm's length, and medium confidence
            // or better leaves out the pixels ARKit itself doubts.
            if (m_DepthSampler.TrySample(uv, 2, 1, out measured, out _))
                return true;
            measured = float.NaN;
            return false;
        }

        // Whether a hand this far away could be the user's: by the depth sensor's measurement where
        // there is one, which is accurate this far out, and by the estimate from palm size otherwise.
        // measured is NaN where there's none.
        public static bool WithinReach(float estimate, float measured) =>
            (float.IsNaN(measured) ? estimate : measured) <= k_MaxReach;

        // Drops a newly found hand too far away to be the user's, and forgets its region, so the palm
        // detector searches the whole image again rather than following the stray.
        void DropOutOfReach(float estimate, float measured)
        {
            m_OutOfReach++;
            if (!m_DroppingStray)
            {
                m_DroppingStray = true;
                Debug.Log(float.IsNaN(measured)
                    ? $"[Hands] Ignored a new hand estimated {estimate:F2} m away, beyond reach; searching again"
                    : $"[Hands] Ignored a new hand measured {measured:F2} m away (estimated {estimate:F2}), beyond reach; searching again");
            }
            if (IsTracking)
                Lose();
            else
                Forget();
        }

        string DepthSummary()
        {
            if (!m_UseDepth)
                return "";
            return $"; measured joint depth over estimate: under 20 cm {RatioSummary(m_NearDepthRatios)}, " +
                   $"20 cm and over {RatioSummary(m_FarDepthRatios)}; palm size taught by {m_DepthCalibrations} " +
                   $"measurements ({m_ImplausibleDepths} implying no real palm left out); {m_DepthNoPixels} palms " +
                   $"without a confident depth, {m_DepthSampler.NoImage} images " +
                   $"without depth, {m_DepthSampler.Unmatched} unmatched";
        }

        static string RatioSummary(List<float> ratios)
        {
            if (ratios.Count == 0)
                return "none";
            ratios.Sort();
            float At(float share) => ratios[Mathf.Clamp(Mathf.RoundToInt(share * (ratios.Count - 1)), 0, ratios.Count - 1)];
            return $"{ratios.Count} palms, median {At(0.5f):F2}x (middle half {At(0.25f):F2}-{At(0.75f):F2}x)";
        }

        // How old a camera image is now, in seconds, on the clock it was stamped with. False until
        // that clock is known. NativeImu.NowNs is 0 where there are no native sensors, which
        // CameraClock rejects, so on iOS it settles on the system clock.
        bool TryGetImageAge(long imageNs, out double seconds)
        {
            seconds = 0;
            if (!m_CameraClock.TryToTrackerClock(imageNs, NativeImu.NowNs(), CameraClock.SystemNowNs(), out _))
                return false;
            seconds = m_CameraClock.ArrivalAgeNs * 1e-9;
            return true;
        }

        async void RunLoop()
        {
            m_LoopActive = true;
            try
            {
                Setup();
                while (m_Running)
                {
                    if (!await TrackImage())
                        await Awaitable.NextFrameAsync();
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
            }
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
            m_MetricIndex = OutputIndex(landmarker, k_MetricOutput);
            Debug.Log($"[Hands] Detector {m_Detector.name}: {Describe(detector)}; landmarker {m_Landmarker.name}: {Describe(landmarker)}; " +
                      $"landmarker at most {(m_TrackInterval > 0f ? $"{1f / m_TrackInterval:F0}/s" : "every image")}, " +
                      $"detector at most {1f / m_SearchInterval:F0}/s while no hand");
            m_DetectorWorker = new Worker(detector, BackendType.CPU);
            m_LandmarkerWorker = new Worker(landmarker, BackendType.CPU);
            m_DetectorInput = new Tensor<float>(new TensorShape(1, PalmDecoder.InputSize, PalmDecoder.InputSize, 3));
            m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerSize, k_LandmarkerSize, 3));
            m_DetectorPixels = new float[PalmDecoder.InputSize * PalmDecoder.InputSize * 3];
            m_LandmarkerPixels = new float[k_LandmarkerSize * k_LandmarkerSize * 3];
            m_Boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            m_Scores = new float[PalmDecoder.AnchorCount];
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

        static string Describe(Model model)
        {
            var text = new System.Text.StringBuilder("in");
            foreach (var input in model.inputs)
                text.Append($" {input.name} {input.shape}");
            text.Append(", out");
            foreach (var output in model.outputs)
                text.Append($" {output.name}");
            return text.ToString();
        }

        void Teardown()
        {
            m_DetectorWorker?.Dispose();
            m_LandmarkerWorker?.Dispose();
            m_DetectorInput?.Dispose();
            m_LandmarkerInput?.Dispose();
            if (m_Image.IsCreated)
                m_Image.Dispose();
        }

        // Runs the newest camera image through the models. False if there was no new image to run.
        async Awaitable<bool> TrackImage()
        {
            if (m_Fusion.CurrentMode != HeadPoseFusion.Mode.SixDof || !m_CameraManager.enabled || IsPaused)
            {
                Lose();
                return false;
            }
            if (Time.unscaledTime < (m_Region == null ? m_NextSearch : m_NextTrack))
                return false;
            if (!TryTakeImage(out var imageNs))
                return false;

            m_Images++;
            var stopwatch = Stopwatch.StartNew();
            if (m_Region == null)
            {
                var letterbox = Letterbox.Fit(m_ImageWidth, m_ImageHeight, PalmDecoder.InputSize);
                ImageSampler.SampleLetterbox(m_Image, m_ImageWidth, m_ImageHeight, letterbox, m_DetectorPixels);
                m_SampleMs += stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Restart();
                m_DetectorInput.Upload(m_DetectorPixels);
                m_DetectorWorker.Schedule(m_DetectorInput);
                // Once the first output is back the rest are ready.
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
                    Lose();
                    return true;
                }
                m_Region = HandRegion.FromPalm(palm, letterbox);
                m_Acquiring = true;
            }

            var region = m_Region.Value;
            m_NextTrack = Time.unscaledTime + m_TrackInterval;
            stopwatch.Restart();
            ImageSampler.SampleRegion(m_Image, m_ImageWidth, m_ImageHeight, region, k_LandmarkerSize, m_LandmarkerPixels);
            m_SampleMs += stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            m_LandmarkerInput.Upload(m_LandmarkerPixels);
            m_LandmarkerWorker.Schedule(m_LandmarkerInput);
            using (var landmarks = await (m_LandmarkerWorker.PeekOutput(m_LandmarksIndex) as Tensor<float>).ReadbackAndCloneAsync())
                CopyTo(landmarks, m_LandmarkValues);
            float presence, handedness;
            using (var output = (m_LandmarkerWorker.PeekOutput(m_PresenceIndex) as Tensor<float>).ReadbackAndClone())
                presence = output[0];
            using (var output = (m_LandmarkerWorker.PeekOutput(m_HandednessIndex) as Tensor<float>).ReadbackAndClone())
                handedness = output[0];
            using (var metric = (m_LandmarkerWorker.PeekOutput(m_MetricIndex) as Tensor<float>).ReadbackAndClone())
                CopyTo(metric, m_MetricValues);
            m_LandmarkMs += stopwatch.Elapsed.TotalMilliseconds;
            if (!m_Running)
                return true;

            // The model may give a probability or a logit.
            if (presence < 0f || presence > 1f)
                presence = PalmDecoder.Sigmoid(presence);
            m_PresenceSum += presence;
            m_Handedness = handedness;
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
            PinchRatio = Vector2.Distance(m_ImageLandmarks[HandGeometry.ThumbTip], m_ImageLandmarks[HandGeometry.IndexTip]) /
                         Mathf.Max(1f, HandGeometry.PalmSpan(m_ImageLandmarks));
            Presence = presence;
            HandednessScore = handedness < 0f || handedness > 1f ? PalmDecoder.Sigmoid(handedness) : handedness;

            // The hand's real size doesn't change, but the model's estimate of it wavers from image to
            // image, so each shape is scaled to the user's palm size.
            ShapeFromModel(region.rotation, m_MetricLandmarks);
            var palmSize = HandGeometry.PalmSize(m_MetricLandmarks);
            if (!(palmSize > 0f))
            {
                m_BadFits++;
                IsTracking = false;
                return true;
            }
            var scale = m_PalmSize / palmSize;
            for (var i = 0; i < HandGeometry.JointCount; i++)
                m_MetricLandmarks[i] *= scale;

            // The palm places the hand: the model's fingers bend and are less sure.
            var position = HandGeometry.FitPosition(m_ImageLandmarks, m_MetricLandmarks, HandGeometry.Palm,
                m_FocalLength, m_PrincipalPoint, out var fitError);
            float depth;
            ShapeFits = fitError <= k_MaxFitError;
            if (ShapeFits)
            {
                // Once measured depth has taught the palm size, it alone does: against LiDAR, the size
                // learned from the model's own estimates wandered 10% in 20 s, and left depth 8% short
                // at 30 cm against a ruler. Until then this still runs, since a hand kept too close
                // to be measured would otherwise be stuck at the typical size.
                if (fitError < k_GoodFitError && !m_PalmFromDepth)
                {
                    var learned = Mathf.Exp(Mathf.Lerp(Mathf.Log(m_PalmSize), Mathf.Log(palmSize), k_PalmSizeRate));
                    m_PalmSize = Mathf.Clamp(learned, k_MinPalmSize, k_MaxPalmSize);
                }
                PinchDistance = Vector3.Distance(m_MetricLandmarks[HandGeometry.ThumbTip], m_MetricLandmarks[HandGeometry.IndexTip]);
                m_ClosestPinch = Mathf.Min(m_ClosestPinch, PinchDistance);
                m_WidestPinch = Mathf.Max(m_WidestPinch, PinchDistance);
                depth = m_DepthFilter.Filter(new Vector3(position.z, 0f, 0f), imageNs * 1e-9).x;
                Array.Copy(m_MetricLandmarks, m_GoodShape, HandGeometry.JointCount);
                m_GoodDepth = depth;
                m_HasGoodShape = true;
            }
            else if (m_HasGoodShape)
            {
                // A collapsed shape. The landmarks in the image still hold, so the joints follow them
                // at the last good shape's depths, and the pinch keeps its last state.
                m_RejectedFits++;
                Array.Copy(m_GoodShape, m_MetricLandmarks, HandGeometry.JointCount);
                depth = m_GoodDepth;
            }
            else
            {
                m_RejectedFits++;
                return true;
            }
            HandGeometry.PlaceJoints(m_ImageLandmarks, m_MetricLandmarks, depth, m_FocalLength, m_PrincipalPoint, m_CameraJoints);
            var distance = HandGeometry.PalmDepth(m_CameraJoints);
            if (float.IsNaN(distance) || distance < k_MinHandDistance)
            {
                m_BadFits++;
                IsTracking = false;
                return true;
            }
            var measured = float.NaN;
            if (m_UseDepth && m_DepthSampler.TimestampNs == imageNs && !TryMeasurePalm(out measured))
                m_DepthNoPixels++;
            if (m_Acquiring && !WithinReach(distance, measured))
            {
                DropOutOfReach(distance, measured);
                return true;
            }
            m_Acquiring = false;
            m_DroppingStray = false;
            m_Fits++;
            m_FitErrorSum += fitError;
            // Only fresh estimates: a collapsed shape's depth is carried over from an earlier image.
            if (ShapeFits && !float.IsNaN(measured))
                MeasureDepth(distance, measured, fitError < k_CalibrationFitError);
            m_NearestHand = Mathf.Min(m_NearestHand, distance);
            m_FarthestHand = Mathf.Max(m_FarthestHand, distance);
            LogDistanceJump(distance, position.z, HandGeometry.PalmSpan(m_ImageLandmarks), fitError, presence, region.size, palmSize);

            if (!m_HasAxes || !TryFindPose(imageNs, out var pose))
            {
                m_WithoutPose++;
                IsTracking = false;
                return true;
            }

            // A speed needs the previous result to be of this hand and recent.
            var gap = (imageNs - m_ResultImageNs) * 1e-9;
            var hasSpeed = IsTracking && gap > 0 && gap <= k_MaxSpeedGapSeconds;
            var shownTip = Joints[HandGeometry.IndexTip];
            var heldTip = m_ResultJoints[HandGeometry.IndexTip];
            var shownPalm = PalmCentre(Joints);
            var heldPalm = PalmCentre(m_ResultJoints);
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var joint = m_CameraJoints[i];
                // The AR camera's local space has x right, y up and z forward on the screen.
                var across = m_ImageXOnScreen * joint.x + m_ImageYOnScreen * joint.y;
                var session = pose.position + pose.rotation * new Vector3(across.x, across.y, joint.z);
                m_ResultJoints[i] = m_JointFilters[i].Filter(transform.TransformPoint(session), imageNs * 1e-9);
            }
            m_HandSpeed = hasSpeed
                ? Vector3.Lerp(m_HandSpeed, (PalmCentre(m_ResultJoints) - heldPalm) / (float)gap, k_SpeedBlend)
                : Vector3.zero;
            m_ResultImageNs = imageNs;
            if (TryGetImageAge(imageNs, out var resultAge))
            {
                m_ResultAgeSum += resultAge;
                m_ResultAges++;
            }
            HandDistance = distance;
            IsTracking = true;
            PredictJoints();
            if (hasSpeed)
                MeasureJump(shownTip, Joints[HandGeometry.IndexTip], heldTip, m_ResultJoints[HandGeometry.IndexTip],
                    shownPalm, PalmCentre(Joints), heldPalm, PalmCentre(m_ResultJoints));
            m_Results++;
            ResultCount++;
            return true;
        }

        // Converts the newest camera image to RGBA, scaled down, with its intrinsics to match.
        bool TryTakeImage(out long timestampNs)
        {
            timestampNs = 0;
            if (!m_CameraManager.TryAcquireLatestCpuImage(out var image))
                return false;

            using (image)
            {
                timestampNs = (long)Math.Round(image.timestamp * 1e9);
                if (timestampNs == m_LastImageNs || !m_CameraManager.TryGetIntrinsics(out var intrinsics))
                    return false;
                m_LastImageNs = timestampNs;
                if (m_UseDepth)
                    TakeDepth(timestampNs);

                var divisor = Mathf.Max(1, Mathf.CeilToInt(image.width / (float)m_MaxImageWidth));
                var size = new Vector2Int(image.width / divisor, image.height / divisor);
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, image.width, image.height),
                    outputDimensions = size,
                    outputFormat = TextureFormat.RGBA32,
                    // CPU images start at the top row, which the samplers expect.
                    transformation = XRCpuImage.Transformation.None,
                };
                var length = size.x * size.y * 4;
                if (!m_Image.IsCreated || m_Image.Length != length)
                {
                    if (m_Image.IsCreated)
                        m_Image.Dispose();
                    m_Image = new NativeArray<byte>(length, Allocator.Persistent);
                }
                image.Convert(conversion, m_Image);
                m_ImageWidth = size.x;
                m_ImageHeight = size.y;

                var scale = new Vector2((float)size.x / intrinsics.resolution.x, (float)size.y / intrinsics.resolution.y);
                m_FocalLength = Vector2.Scale(intrinsics.focalLength, scale);
                m_PrincipalPoint = Vector2.Scale(intrinsics.principalPoint, scale);
                if (!m_LoggedSetup && m_HasAxes)
                {
                    m_LoggedSetup = true;
                    var d = m_DisplayMatrix;
                    Debug.Log($"[Hands] Camera image {image.width}x{image.height} -> {size.x}x{size.y}, intrinsics for " +
                              $"{intrinsics.resolution.x}x{intrinsics.resolution.y}: focal {intrinsics.focalLength:F1}, " +
                              $"principal {intrinsics.principalPoint:F1}; display matrix u ({d.m00:F2}, {d.m01:F2}) v ({d.m10:F2}, {d.m11:F2}) " +
                              $"offset ({d.m20:F2}, {d.m21:F2}); image x on screen {m_ImageXOnScreen}, y {m_ImageYOnScreen}" +
                              (m_AxesVariant is >= 1 and <= 8 ? $" (handAxes {m_AxesVariant})" : ""));
                }
            }
            return true;
        }

        bool TryFindPose(long timestampNs, out ArPoseSource.Sample pose)
        {
            for (var i = m_Poses.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(m_Poses[i].timestampNs - timestampNs) <= k_PoseMatchNs)
                {
                    pose = m_Poses[i];
                    return true;
                }
            }

            // ArPoseSource misses some images' poses; interpolate between the ones on either side.
            for (var i = m_Poses.Count - 1; i > 0; i--)
            {
                var before = m_Poses[i - 1];
                var after = m_Poses[i];
                if (before.timestampNs < timestampNs && timestampNs < after.timestampNs &&
                    after.timestampNs - before.timestampNs <= k_MaxPoseGapNs)
                {
                    var t = (float)((double)(timestampNs - before.timestampNs) / (after.timestampNs - before.timestampNs));
                    pose = new ArPoseSource.Sample
                    {
                        position = Vector3.Lerp(before.position, after.position, t),
                        rotation = Quaternion.Slerp(before.rotation, after.rotation, t),
                        timestampNs = timestampNs,
                    };
                    m_InterpolatedPoses++;
                    return true;
                }
            }
            pose = default;
            return false;
        }

        // The model's hand shape in the camera image's axes, in meters: x right, y down, z away. It
        // comes upright like the crop, so it's turned back by the region's rotation. (Of the eight
        // ways to read its turn and y and z signs, this one fit the landmarks best on the device.)
        void ShapeFromModel(float rotation, Vector3[] shape)
        {
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var across = HandRegion.Rotate(new Vector2(m_MetricValues[i * 3], m_MetricValues[i * 3 + 1]), rotation);
                shape[i] = new Vector3(across.x, across.y, m_MetricValues[i * 3 + 2]);
            }
        }

        void Lose()
        {
            if (IsTracking || m_Region != null)
            {
                m_Losses++;
                LogLoss();
            }
            Forget();
        }

        // Lets go of the hand and everything learned from its last results, without counting a loss.
        void Forget()
        {
            IsTracking = false;
            m_Region = null;
            foreach (var filter in m_JointFilters)
                filter.Reset();
            m_DepthFilter.Reset();
            m_HandSpeed = Vector3.zero;
            m_HasGoodShape = false;
        }

        // Where the hand was when it went. One that left the camera's view goes at the frame's edge,
        // which tells that apart from a hand lost to a poor reading while in plain view: the camera
        // only covers 68 by 42 degrees, so anything meant to be reached for has to sit inside that.
        // At most one line a second.
        void LogLoss()
        {
            if (m_Region == null || m_ImageWidth <= 0 || m_ImageHeight <= 0 || Time.unscaledTime - m_LastLossLog < 1f)
                return;
            m_LastLossLog = Time.unscaledTime;
            var center = m_Region.Value.center;
            var across = center.x / m_ImageWidth;
            var down = center.y / m_ImageHeight;
            var toEdge = Mathf.Min(Mathf.Min(across, 1f - across), Mathf.Min(down, 1f - down));
            Debug.Log($"[Hands] Lost the hand {across * 100f:F0}% across and {down * 100f:F0}% down the image " +
                      $"({toEdge * 100f:F0}% from the nearest edge), last {m_LastDistance:F2} m away");
        }

        static OneEuroFilter[] CreateJointFilters()
        {
            var filters = new OneEuroFilter[HandGeometry.JointCount];
            for (var i = 0; i < filters.Length; i++)
                filters[i] = new OneEuroFilter(k_JointMinCutoff, k_JointBeta);
            return filters;
        }

        // Logs a sudden change in the hand's distance between consecutive results, with what the
        // models saw, to find what throws the estimate. At most one line a second.
        void LogDistanceJump(float distance, float rawDepth, float palmSpan, float fitError, float presence, float regionSize, float palmSize)
        {
            var now = Time.unscaledTime;
            if (now - m_LastResultTime < 0.25f && m_LastDistance > 0f && now - m_LastJumpLog > 1f &&
                Mathf.Abs(Mathf.Log(distance / m_LastDistance)) > Mathf.Log(k_JumpRatio))
            {
                m_LastJumpLog = now;
                Debug.Log($"[Hands] Distance jumped {m_LastDistance:F2} -> {distance:F2} m (unsmoothed {rawDepth:F2}): palm spans " +
                          $"{m_LastPalmSpan:F0} -> {palmSpan:F0} px, model's palm {palmSize * 100f:F1} cm (learned " +
                          $"{m_PalmSize * 100f:F1}), fit error {fitError:F1} px, presence {presence:F2}, region {regionSize:F0} px");
            }
            m_LastDistance = distance;
            m_LastPalmSpan = palmSpan;
            m_LastResultTime = now;
        }

        static void CopyTo(Tensor<float> tensor, float[] values)
        {
            var count = Mathf.Min(values.Length, tensor.shape.length);
            for (var i = 0; i < count; i++)
                values[i] = tensor[i];
        }

        void LogStatus()
        {
            var seconds = Time.unscaledTime - m_StatusStart;
            if (seconds < k_StatusSeconds)
                return;
            if (m_Images > 0)
            {
                var landmarkerRuns = Mathf.Max(1, m_Images - (m_Detections - (m_Images - m_Results - m_WithoutPose)));
                var distance = float.IsPositiveInfinity(m_NearestHand) ? "no hand" : $"hand {m_NearestHand:F2}-{m_FarthestHand:F2} m away";
                Debug.Log($"[Hands] {m_Images / seconds:F1} images/s, placed {m_Results}, {m_Detections} detector runs, " +
                          $"{m_Losses} lost, {m_WithoutPose} without a pose; {distance}; per image sampling " +
                          $"{m_SampleMs / m_Images:F1} ms; detector {m_DetectMs / Mathf.Max(1, m_Detections):F0} ms and landmarker " +
                          $"{m_LandmarkMs / landmarkerRuns:F0} ms including waits; presence {m_PresenceSum / landmarkerRuns:F2}, " +
                          $"handedness {m_Handedness:F2}; fit error {m_FitErrorSum / Mathf.Max(1, m_Fits):F1} px, {m_RejectedFits} " +
                          $"collapsed shapes replaced, {m_BadFits} bad fits, {m_OutOfReach} new hands beyond reach ignored, " +
                          $"palm {m_PalmSize * 100f:F1} cm, " +
                          $"{m_InterpolatedPoses} poses interpolated, fingertips " +
                          (float.IsPositiveInfinity(m_ClosestPinch) ? "not seen" : $"{m_ClosestPinch * 100f:F1}-{m_WidestPinch * 100f:F1} cm apart") +
                          $"; results {m_ResultAgeSum * 1000 / Math.Max(1, m_ResultAges):F0} ms behind the camera image, " +
                          $"predicted {m_LeadSum * 1000 / Math.Max(1, m_Leads):F0} ms on" + JumpSummary() + DepthSummary());
            }
            m_ShownDepthJumps = m_ShownAcrossJumps = m_HeldDepthJumps = m_HeldAcrossJumps = m_ShownPalmJumps = m_HeldPalmJumps = 0;
            m_Jumps = 0;
            m_NearDepthRatios.Clear();
            m_FarDepthRatios.Clear();
            m_DepthNoPixels = m_DepthCalibrations = m_ImplausibleDepths = 0;
            m_DepthSampler.ClearCounts();
            m_Images = m_Results = m_Detections = m_Losses = m_WithoutPose = m_Fits = m_BadFits = m_RejectedFits = m_InterpolatedPoses = 0;
            m_OutOfReach = 0;
            m_FitErrorSum = 0f;
            m_ClosestPinch = float.PositiveInfinity;
            m_WidestPinch = 0f;
            m_ResultAgeSum = m_LeadSum = 0;
            m_ResultAges = m_Leads = 0;
            m_SampleMs = m_DetectMs = m_LandmarkMs = 0;
            m_PresenceSum = 0f;
            m_NearestHand = float.PositiveInfinity;
            m_FarthestHand = 0f;
            m_StatusStart = Time.unscaledTime;
        }
    }
}
