using CardboardXR.Interaction;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace CardboardXR.Hands
{
    // Two ways to reach things that never overlap: the head looks at UI and at surfaces and a pinch
    // clicks them, while things to pick up are taken by the hand — beside them, or along the line from
    // the eyes through the point between the thumb and index fingertips. A pinch never clicks what it
    // could pick up, and never picks up what it clicks; PinchRouter decides which, and a button under
    // the gaze comes first.
    // Every frame, before XRI's interaction manager (XRInteractionUpdateOrder.k_InteractionManager,
    // −105), this puts the interactor's point between the fingertips, facing along that line, which
    // both its casters and its attach controller use: a near sphere for what's beside the fingers, a
    // far ray for what's out of reach, and what's held follows the hand.
    // Closing the fingers moves the point between them, and with it the ray, by a median 5.6 degrees
    // on the phone, so a pinch aims with the ray from k_AimLookbackSeconds before it (AimHistory) and
    // keeps that turn while it holds something, easing back when the fingers open.
    // A brief loss keeps the pinch, so tracking flickers don't drop what's being carried; after
    // m_LostSeconds the interactor turns off, which lets go. That grace is short, because a hand
    // holding something sits close to the camera, where its 68 by 42 degrees see a window only about
    // 27 by 15 cm across: carrying a cube, the hand leaves the view far more often than it opens, so
    // leaving the view has to let go promptly. Put it on the XR Origin. Logs [Hands].
    [DefaultExecutionOrder(-110)]
    public class HandInteractorRig : MonoBehaviour, IXRInputButtonReader
    {
        // How far back a pinch takes its aim from, in seconds: before the fingers started closing.
        const float k_AimLookbackSeconds = 0.2f;
        const float k_AimHistorySeconds = 0.5f;
        // How quickly the ray eases back onto the hand after a pinch: the time constant, in seconds.
        const float k_AimReturnSeconds = 0.1f;

        [SerializeField] HandTracker m_Tracker;
        [SerializeField] HandSubsystemFeed m_Feed;
        [Tooltip("The head's gaze pointer: it aims at UI and surfaces, and a pinch clicks what it's on.")]
        [SerializeField] GazePointer m_Gaze;
        [SerializeField] NearFarInteractor m_Interactor;
        [Tooltip("The interactor's casters and attach point, kept between the thumb and index fingertips.")]
        [SerializeField] Transform m_HandPoint;
        [Tooltip("How long the hand can be lost, in seconds, before a pinch counts as let go. Short: " +
                 "leaving the camera's view is how a hand usually ends up letting go of something.")]
        [SerializeField] float m_LostSeconds = 0.25f;

        readonly AimHistory m_Aims = new(k_AimHistorySeconds);
        float m_LastSeen = float.NegativeInfinity;
        bool m_Pinched;
        // Whether this pinch took hold of something, rather than clicking.
        bool m_Grabbing;
        // Whether a demo claimed this pinch (PinchRouter.Handler), rather than it clicking.
        bool m_Claimed;
        int m_PinchFrame = -1;
        int m_ReleaseFrame = -1;
        // How the ray is turned from the hand's line: at a pinch, back onto the line from before it.
        Quaternion m_AimTurn = Quaternion.identity;

        public bool IsPinching => m_Pinched;

        // Whether the hand is being tracked well enough to reach anything.
        public bool IsActive => m_Interactor != null && m_Interactor.gameObject.activeSelf;

        // Whether the interactor is on something that can be picked up, near or along the ray.
        public bool HasGrabTarget => FindGrabbable() != null;

        void OnDisable()
        {
            EndPinch();
            m_Aims.Clear();
            m_AimTurn = Quaternion.identity;
            if (m_Interactor != null)
                m_Interactor.gameObject.SetActive(false);
        }

        void Update()
        {
            var now = Time.unscaledTime;
            var tracked = m_Tracker.IsTracking;
            if (tracked)
            {
                m_LastSeen = now;
                var eyes = m_Gaze.transform;
                var fingertips = m_Tracker.PinchPoint;
                var line = new Ray(fingertips, fingertips - eyes.position);
                m_Aims.Add(now, line);
                if (!m_Pinched)
                    m_AimTurn = Quaternion.Slerp(m_AimTurn, Quaternion.identity,
                        1f - Mathf.Exp(-Time.unscaledDeltaTime / k_AimReturnSeconds));
                m_HandPoint.SetPositionAndRotation(fingertips,
                    m_AimTurn * Quaternion.LookRotation(line.direction, eyes.up));
            }

            // Picking things up needs a hand, so the interactor only runs while there is one.
            var hasHand = now - m_LastSeen <= m_LostSeconds;
            if (m_Interactor.gameObject.activeSelf != hasHand)
                m_Interactor.gameObject.SetActive(hasHand);
            if (!hasHand)
            {
                EndPinch();
                m_Aims.Clear();
                return;
            }
            if (tracked)
                UpdatePinch(now);
        }

        void UpdatePinch(float now)
        {
            var pinched = m_Feed.IsPinching;
            if (pinched == m_Pinched)
                return;
            if (!pinched)
            {
                // EndPinch clears the state itself, and has to see the pinch still on to record the
                // frame it ended on: that frame is what XRI reads as the select completing, and
                // without it a held object stays held until the interactor is turned off.
                EndPinch();
                return;
            }

            m_Pinched = true;
            m_PinchFrame = Time.frameCount;
            // Aim with the ray from before the fingers closed, and keep that turn while it holds.
            if (m_Aims.TryGetAt(now - k_AimLookbackSeconds, out var before))
            {
                var line = new Ray(m_HandPoint.position, m_HandPoint.forward);
                m_AimTurn = Quaternion.FromToRotation(line.direction, before.direction) * m_AimTurn;
                m_HandPoint.rotation = Quaternion.LookRotation(before.direction, m_Gaze.transform.up);
            }

            // PinchRouter decides what the pinch does. Grabbing is the interactor's own: XRI reads it from
            // this as its select, just after this runs.
            var route = PinchRouter.Begin(PinchSource.Hand, m_Gaze, FindGrabbable() != null);
            m_Grabbing = route == PinchRoute.Grab;
            m_Claimed = route == PinchRoute.Demo;
        }

        void EndPinch()
        {
            if (m_Pinched)
                m_ReleaseFrame = Time.frameCount;
            m_Pinched = false;
            m_Grabbing = false;
            if (m_Claimed)
            {
                m_Claimed = false;
                PinchRouter.End();
            }
        }

        // What the interactor is on that could be picked up, or null. The hovers are the interaction
        // manager's from the frame before, a sixtieth of a second the hand barely moves in.
        XRGrabInteractable FindGrabbable()
        {
            if (!IsActive)
                return null;
            var hovered = m_Interactor.interactablesHovered;
            for (var i = 0; i < hovered.Count; i++)
            {
                if (hovered[i] is XRGrabInteractable grabbable)
                    return grabbable;
            }
            return null;
        }

        // The interactor's select input: a pinch, but only one that found something to take. A pinch
        // anywhere else has already gone to the gaze, and XRI must not select as well.
        public bool ReadIsPerformed() => m_Pinched && m_Grabbing;

        public bool ReadWasPerformedThisFrame() => m_PinchFrame == Time.frameCount && m_Grabbing;

        public bool ReadWasCompletedThisFrame() => m_ReleaseFrame == Time.frameCount;

        public float ReadValue() => m_Pinched && m_Grabbing ? 1f : 0f;

        public bool TryReadValue(out float value)
        {
            value = ReadValue();
            return true;
        }
    }
}
