using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Gaze and pinch are the app's main input everywhere: the head aims, a pinch clicks, and a screen
    // tap still works too. In 6DoF, hand tracking (HandTracker, through HandInteractorRig) turns a
    // pinch into a click and also picks things up. In 3DoF there's no AR session to track the hand
    // with, so PinchSensor watches a plain camera feed for pinches instead. This runs it whenever the
    // app is in 3DoF and nothing else has the camera: it lets go of the camera just before 6DoF starts
    // the AR session, and waits k_HandoffSeconds after 6DoF ends before taking it back, since the
    // camera can come up silent if reopened at once. The visor leaves it off: a plain camera feed in
    // 3DoF is heat the robot view does not need, and a screen tap still clicks. The "pinch 1" launch
    // option turns it on. (The detector slows to a few checks a second with no hand in view.) It's
    // for 3DoF only: in 6DoF, hand tracking is part of what each demo asks for (DemoRequirements.Hands).
    // The reticle turns
    // light blue while a hand is in view in either mode, so it's clear a pinch will register; and while
    // the gaze rests on a button with no hand in view, a hint under the reticle says to raise one (the
    // camera only sees about 21 degrees above and below the gaze, so a hand resting low can't pinch:
    // in the lobby a pinch on the settings panel's X once took 16 s to be seen). Put it on the XR
    // Origin. Logs [Pinch].
    public class PinchInput : MonoBehaviour
    {
        const float k_HandoffSeconds = 1f;
        // How long the gaze rests on a button, and no hand is seen, before the hint shows, in seconds.
        public const float k_HintSeconds = 1f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] PinchSensor m_Sensor;
        [SerializeField] HandTracker m_Tracker;
        [SerializeField] GazePointer m_Gaze;
        [Tooltip("Under the reticle: shown while the gaze rests on a button and no hand is in view.")]
        [SerializeField] GameObject m_HandHint;

        bool m_On = true;
        float m_ThreeDofSince;
        float m_NoHandFor;
        float m_OnButtonFor;

        // Whether pinching clicks. Off unless the "pinch 1" launch option says otherwise.
        public bool IsOn => m_On;

        void Awake()
        {
            m_On = LaunchOptions.GetInt("pinch", 0) != 0;
        }

        void OnEnable()
        {
            m_Fusion.ModeChanging += OnModeChanging;
            m_ThreeDofSince = Time.unscaledTime;
            Debug.Log($"[Pinch] Pinch to click {(m_On ? "on" : "off")}");
        }

        void OnDisable()
        {
            m_Fusion.ModeChanging -= OnModeChanging;
            m_Sensor.enabled = false;
        }

        void OnModeChanging(HeadPoseFusion.Mode next)
        {
            if (next == HeadPoseFusion.Mode.SixDof)
                m_Sensor.enabled = false;
            else
                m_ThreeDofSince = Time.unscaledTime;
        }

        void Update()
        {
            var threeDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.ThreeDof;
            var wanted = m_On && threeDof && Time.unscaledTime - m_ThreeDofSince >= k_HandoffSeconds;
            if (m_Sensor.enabled != wanted)
                m_Sensor.enabled = wanted;

            var handSeen = threeDof
                ? m_Sensor.enabled && m_Sensor.SeesHand
                : m_Tracker.isActiveAndEnabled && m_Tracker.IsTracking;
            if (m_Gaze.Reticle != null)
                m_Gaze.Reticle.HandSeen = handSeen;

            var pinchRunning = threeDof ? m_Sensor.enabled : m_Tracker.isActiveAndEnabled && !m_Tracker.IsPaused;
            var deltaTime = Time.unscaledDeltaTime;
            m_NoHandFor = handSeen ? 0f : m_NoHandFor + deltaTime;
            m_OnButtonFor = PinchRouter.IsButton(m_Gaze.Target) ? m_OnButtonFor + deltaTime : 0f;
            if (m_HandHint != null)
            {
                var show = ShowHint(pinchRunning, m_NoHandFor, m_OnButtonFor);
                if (M3Fade.IsShown(m_HandHint) != show)
                    M3Fade.SetShown(m_HandHint, show);
            }
        }

        // Whether to show the hint: pinching is running, and for k_HintSeconds the gaze has rested on a
        // button and no hand has been seen. Pure, for testing.
        public static bool ShowHint(bool pinchRunning, float noHandFor, float onButtonFor) =>
            pinchRunning && noHandFor >= k_HintSeconds && onButtonFor >= k_HintSeconds;
    }
}
