using CardboardXR.Tracking;
using UnityEngine;
using CardboardXR.UI;

namespace CardboardXR.Stereo
{
    // Drives the head in one of two modes. 3DoF: GyroHeadTracker's rotation alone, with ARCore off
    // and the display at up to 120 Hz. 6DoF: ARCore adds position; Android caps the display at 60 Hz
    // while the camera runs. The rotation still comes from the head tracker's 400 Hz sensor fusion.
    // ARCore's 30 Hz camera poses only correct its heading (YawAligner) and fix the position.
    // PositionFilter integrates the accelerometer between those fixes, which are 45-100 ms old when
    // they arrive, and predicts ahead to the display.
    //
    // ARCore misbehaves in poor light or with few features. It loses tracking, or keeps "tracking"
    // while its position slides or jumps, or turns its whole space when it relocalizes. In each case
    // the view holds or carries on from where it is, never jumping: ARCore's positions are offset to
    // meet the eyes and the filter restarts there at rest ("re-anchoring"). A position estimate that
    // runs away faster than anyone moves in a room is re-anchored too. The "dof" launch option (3 or
    // 6) picks the starting mode.
    [DefaultExecutionOrder(-90)]
    public class HeadPoseFusion : MonoBehaviour
    {
        public enum Mode
        {
            ThreeDof,
            SixDof,
        }

        const float k_StatsIntervalSeconds = 5f;
        // Longer than this without a new ARCore pose counts as lost tracking.
        const long k_TrackingTimeoutNs = 300_000_000;
        // This many rejected fixes in a row means ARCore's position jumped or slid away for good.
        const int k_RejectionsToReanchor = 10;
        // A rotation pair further than this from the heading-corrected one is treated as mismatched.
        const float k_MaxPairErrorDegrees = 10f;
        // This many mismatched pairs in a row means ARCore's space turned; accelerations stop until
        // the rotations agree again.
        const int k_MismatchesToCoast = 3;
        // Faster than this, in m/s, the position estimate has run away.
        const float k_MaxSpeed = 3f;
        // A re-anchor after rejections keeps a bias smaller than this, in m/s^2.
        const double k_TrustedBias = 0.3;
        // How fast the world rises or falls to meet a newly detected floor.
        const float k_FloorEaseMetersPerSecond = 0.5f;

        [SerializeField] GyroHeadTracker m_Tracker;
        [SerializeField] ArPoseSource m_ArPose;
        [SerializeField] Mode m_Mode = Mode.ThreeDof;
        [Tooltip("The eyes' midpoint relative to the AR camera, in head axes (x right, y up, z forward). " +
                 "The default estimates a Pixel 9 Pro in a Cardboard viewer.")]
        [SerializeField] Vector3 m_CameraToEyes = new(0.062f, -0.02f, -0.066f);
        [Tooltip("How long the eyes take to move onto the camera, or back, when EyesAtCamera changes.")]
        [SerializeField] float m_EyeShiftSeconds = 0.5f;
        [Tooltip("The panel's 6DoF toggle, shown selected in 6DoF.")]
        [SerializeField] M3Button m_ModeToggle;
        [Tooltip("The virtual floor's height in world space. In 6DoF a detected real floor is lined up with it.")]
        [SerializeField] float m_VirtualFloorHeight = -1.6f;

        readonly PositionFilter m_Filter = new();
        readonly YawAligner m_Yaw = new();
        // Learns the gyroscope bias behind the heading drift; kept across modes, since it applies in
        // 3DoF too.
        readonly GyroDriftEstimator m_Drift = new();
        long m_LastArTimestampNs;
        long m_LastArArrivalNs;
        // Camera timestamps on the tracker's clock; ARKit's and the tracker's differ on iOS.
        readonly CameraClock m_CameraClock = new();
        bool m_Tracking;
        // Added to ARCore positions so the view continues from where it was, not where ARCore jumped.
        Vector3 m_ArOffset;
        Vector3 m_LastEyes;
        // How far the eyes have moved onto the camera, from 0 to 1.
        float m_EyesAtCameraAmount;
        // The heading correction in use while YawAligner starts over.
        Quaternion m_HeldCorrection = Quaternion.identity;
        // The full rotation from the tracker's space to ARCore's at the latest consistent pose, for
        // accelerations. It includes the tilt the two disagree on, which would leak gravity in.
        Quaternion m_AccelerationCorrection = Quaternion.identity;
        bool m_HasAccelerationCorrection;
        Quaternion m_RecenterRotation = Quaternion.identity;
        Vector3 m_RecenterPivot;
        bool m_RecenterPending;
        // Raises or lowers the world so the detected floor meets the virtual one.
        bool m_HasFloor;
        float m_FloorHeight;
        float m_HeightOffset;
        float m_LastHeightTime;
        int m_ConsecutiveRejections;
        int m_ConsecutiveMismatches;
        // The "imuPosition" launch option (0) leaves the accelerometer out, for comparison.
        bool m_UseAccelerometer = true;

        // Statistics for the [Pose] log.
        int m_ArSamples;
        int m_UnmatchedSamples;
        int m_MismatchedPairs;
        int m_Reanchors;
        int m_AlignmentSamples;
        float m_AlignmentErrorSum;
        float m_TiltErrorSum;
        float m_StatsStart;

        public Mode CurrentMode => m_Mode;

        // Just before switching to a mode, while the old one still runs: what else uses the camera
        // (PinchSensor) must let go of it before the AR session starts.
        public event System.Action<Mode> ModeChanging;

        // Whether ARCore is tracking the phone in 6DoF right now.
        public bool IsArTracking => m_Mode == Mode.SixDof && m_Tracking;

        // Where ARCore's session space sits in the world, for placing what ARCore detects, such as
        // planes.
        public Pose SessionToWorld => new(
            m_RecenterRotation * (m_ArOffset - m_RecenterPivot) + Vector3.up * m_HeightOffset, m_RecenterRotation);

        // The eyes' position in ARCore's session space.
        public Vector3 EyesInSession => m_LastEyes - m_ArOffset;

        // Puts the eyes at the AR camera instead of the viewer's eyes, for passthrough: the camera
        // image is then seen from where it was taken, so near things in it line up with the virtual
        // world. The eyes move there over m_EyeShiftSeconds.
        public bool EyesAtCamera { get; set; }

        // A pose to take as-is instead of the fused one while AR tracks, or null: the AR camera's, for
        // the single phone view in passthrough, where the camera image should sit still on the screen
        // with virtual things locked to it, as in an ordinary AR app (PassthroughController). It has
        // the image's own delay, which doesn't matter in a hand-held view as it does in a viewer.
        public Transform FollowPose { get; set; }

        Vector3 CameraToEyes => Vector3.Lerp(m_CameraToEyes, Vector3.zero, m_EyesAtCameraAmount);

        // A detected real floor's height in ARCore's session space; the world eases to line it up
        // with the virtual floor.
        public void SetFloor(float sessionHeight)
        {
            m_HasFloor = true;
            m_FloorHeight = sessionHeight;
        }

        Quaternion Correction => m_Yaw.IsInitialized ? ToQuaternion(m_Yaw.Correction) : m_HeldCorrection;

        void OnEnable()
        {
            m_Tracker.LinearAccelerationSampled += OnLinearAcceleration;
            Application.onBeforeRender += UpdatePose;
            m_StatsStart = Time.unscaledTime;
            m_UseAccelerometer = LaunchOptions.GetInt("imuPosition", 1) != 0;
            var sixDof = LaunchOptions.GetInt("dof", m_Mode == Mode.SixDof ? 6 : 3) == 6;
            SetMode(sixDof ? Mode.SixDof : Mode.ThreeDof, force: true);
        }

        void OnDisable()
        {
            m_Tracker.LinearAccelerationSampled -= OnLinearAcceleration;
            Application.onBeforeRender -= UpdatePose;
        }

        public void ToggleMode() => SetMode(m_Mode == Mode.SixDof ? Mode.ThreeDof : Mode.SixDof);

        public void SetMode(Mode mode) => SetMode(mode, force: false);

        void SetMode(Mode mode, bool force)
        {
            if (mode == m_Mode && !force)
                return;

            ModeChanging?.Invoke(mode);
            m_Mode = mode;
            var sixDof = mode == Mode.SixDof;
            m_ArPose.enabled = sixDof;
            m_Tracker.DrivesTransform = !sixDof;
            m_Filter.Reset();
            m_Yaw.Reset();
            m_Drift.Restart();
            m_HeldCorrection = Quaternion.identity;
            m_HasAccelerationCorrection = false;
            m_LastArTimestampNs = 0;
            m_CameraClock.Reset();
            m_Tracking = false;
            m_ConsecutiveRejections = 0;
            m_ConsecutiveMismatches = 0;
            m_ArOffset = Vector3.zero;
            m_LastEyes = Vector3.zero;
            m_RecenterRotation = Quaternion.identity;
            m_RecenterPivot = Vector3.zero;
            m_RecenterPending = sixDof;
            m_HasFloor = false;
            m_HeightOffset = 0f;
            if (!sixDof)
            {
                transform.localPosition = Vector3.zero;
                m_Tracker.Recenter();
            }

            if (m_ModeToggle != null)
                m_ModeToggle.Selected = sixDof;
            Debug.Log(sixDof
                ? "[Pose] 6DoF: ARCore on. Android limits the display to 60 Hz while the camera runs."
                : "[Pose] 3DoF: ARCore off");
        }

        // Turns the current heading into straight ahead and puts the head over the origin, keeping
        // its height.
        public void Recenter()
        {
            if (m_Mode == Mode.ThreeDof)
                m_Tracker.Recenter();
            else
                m_RecenterPending = true;
        }

        void Update()
        {
            UpdatePose();
            LogStats();
        }

        void UpdatePose()
        {
            // Reading the rotation also feeds the tracker's new samples, and their accelerations, in.
            if (m_Mode != Mode.SixDof || !m_Tracker.TryGetRotation(out var trackerRotation))
                return;

            var nowNs = m_Tracker.NowNs;
            ConsumeArPose(nowNs);

            m_EyesAtCameraAmount = Mathf.MoveTowards(m_EyesAtCameraAmount, EyesAtCamera ? 1f : 0f,
                Time.unscaledDeltaTime / Mathf.Max(0.01f, m_EyeShiftSeconds));
            var rotation = Correction * trackerRotation;
            var eyes = m_LastEyes;
            if (m_Tracking && m_Filter.IsInitialized)
            {
                var speed = (float)m_Filter.Velocity.Length;
                if (speed > k_MaxSpeed)
                {
                    // Nobody moves this fast in a room. Hold, and re-anchor at the next fix.
                    m_Reanchors++;
                    m_Tracking = false;
                    m_Filter.Reset();
                    Debug.Log($"[Pose] Position estimate ran away at {speed:F1} m/s; re-anchoring");
                }
                else
                {
                    var predictNs = nowNs + (long)(m_Tracker.PredictionSeconds * 1e9);
                    eyes = ToVector3(m_Filter.PredictPosition(predictNs)) + rotation * CameraToEyes;
                    m_LastEyes = eyes;
                }
            }

            if (m_RecenterPending)
            {
                m_RecenterPending = false;
                m_RecenterRotation = Quaternion.Euler(0f, -rotation.eulerAngles.y, 0f);
                m_RecenterPivot = new Vector3(eyes.x, 0f, eyes.z);
            }

            UpdateHeightOffset();
            transform.SetPositionAndRotation(
                m_RecenterRotation * (eyes - m_RecenterPivot) + Vector3.up * m_HeightOffset, m_RecenterRotation * rotation);
            // Kept fused while AR has lost track, so the view doesn't freeze.
            if (FollowPose != null && IsArTracking)
                transform.SetPositionAndRotation(FollowPose.position, FollowPose.rotation);
        }

        // Eases toward lining the detected floor up with the virtual one. Positions carry
        // m_ArOffset, so the floor does too.
        void UpdateHeightOffset()
        {
            var now = Time.unscaledTime;
            var seconds = Mathf.Min(now - m_LastHeightTime, 0.1f);
            m_LastHeightTime = now;
            var target = m_HasFloor ? m_VirtualFloorHeight - (m_FloorHeight + m_ArOffset.y) : 0f;
            m_HeightOffset = Mathf.MoveTowards(m_HeightOffset, target, k_FloorEaseMetersPerSecond * seconds);
        }

        void ConsumeArPose(long nowNs)
        {
            if (!m_ArPose.TryGetLatest(out var sample) || sample.timestampNs <= m_LastArTimestampNs)
            {
                if (m_Tracking && nowNs - m_LastArArrivalNs > k_TrackingTimeoutNs)
                    LoseTracking();
                return;
            }

            m_LastArTimestampNs = sample.timestampNs;
            m_LastArArrivalNs = nowNs;
            m_ArSamples++;
            // From here on the sample's time is on the tracker's clock, which everything below
            // compares it with. Both clocks are read together, so the offset between them is exact.
            var systemNowNs = CameraClock.SystemNowNs();
            if (!m_CameraClock.TryToTrackerClock(sample.timestampNs, m_Tracker.NowNs, systemNowNs, out sample.timestampNs))
            {
                m_UnmatchedSamples++;
                return;
            }
            // The tracker's rotation when the camera image was captured, on the same clock.
            if (!m_Tracker.TryGetRotationAt(sample.timestampNs, out var trackedThen, out var upInSensor))
            {
                m_UnmatchedSamples++;
                return;
            }

            UpdateHeading(sample, trackedThen, upInSensor);

            if (!m_Tracking)
            {
                Reanchor(sample, keepBias: true);
                m_Tracking = true;
                Debug.Log("[Pose] ARCore tracking");
                return;
            }

            if (m_Filter.AddPosition(sample.timestampNs, ToVector3d(sample.position + m_ArOffset)))
            {
                m_ConsecutiveRejections = 0;
            }
            else if (++m_ConsecutiveRejections >= k_RejectionsToReanchor)
            {
                var jump = m_Filter.LastRejectedInnovation.Length;
                m_Reanchors++;
                Reanchor(sample, keepBias: m_Filter.AccelerometerBias.Length < k_TrustedBias);
                Debug.Log($"[Pose] ARCore's position jumped {jump * 100:F0} cm; re-anchoring");
            }
        }

        // Carries on from where the view is: ARCore's positions are offset to meet the eyes, and the
        // filter restarts there at rest.
        void Reanchor(ArPoseSource.Sample sample, bool keepBias)
        {
            m_ArOffset = m_LastEyes - (sample.position + sample.rotation * CameraToEyes);
            m_Filter.Relocate(sample.timestampNs, ToVector3d(sample.position + m_ArOffset), keepBias);
            m_ConsecutiveRejections = 0;
        }

        void UpdateHeading(ArPoseSource.Sample sample, Quaternion trackedThen, Vector3d upInSensor)
        {
            var before = Correction;
            var wasAligned = m_Yaw.IsInitialized;
            m_Yaw.AddSample(sample.timestampNs, ToRotation(sample.rotation), ToRotation(trackedThen));
            if (wasAligned && !m_Yaw.IsInitialized)
            {
                // ARCore turned its space, as when it relocalizes: keep the old heading correction
                // meanwhile, stop accelerations, and re-anchor the position at this fix.
                m_HeldCorrection = before;
                m_Drift.Restart();
                StopAccelerations();
                if (m_Tracking)
                {
                    m_Tracking = false;
                    m_Reanchors++;
                    Debug.Log("[Pose] ARCore's heading jumped; re-anchoring");
                }
            }
            else if (!wasAligned && m_Yaw.IsInitialized)
            {
                // A fresh alignment would turn the view by the change in correction; turn the
                // recentering back by the same amount so it doesn't.
                TurnRecentering(before * Quaternion.Inverse(ToQuaternion(m_Yaw.Correction)));
            }

            if (!m_Yaw.IsInitialized)
                return;
            var aligned = ToQuaternion(m_Yaw.Correction) * trackedThen;
            var error = Quaternion.Angle(aligned, sample.rotation);
            if (error > k_MaxPairErrorDegrees)
            {
                m_MismatchedPairs++;
                if (++m_ConsecutiveMismatches >= k_MismatchesToCoast)
                    StopAccelerations();
                return;
            }

            m_ConsecutiveMismatches = 0;
            m_AlignmentErrorSum += error;
            m_TiltErrorSum += Vector3.Angle(Quaternion.Inverse(aligned) * Vector3.up, Quaternion.Inverse(sample.rotation) * Vector3.up);
            m_AlignmentSamples++;
            m_AccelerationCorrection = sample.rotation * Quaternion.Inverse(trackedThen);
            m_HasAccelerationCorrection = true;

            // ARCore's heading minus the tracker's; its trend is the gyroscope's drift.
            var difference = m_AccelerationCorrection * Vector3.forward;
            var updates = m_Drift.Updates;
            m_Drift.AddSample(sample.timestampNs, Mathf.Atan2(difference.x, difference.z), upInSensor);
            if (m_Drift.Updates != updates)
                m_Tracker.ExternalGyroscopeBias = m_Drift.Bias;
        }

        // Stops integrating accelerations, whose rotation into ARCore's space may be wrong, until
        // the rotations agree again. The filter coasts with no net acceleration meanwhile.
        void StopAccelerations()
        {
            if (!m_HasAccelerationCorrection)
                return;
            m_HasAccelerationCorrection = false;
            if (m_Filter.IsInitialized)
                m_Filter.AddAcceleration(m_Tracker.NowNs, m_Filter.AccelerometerBias);
        }

        // The position holds until tracking returns. ARCore may come back in a moved and turned
        // space, so the heading correction starts over, and accelerations wait for a new rotation.
        void LoseTracking()
        {
            m_Tracking = false;
            m_ConsecutiveRejections = 0;
            m_HasAccelerationCorrection = false;
            m_HeldCorrection = Correction;
            m_Yaw.Reset();
            m_Drift.Restart();
            Debug.Log("[Pose] ARCore tracking lost; holding position");
        }

        // Changes the recentering rotation by turn about the held eye position, so the view's pose
        // stays where it was.
        void TurnRecentering(Quaternion turn)
        {
            var world = m_RecenterRotation * (m_LastEyes - m_RecenterPivot);
            m_RecenterRotation *= turn;
            m_RecenterPivot = m_LastEyes - Quaternion.Inverse(m_RecenterRotation) * world;
        }

        void OnLinearAcceleration(long timeNs, Vector3 acceleration)
        {
            if (m_Mode != Mode.SixDof || !m_Tracking || !m_HasAccelerationCorrection || !m_UseAccelerometer)
                return;
            m_Filter.AddAcceleration(timeNs, ToVector3d(m_AccelerationCorrection * acceleration));
        }

        void LogStats()
        {
            var seconds = Time.unscaledTime - m_StatsStart;
            if (seconds < k_StatsIntervalSeconds)
                return;
            if (m_Mode == Mode.SixDof)
            {
                m_Filter.TakeInnovationStats(out var fixes, out var rms, out var max);
                var alignment = m_AlignmentSamples == 0 ? "no rotation pairs" :
                    $"rotation vs ARCore {m_AlignmentErrorSum / m_AlignmentSamples:F1} deg (tilt {m_TiltErrorSum / m_AlignmentSamples:F1}), " +
                    $"{m_MismatchedPairs} mismatched";
                Debug.Log($"[Pose] 6DoF {(m_Tracking ? "tracking" : "not tracking")}, ARCore {m_ArSamples / seconds:F1} Hz " +
                          $"({m_UnmatchedSamples} unmatched, {m_Filter.RejectedPositions} rejected, {m_Reanchors} re-anchors), " +
                          $"camera clock {m_CameraClock.Source} with images {m_CameraClock.ArrivalAgeNs * 1e-6:F0} ms old on arrival, " +
                          $"fix vs prediction rms {rms * 1000:F1} mm max {max * 1000:F1} mm over {fixes}, " +
                          $"heading correction {m_Yaw.YawRadians * Mathf.Rad2Deg:F1} deg ({m_Yaw.Outliers} outliers, {m_Yaw.Rejected} tilted), " +
                          $"{alignment}, gyro drift {m_Drift.LastDriftRate * Mathf.Rad2Deg:F2} deg/s " +
                          $"(bias correction {m_Drift.Bias} after {m_Drift.Updates} fits), " +
                          $"accelerometer {(m_UseAccelerometer ? "on" : "off")}, height offset {m_HeightOffset:F2} m, " +
                          $"velocity {m_Filter.Velocity}, accel bias {m_Filter.AccelerometerBias}, eyes {m_LastEyes:F3}");
            }
            m_ArSamples = m_UnmatchedSamples = m_MismatchedPairs = m_Reanchors = m_AlignmentSamples = 0;
            m_AlignmentErrorSum = m_TiltErrorSum = 0f;
            m_StatsStart = Time.unscaledTime;
        }

        static Rotation ToRotation(Quaternion q) => Rotation.FromQuaternion(q.x, q.y, q.z, q.w);

        static Quaternion ToQuaternion(Rotation r) => new((float)r.x, (float)r.y, (float)r.z, (float)r.w);

        static Vector3d ToVector3d(Vector3 v) => new(v.x, v.y, v.z);

        static Vector3 ToVector3(Vector3d v) => new((float)v.x, (float)v.y, (float)v.z);
    }
}
