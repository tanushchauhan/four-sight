using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Keeps ARCore tracking running behind the stereo view and exposes its latest camera pose, stamped
    // with the capture time of the camera image it came from. ARCore updates the pose once per camera
    // image, 30 times a second on a Pixel 9 Pro, so the head tracker's rotation has to fill in
    // between. HeadPoseFusion enables it in 6DoF. Logs [AR] stats every few seconds.
    public class ArPoseSource : MonoBehaviour
    {
        public struct Sample
        {
            // In AR session space, which is gravity-aligned with y up.
            public Vector3 position;
            public Quaternion rotation;
            // The camera image's capture time; 0 if no image has arrived yet.
            public long timestampNs;
        }

        const float k_StatsIntervalSeconds = 5f;

        [SerializeField] ARSession m_Session;
        [SerializeField] ARCameraManager m_CameraManager;
        [Tooltip("Starts the XR input subsystem, which provides the camera pose device.")]
        [SerializeField] ARInputManager m_InputManager;

        HandheldARInputDevice m_Device;
        Sample m_Latest;
        bool m_HasSample;
        long m_CameraTimestampNs;
        TrackingState m_TrackingState = TrackingState.None;

        // Statistics for the [AR] log.
        int m_PoseUpdates;
        int m_CameraFrames;
        long m_MinAgeNs = long.MaxValue;
        long m_MaxAgeNs;
        float m_StatsStart;

        // The latest pose, while ARCore is fully tracking. ARSession.state still says SessionTracking
        // when tracking is only limited, as when the camera is covered, and those poses drift and
        // then snap back.
        public bool TryGetLatest(out Sample sample)
        {
            sample = m_Latest;
            return m_HasSample && m_TrackingState == TrackingState.Tracking;
        }

        void OnEnable()
        {
            // Poses and images from before a restart are stale.
            m_HasSample = false;
            m_CameraTimestampNs = 0;
            m_TrackingState = TrackingState.None;
            m_PoseUpdates = m_CameraFrames = 0;
            m_MinAgeNs = long.MaxValue;
            m_MaxAgeNs = 0;

            // Otherwise AR Foundation caps rendering at the camera's frame rate.
            m_Session.matchFrameRateRequested = false;
            m_Session.enabled = true;
            m_InputManager.enabled = true;
            m_CameraManager.enabled = true;
            m_CameraManager.frameReceived += OnCameraFrame;
            m_StatsStart = Time.unscaledTime;
            Debug.Log("[AR] Tracking on");
        }

        // Each is checked, because when the app closes Unity may destroy them before this runs, and
        // enabling or disabling a destroyed component throws: the iPhone logged exactly that on quit.
        void OnDisable()
        {
            if (m_CameraManager != null)
            {
                m_CameraManager.frameReceived -= OnCameraFrame;
                m_CameraManager.enabled = false;
            }
            if (m_Session != null)
                m_Session.enabled = false;
            if (m_InputManager != null)
                m_InputManager.enabled = false;
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            // frameReceived can repeat an image; count only new ones.
            if (args.timestampNs is long timestamp && timestamp != m_CameraTimestampNs)
            {
                m_CameraTimestampNs = timestamp;
                m_CameraFrames++;
            }
        }

        // ARCameraManager runs earlier in the frame, so the timestamp belongs to the current pose.
        void Update()
        {
            var subsystem = m_Session.subsystem;
            var state = subsystem?.trackingState ?? TrackingState.None;
            if (state != m_TrackingState)
            {
                m_TrackingState = state;
                Debug.Log($"[AR] Tracking state {state} ({subsystem?.notTrackingReason})");
            }

            if (m_Device == null || !m_Device.added)
                m_Device = InputSystem.GetDevice<HandheldARInputDevice>();
            if (m_Device != null)
                ReadPose();
            LogStats();
        }

        void ReadPose()
        {
            var position = m_Device.devicePosition.ReadValue();
            var rotation = m_Device.deviceRotation.ReadValue();
            if (m_HasSample && position == m_Latest.position && rotation == m_Latest.rotation)
                return;

            m_Latest = new Sample { position = position, rotation = rotation, timestampNs = m_CameraTimestampNs };
            m_HasSample = true;
            m_PoseUpdates++;
            // Only where the native sensors' clock exists, which is also the camera's there. Without
            // it this measured against 0 and reported images four thousand seconds in the future.
            // HeadPoseFusion's [Pose] line reports image age on every platform.
            if (m_CameraTimestampNs == 0 || !NativeImu.Supported)
                return;
            // How old the image behind the pose is on NativeImu's clock, if they share a clock.
            var ageNs = NativeImu.NowNs() - m_CameraTimestampNs;
            m_MinAgeNs = System.Math.Min(m_MinAgeNs, ageNs);
            m_MaxAgeNs = System.Math.Max(m_MaxAgeNs, ageNs);
        }

        void LogStats()
        {
            var seconds = Time.unscaledTime - m_StatsStart;
            if (seconds < k_StatsIntervalSeconds)
                return;

            var age = m_MinAgeNs == long.MaxValue ? "image age n/a" : $"image age {m_MinAgeNs * 1e-6:F1}-{m_MaxAgeNs * 1e-6:F1} ms";
            var device = m_Device == null ? ", no pose device" : "";
            // The session's own state, which says whether AR ever started: a tracking state of None
            // reads the same whether the session is tracking badly or was never created at all.
            var session = m_Session.subsystem == null ? "no subsystem" : $"session {ARSession.state}";
            var p = m_Latest.position;
            Debug.Log($"[AR] pose {m_PoseUpdates / seconds:F1} Hz, camera {m_CameraFrames / seconds:F1} Hz, {age}, " +
                      $"tracking {m_TrackingState}, {session}, position ({p.x:F3}, {p.y:F3}, {p.z:F3}){device}");
            m_PoseUpdates = m_CameraFrames = 0;
            m_MinAgeNs = long.MaxValue;
            m_MaxAgeNs = 0;
            m_StatsStart = Time.unscaledTime;
        }
    }
}
