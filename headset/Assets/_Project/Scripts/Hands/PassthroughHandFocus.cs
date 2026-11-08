using CardboardXR.Stereo;
using UnityEngine;

namespace CardboardXR.Hands
{
    // While a hand is tracked, moves the passthrough image to the hand's distance, so the hand in the
    // image lines up with its tracked joints in both eyes. The image is flat and can only be at one
    // depth, and a raised hand is what the user is looking at. Brief tracking gaps don't send the
    // image back out.
    public class PassthroughHandFocus : MonoBehaviour
    {
        [SerializeField] HandTracker m_Tracker;
        [SerializeField] PassthroughController m_Passthrough;
        [Tooltip("How long the hand can be lost before the image goes back to its usual distance.")]
        [SerializeField] float m_HoldSeconds = 0.5f;

        float m_LastSeen = float.NegativeInfinity;

        // Before PassthroughController places the image in LateUpdate.
        void Update()
        {
            if (m_Tracker.IsTracking)
            {
                m_LastSeen = Time.unscaledTime;
                m_Passthrough.FocusDistance = m_Tracker.HandDistance;
            }
            else if (Time.unscaledTime - m_LastSeen > m_HoldSeconds)
            {
                m_Passthrough.FocusDistance = null;
            }
        }
    }
}
