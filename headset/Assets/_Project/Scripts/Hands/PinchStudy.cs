using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Hands
{
    // A short, prompted run for tuning pinch detection, with the "pinchStudy" launch option (1). Text
    // in front of the eyes alternates between asking for an open hand and a pinch, first with the palm
    // toward the camera and then with the hand turned sideways, and every hand result is logged with
    // the prompt ([Pinch]): the fingertips' 3D gap in the model's hand shape, their 2D gap over the
    // palm's size in the image, whether the shape fit, and the presence score. Results just after the
    // prompt changes are marked "moving", since the hand is still getting there. Pinch actions are off
    // during the run. It waits for the first hand result before starting, since tracking may not have
    // begun (the phone face down, or the room still being scanned).
    public class PinchStudy : MonoBehaviour
    {
        // The "get ready" time after the hand is first found.
        const float k_IntroSeconds = 3f;
        const float k_PhaseSeconds = 3f;
        // Open-then-pinch cycles for each pose.
        const int k_CyclesPerPose = 6;
        // Results this soon after the prompt changes are marked "moving".
        const float k_SettleSeconds = 0.8f;

        [SerializeField] HandTracker m_Tracker;
        [Tooltip("Turned off during the run, so pinches don't click or grab.")]
        [SerializeField] Behaviour m_HandInput;
        [Tooltip("The head; the prompt sits in front of it.")]
        [SerializeField] Transform m_Head;

        TMP_Text m_Prompt;
        float m_Start;
        int m_LastResult;
        bool m_Running;
        // Whether a hand has been found, which starts the clock.
        bool m_Started;

        void Start()
        {
            if (LaunchOptions.GetInt("pinchStudy", 0) == 0)
            {
                enabled = false;
                return;
            }
            CreatePrompt();
            if (m_HandInput != null)
                m_HandInput.enabled = false;
            m_LastResult = m_Tracker.ResultCount;
            m_Running = true;
            m_Prompt.text = "Pinch study\nHold one hand up in front of the camera";
            Debug.Log($"[Pinch] Study waiting for a hand: {k_CyclesPerPose} open/pinch cycles of {k_PhaseSeconds:F0} s per pose, palm then side");
        }

        void Update()
        {
            if (!m_Running)
                return;
            if (!m_Started)
            {
                if (m_Tracker.ResultCount == m_LastResult)
                    return;
                m_Started = true;
                m_Start = Time.unscaledTime;
                m_LastResult = m_Tracker.ResultCount;
                Debug.Log("[Pinch] Hand found; study starting");
            }

            var elapsed = Time.unscaledTime - m_Start;
            string pose = null, phase = null;
            var sinceChange = elapsed;
            if (elapsed < k_IntroSeconds)
            {
                m_Prompt.text = "Get ready\nOpen or pinch when asked";
            }
            else
            {
                var step = (int)((elapsed - k_IntroSeconds) / k_PhaseSeconds);
                var stepsPerPose = 2 * k_CyclesPerPose;
                if (step >= 2 * stepsPerPose)
                {
                    Finish();
                    return;
                }
                pose = step < stepsPerPose ? "palm" : "side";
                phase = step % 2 == 0 ? "open" : "pinch";
                sinceChange = elapsed - k_IntroSeconds - step * k_PhaseSeconds;
                m_Prompt.text = (phase == "open" ? "OPEN your hand" : "PINCH thumb and index") +
                                (pose == "palm" ? "\n(palm toward the camera)" : "\n(hand turned sideways)");
            }

            if (m_Tracker.ResultCount == m_LastResult)
                return;
            m_LastResult = m_Tracker.ResultCount;
            if (phase == null)
                return;
            Debug.Log($"[Pinch] {pose} {phase} {(sinceChange >= k_SettleSeconds ? "settled" : "moving")}: " +
                      $"3d {m_Tracker.PinchDistance * 100f:F2} cm, 2d {m_Tracker.PinchRatio:F3}, " +
                      $"shape {(m_Tracker.ShapeFits ? "fits" : "collapsed")}, presence {m_Tracker.Presence:F2}");
        }

        void Finish()
        {
            m_Running = false;
            m_Prompt.text = "Done. Thanks!";
            if (m_HandInput != null)
                m_HandInput.enabled = true;
            Debug.Log("[Pinch] Study finished");
            Destroy(m_Prompt.canvas.gameObject, 3f);
        }

        // A Material panel of text, held in front of the eyes and a little above center, clear of the hand.
        void CreatePrompt()
        {
            const float distance = 1.2f;
            var panel = M3.Canvas("Pinch Study Prompt", m_Head, new Vector2(480f, 128f), distance,
                M3Theme.WithAlpha(M3Theme.SurfaceContainer, 0.9f));
            panel.localPosition = new Vector3(0f, 0.12f, distance);
            m_Prompt = M3.Text("Text", panel, "", M3Theme.Type.TitleLarge, Vector2.zero, new Vector2(440f, 104f));
        }
    }
}
