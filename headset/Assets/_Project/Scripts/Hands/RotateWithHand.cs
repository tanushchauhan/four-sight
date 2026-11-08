using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CardboardXR.Hands
{
    // Turns a held object with the hand holding it, as grabbed things do in VR: twist the wrist and it
    // twists too. XRI moves it (its XRGrabInteractable tracks position only), because the point XRI
    // follows aims along the line from the eyes through the fingertips, which a wrist turn doesn't
    // change. This takes the hand's own orientation from its tracked joints (HandFrame) when the object
    // is picked up, and while it's held turns the object by however much the hand has turned since,
    // eased a little against the tracker's jitter. Frames where the hand's 3D shape didn't fit are
    // skipped, holding the last turn, since the shape then collapses.
    //   The tracked hand's orientation isn't always to be trusted: from one camera a hand and its
    //   mirror image look alike, and the shape flips front to back for a while, turning the palm by
    //   up to 180 degrees (a cube only carried turned 140-154). So a change of more than k_MaxStep
    //   between two tracker results is taken as a glitch: ignored, and if it lasts k_FlipSeconds,
    //   taken as the new reference without moving the object. And turns under k_DeadZone don't count,
    //   so the small wrist movements of carrying don't turn it; bigger ones ramp in (DeadZoned).
    // Goes on the object's prefab, with its XRGrabInteractable. Logs [Hands].
    [RequireComponent(typeof(XRGrabInteractable))]
    public class RotateWithHand : MonoBehaviour
    {
        // How quickly the object catches up with the hand's turn: the time constant, in seconds.
        const float k_EaseSeconds = 0.1f;
        // Degrees between two tracker results that can't be a real turn.
        public const float k_MaxStep = 40f;
        const float k_FlipSeconds = 0.4f;
        // Degrees of hand turn ignored.
        public const float k_DeadZone = 15f;

        static HandTracker s_Tracker;

        XRGrabInteractable m_Grab;
        Rigidbody m_Body;
        // Where it's turned to now, easing toward m_Target; applied through the body if it has one,
        // since setting an interpolated body's rotation directly is overwritten.
        Quaternion m_Shown;
        bool m_Held;
        Quaternion m_HandAtGrab;
        Quaternion m_ObjectAtGrab;
        Quaternion m_Target;
        float m_MostTurn;
        // The last hand orientation taken, the tracker result it came from, and since when results
        // have jumped away from it.
        Quaternion m_LastHand;
        int m_LastResult = -1;
        float m_JumpingSince = -1f;
        int m_Jumps;

        void Awake()
        {
            m_Grab = GetComponent<XRGrabInteractable>();
            m_Body = GetComponent<Rigidbody>();
        }

        void OnEnable()
        {
            m_Grab.selectEntered.AddListener(OnSelectEntered);
            m_Grab.selectExited.AddListener(OnSelectExited);
        }

        void OnDisable()
        {
            m_Grab.selectEntered.RemoveListener(OnSelectEntered);
            m_Grab.selectExited.RemoveListener(OnSelectExited);
            m_Held = false;
        }

        // The hand's orientation from its wrist and knuckles, in world space: forward from the wrist to
        // the middle finger's knuckle, up out of the back or the palm (which way depends on the hand,
        // but only turns from one moment to the next are used). Null if the joints are degenerate.
        public static Quaternion? HandFrame(Vector3 wrist, Vector3 indexKnuckle, Vector3 middleKnuckle, Vector3 pinkyKnuckle)
        {
            var forward = middleKnuckle - wrist;
            var across = indexKnuckle - pinkyKnuckle;
            var up = Vector3.Cross(forward, across);
            if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-10f)
                return null;
            return Quaternion.LookRotation(forward, up);
        }

        // A turn with the first k_DeadZone degrees taken out, ramping back to the full turn by twice
        // that, so it starts gently and then tracks the hand exactly. Pure, for testing.
        public static Quaternion DeadZoned(Quaternion turn)
        {
            turn.ToAngleAxis(out var angle, out var axis);
            if (angle > 180f)
                angle -= 360f;
            var size = Mathf.Abs(angle);
            float kept;
            if (size <= k_DeadZone)
                kept = 0f;
            else if (size < 2f * k_DeadZone)
                kept = (size - k_DeadZone) * 2f;
            else
                kept = size;
            return Quaternion.AngleAxis(Mathf.Sign(angle) * kept, axis);
        }

        static bool TryHand(out Quaternion hand)
        {
            hand = Quaternion.identity;
            if (s_Tracker == null)
                s_Tracker = FindAnyObjectByType<HandTracker>();
            if (s_Tracker == null || !s_Tracker.IsTracking || !s_Tracker.ShapeFits)
                return false;
            var joints = s_Tracker.Joints;
            var frame = HandFrame(joints[0], joints[5], joints[9], joints[17]);
            if (!frame.HasValue)
                return false;
            hand = frame.Value;
            return true;
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            m_Held = TryHand(out m_HandAtGrab);
            m_LastHand = m_HandAtGrab;
            m_LastResult = s_Tracker != null ? s_Tracker.ResultCount : -1;
            m_JumpingSince = -1f;
            m_Jumps = 0;
            m_ObjectAtGrab = transform.rotation;
            m_Target = transform.rotation;
            m_Shown = transform.rotation;
            m_MostTurn = 0f;
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (m_Held)
                Debug.Log($"[Hands] {name} turned with the hand, up to {m_MostTurn:F0} degrees; {m_Jumps} jumps in the hand's " +
                          "orientation ignored");
            m_Held = false;
        }

        void LateUpdate()
        {
            if (!m_Grab.isSelected)
                return;
            // The hand may not have been seen whole at the grab: start from the first time it is.
            if (!m_Held)
            {
                if (!TryHand(out m_HandAtGrab))
                    return;
                m_Held = true;
                m_LastHand = m_HandAtGrab;
                m_LastResult = s_Tracker.ResultCount;
                m_ObjectAtGrab = transform.rotation;
                m_Target = transform.rotation;
                m_Shown = transform.rotation;
                return;
            }
            // Each new tracker result once.
            if (s_Tracker != null && s_Tracker.ResultCount != m_LastResult && TryHand(out var hand))
            {
                m_LastResult = s_Tracker.ResultCount;
                if (Quaternion.Angle(m_LastHand, hand) > k_MaxStep)
                {
                    if (m_JumpingSince < 0f)
                    {
                        m_JumpingSince = Time.unscaledTime;
                        m_Jumps++;
                    }
                    if (Time.unscaledTime - m_JumpingSince < k_FlipSeconds)
                        return;
                    // It stayed there: the new reference, carrying over the turn so far.
                    m_HandAtGrab = hand * Quaternion.Inverse(m_LastHand) * m_HandAtGrab;
                }
                m_JumpingSince = -1f;
                m_LastHand = hand;
                var turn = DeadZoned(hand * Quaternion.Inverse(m_HandAtGrab));
                m_Target = turn * m_ObjectAtGrab;
                m_MostTurn = Mathf.Max(m_MostTurn, Quaternion.Angle(Quaternion.identity, turn));
            }
            m_Shown = Quaternion.Slerp(m_Shown, m_Target, 1f - Mathf.Exp(-Time.deltaTime / k_EaseSeconds));
            if (m_Body == null)
                transform.rotation = m_Shown;
        }

        void FixedUpdate()
        {
            if (m_Body != null && m_Held && m_Grab.isSelected)
                m_Body.MoveRotation(m_Shown);
        }
    }
}
