using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Configuration;
using UnityEngine.XR.Hands.ProviderImplementation;

namespace CardboardXR.Hands
{
    // Runs the XR Hands subsystem for the camera-tracked hand (CardboardHandProvider) and supplies it:
    // HandTracker's joints in the XR Origin's tracking space, where XR Hands' poses are, as the left or
    // right hand by the landmarker's handedness; the pointing ray, from the eyes through the point
    // between the thumb and index fingertips, as the aim pose; and the pinch. XR Hands updates the
    // hand early in each frame and again just before rendering, and XRHandDevice passes it on to
    // input actions. Put it on the XR Origin. It runs after HandTracker and before HandInteractorRig
    // and XRI's interaction manager, which use its pinch. Logs [Hands].
    [DefaultExecutionOrder(-115)]
    public class HandSubsystemFeed : MonoBehaviour
    {
        internal enum PoseKind { Aim, Grip, Pinch, Poke }

        // The handedness is smoothed over results, and the hand only changes sides when the smoothed
        // chance of a right hand is this far past even, so a wavering reading doesn't swap it.
        const float k_HandednessRate = 0.3f;
        const float k_SideChangeMargin = 0.2f;
        const float k_StatusSeconds = 5f;

        [SerializeField] HandTracker m_Tracker;
        [Tooltip("The XR Origin's camera offset, which XR Hands' poses are relative to; this object if unset.")]
        [SerializeField] Transform m_TrackingSpace;
        [Tooltip("The eyes: the pointing ray runs from them through the point between the thumb and index fingertips.")]
        [SerializeField] Transform m_Eyes;
        // From the pinch study: fingertips about 2-3 cm apart pinched and 9.5-10.4 cm open.
        [Tooltip("Fingertips closer than this, in meters, start a pinch.")]
        [SerializeField] float m_PinchDistance = 0.05f;
        [Tooltip("Fingertips farther apart than this, in meters, end a pinch.")]
        [SerializeField] float m_ReleaseDistance = 0.07f;
        [Tooltip("Fingertips at least this far apart, in meters, give a pinch value of 0.")]
        [SerializeField] float m_OpenDistance = 0.09f;
        [Tooltip("Fingertips this far apart in the image, over the palm's width there, end a pinch " +
                 "whatever the hand's 3D shape says. The pinch study measured about 0.23 pinched and " +
                 "1.8 to 2.1 open.")]
        [SerializeField] float m_OpenRatio = 1f;

        // Frames where the image gap read open while the fitting 3D shape held the pinch, for the
        // status line.
        int m_IgnoredOpens;

        static readonly List<XRHandSubsystemDescriptor> s_Descriptors = new();

        XRHandSubsystem m_Subsystem;
        XRHandProviderUtility.SubsystemUpdater m_Updater;
        PinchDetector m_Pinch;
        readonly HandTurnGate m_Turn = new();
        // A pinch only ends on k_OpenResults tracker results in a row that read the fingers open, with
        // the hand at least k_EdgeMargin of the image from its edge: telling letting go from the hand
        // leaving the camera's view, where most losses happen (0-10% from the edge in the logs; false
        // opens read at most 2%, so 3% holds back few real releases).
        const int k_OpenResults = 2;
        // Longer than HandInteractorRig's m_LostSeconds (0.25 s).
        const float k_PinchKeptSeconds = 0.3f;
        float m_LostSince = -1f;
        const float k_EdgeMargin = 0.03f;
        int m_OpenResults;
        int m_PinchResult = -1;
        bool m_LoggedEdge;
        // Whether a pinch was held back during this turn, to log it once.
        bool m_LoggedTurn;
        readonly Vector3[] m_Landmarks = new Vector3[HandGeometry.JointCount];
        readonly Pose[] m_Joints = new Pose[HandSkeleton.JointCount];
        bool m_HasHand;
        bool m_IsRight;
        // The smoothed chance that the hand is a right hand.
        float m_RightChance;
        int m_LastResult;
        float m_StatusStart;
        // XR Hands' updates since the last status line, early in the frame and just before rendering.
        int m_EarlyUpdates;
        int m_BeforeRenderUpdates;


        // Which hand is being reported, or Invalid while none is.
        public Handedness TrackedHandedness => !m_HasHand ? Handedness.Invalid : m_IsRight ? Handedness.Right : Handedness.Left;

        public bool IsPinching => m_Pinch != null && m_Pinch.IsPinched;

        // From 0 with the fingertips open to 1 once they pinch; 0 while there's no hand.
        public float PinchValue => !m_HasHand ? 0f
            : IsPinching ? 1f
            : Mathf.InverseLerp(m_OpenDistance, m_PinchDistance, m_Tracker.PinchDistance);

        Transform TrackingSpace => m_TrackingSpace != null ? m_TrackingSpace : transform;

        void Awake()
        {
            m_Pinch = new PinchDetector(m_PinchDistance, m_ReleaseDistance);
            SubsystemManager.GetSubsystemDescriptors(s_Descriptors);
            foreach (var descriptor in s_Descriptors)
            {
                if (descriptor.id == CardboardHandProvider.Id)
                {
                    m_Subsystem = descriptor.Create();
                    break;
                }
            }
            if (m_Subsystem == null)
            {
                Debug.LogError($"[Hands] XR Hands has no {CardboardHandProvider.Id} subsystem");
                return;
            }
            // XRHandDevice takes its aim, pinch, poke and grip from the provider (see CardboardHandProvider)
            // only with common gestures. By default it works its own out from the joints and leaves the
            // aim and pinch empty.
            m_Subsystem.UpdateHandsConfiguration(new XRHandSubsystemConfiguration
            {
                xrHandDevicePoseSource = XRHandDevicePoseSource.CommonGestures,
            });
            m_Updater = new XRHandProviderUtility.SubsystemUpdater(m_Subsystem);
        }

        void OnEnable()
        {
            if (m_Subsystem == null)
                return;
            CardboardHandProvider.Feed = this;
            m_Subsystem.Start();
            m_Updater.Start();
            m_Subsystem.updatedHands += OnUpdatedHands;
            m_StatusStart = Time.unscaledTime;
            Debug.Log("[Hands] XR Hands running");
        }

        void OnDisable()
        {
            if (m_Subsystem == null)
                return;
            m_Subsystem.updatedHands -= OnUpdatedHands;
            m_Updater.Stop();
            m_Subsystem.Stop();
            if (CardboardHandProvider.Feed == this)
                CardboardHandProvider.Feed = null;
            m_HasHand = false;
        }

        void OnDestroy()
        {
            m_Updater?.Destroy();
            m_Subsystem?.Destroy();
            m_Subsystem = null;
        }

        void Update()
        {
            if (!m_Tracker.IsTracking)
            {
                if (m_HasHand)
                    Debug.Log($"[Hands] XR Hands lost the {SideName} hand");
                m_HasHand = false;
                // A pinch outlasts a brief miss: dropping it at once read as the fingers opening when the
                // hand came back, which let go of (and threw) what it held. Past HandInteractorRig's grace
                // the hand counts as lost, and the pinch goes.
                if (m_LostSince < 0f)
                    m_LostSince = Time.unscaledTime;
                if (Time.unscaledTime - m_LostSince > k_PinchKeptSeconds)
                    m_Pinch.Reset();
                m_Turn.Reset();
                return;
            }
            m_LostSince = -1f;

            if (m_Tracker.ResultCount != m_LastResult)
            {
                m_LastResult = m_Tracker.ResultCount;
                UpdateSide();
                var landmarks = m_Tracker.ImageLandmarks;
                m_Turn.Update(PalmGesture.Facing(landmarks[PalmGesture.k_Wrist], landmarks[PalmGesture.k_IndexKnuckle],
                    landmarks[PalmGesture.k_PinkyKnuckle], m_Tracker.HandednessScore >= 0.5f), Time.unscaledTime);
            }
            UpdatePinch();
            LogStatus();
        }

        // The fingertips' gap in the model's 3D hand shape decides the pinch, except that closed
        // fingers hide each other and collapse that shape: the tracker then reuses the last one it
        // trusted, whose fingertips are still together, and a hand that has opened goes on reading as
        // pinched. The gap in the image over the palm's width there comes straight from the 2D
        // landmarks, which survive that, so it ends a pinch on its own. It only ever opens a pinch,
        // never closes one, since it's the noisier of the two when the hand is edge-on.
        //
        // And only after a collapse. While the shape still fits, it's the better witness: pressing
        // the fingertips together hides one behind the other, and on the iPhone the image then
        // threw one away, reading 1.0 to 1.6 palm-widths open for up to 0.8 s at a time, while the
        // 3D shape kept them together. Every pinch lost dragging a cube was that.
        //
        // A pinch doesn't start while the hand is turning over (HandTurnGate): passing edge-on, the
        // shape squeezes the fingertips together for a few frames.
        void UpdatePinch()
        {
            var newResult = m_Tracker.ResultCount != m_PinchResult;
            m_PinchResult = m_Tracker.ResultCount;
            if (!m_Pinch.IsPinched)
            {
                m_OpenResults = 0;
                if (!m_Tracker.ShapeFits)
                    return;
                var wouldClose = m_Tracker.PinchDistance < m_PinchDistance;
                if (wouldClose && m_Turn.IsTurning)
                {
                    if (!m_LoggedTurn)
                        Debug.Log($"[Hands] Fingertips {m_Tracker.PinchDistance * 100f:F1} cm apart while the hand turned over; not a pinch");
                    m_LoggedTurn = true;
                }
                else
                {
                    m_Pinch.Update(m_Tracker.PinchDistance);
                }
                if (!m_Turn.IsTurning)
                    m_LoggedTurn = false;
                return;
            }

            // Pinched: whether this result says the fingers have opened, by the 3D shape while it fits,
            // by the image's gap once it has collapsed.
            var opens = m_Tracker.ShapeFits ? m_Tracker.PinchDistance > m_ReleaseDistance : m_Tracker.PinchRatio > m_OpenRatio;
            if (m_Tracker.ShapeFits && !opens && m_Tracker.PinchRatio > m_OpenRatio)
                m_IgnoredOpens++;
            if (!opens)
            {
                m_OpenResults = 0;
                m_LoggedEdge = false;
                return;
            }
            if (!newResult)
                return;
            // Near the image's edge the fingers are often partly out of view, and read open as the hand
            // leaves it: that's a hand being lost, not let go, so the pinch holds. Either the hand comes
            // back into view or it's lost (and what it held stays where it was: CubePhysics).
            var edge = m_Tracker.EdgeDistance;
            if (edge < k_EdgeMargin)
            {
                if (!m_LoggedEdge)
                    Debug.Log($"[Hands] Fingers read open {edge * 100f:F0}% from the image's edge; holding the pinch");
                m_LoggedEdge = true;
                m_OpenResults = 0;
                return;
            }
            // And only once it lasts: a single result can be a glitch.
            if (++m_OpenResults < k_OpenResults)
                return;
            if (!m_Tracker.ShapeFits)
                Debug.Log($"[Hands] Fingers opened to {m_Tracker.PinchRatio:F2} of the palm's width; ending the pinch, " +
                          "whose 3D shape had collapsed");
            m_Pinch.Reset();
            m_OpenResults = 0;
        }

        void UpdateSide()
        {
            // MediaPipe documents its handedness as if the image were mirrored, as a selfie camera's
            // is, which would swap the rear camera's hands. On the phone it reads them the right way
            // round: swapping them put the other hand's mesh, turned over, on the real hand.
            var right = m_Tracker.HandednessScore;
            if (!m_HasHand)
            {
                m_HasHand = true;
                m_RightChance = right;
                m_IsRight = right >= 0.5f;
                Debug.Log($"[Hands] XR Hands found a {SideName} hand (chance of a right hand {right:F2})");
                return;
            }
            m_RightChance = Mathf.Lerp(m_RightChance, right, k_HandednessRate);
            if (m_IsRight ? m_RightChance < 0.5f - k_SideChangeMargin : m_RightChance > 0.5f + k_SideChangeMargin)
            {
                m_IsRight = !m_IsRight;
                Debug.Log($"[Hands] XR Hands now reads it as a {SideName} hand (chance of a right hand {m_RightChance:F2})");
            }
        }

        string SideName => m_IsRight ? "right" : "left";

        // The hand's joints in the tracking space, for the provider; false while there's no hand.
        internal bool TryGetJoints(out Handedness handedness, out Pose[] joints)
        {
            handedness = TrackedHandedness;
            joints = m_Joints;
            if (!m_HasHand || !m_Tracker.IsTracking)
                return false;
            var space = TrackingSpace;
            var world = m_Tracker.Joints;
            for (var i = 0; i < m_Landmarks.Length; i++)
                m_Landmarks[i] = space.InverseTransformPoint(world[i]);
            HandSkeleton.Build(m_Landmarks, m_IsRight, m_Joints);
            return true;
        }

        // The provider asks for these after the joints (TryGetJoints), in the same update.
        internal bool TryGetPose(Handedness handedness, PoseKind kind, out Pose pose)
        {
            pose = Pose.identity;
            if (!m_HasHand || handedness != TrackedHandedness)
                return false;
            switch (kind)
            {
                case PoseKind.Grip:
                    pose = m_Joints[HandSkeleton.Palm];
                    break;
                case PoseKind.Poke:
                    pose = m_Joints[HandSkeleton.IndexTip];
                    break;
                default:
                    var space = TrackingSpace;
                    var fingertips = (m_Landmarks[HandGeometry.ThumbTip] + m_Landmarks[HandGeometry.IndexTip]) / 2f;
                    var eyes = space.InverseTransformPoint(m_Eyes.position);
                    pose = new Pose(fingertips, Quaternion.LookRotation(fingertips - eyes, space.InverseTransformDirection(m_Eyes.up)));
                    break;
            }
            return true;
        }

        // The pinch value runs from 0 with the fingertips open to 1 once they pinch.
        internal bool TryGetPinch(Handedness handedness, out float value, out bool pinched)
        {
            value = 0f;
            pinched = false;
            if (!m_HasHand || handedness != TrackedHandedness)
                return false;
            pinched = m_Pinch.IsPinched;
            value = PinchValue;
            return true;
        }

        // What XR Hands and its input device report, to check them against the tracker.
        void LogStatus()
        {
            if (Time.unscaledTime - m_StatusStart < k_StatusSeconds)
                return;
            m_StatusStart = Time.unscaledTime;
            var hand = m_IsRight ? m_Subsystem.rightHand : m_Subsystem.leftHand;
            var common = m_IsRight ? m_Subsystem.rightHandCommonGestures : m_Subsystem.leftHandCommonGestures;
            var device = m_IsRight ? XRHandDevice.rightHand : XRHandDevice.leftHand;
            var wrist = TrackingSpace.TransformPoint(hand.rootPose.position);
            var wristOff = Vector3.Distance(wrist, m_Tracker.Joints[0]);
            // The aim at each step on its way to input actions: this feed, XR Hands, its input device.
            var feedAim = TryGetPose(TrackedHandedness, PoseKind.Aim, out var aim) ? $"{aim.rotation.eulerAngles:F0}" : "none";
            var subsystemAim = common != null && common.TryGetAimPose(out var commonAim) ? $"{commonAim.rotation.eulerAngles:F0}" : "none";
            var status = $"[Hands] XR Hands {SideName} hand: tracked {hand.isTracked}, wrist {wristOff * 100f:F1} cm from the tracker's, " +
                         $"chance of a right hand {m_RightChance:F2}; {m_EarlyUpdates} early and {m_BeforeRenderUpdates} before-render " +
                         $"updates; aim from the feed {feedAim}, in XR Hands {subsystemAim}";
            if (device != null)
                status += $", on the device {device.aimRotation.ReadValue().eulerAngles:F0} (tracking {device.aimTrackingState.ReadValue()}, " +
                          $"device tracked {device.isTracked.isPressed}); device pinch {device.pinchValue.ReadValue():F2} " +
                          $"(touched {device.pinchTouched.isPressed})";
            else
                status += "; no XRHandDevice";
            status += $"; {m_IgnoredOpens} frames the image read open fingers while the 3D shape held the pinch";
            Debug.Log(status);
            m_EarlyUpdates = m_BeforeRenderUpdates = m_IgnoredOpens = 0;
        }

        void OnUpdatedHands(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType updateType)
        {
            if (updateType == XRHandSubsystem.UpdateType.Dynamic)
                m_EarlyUpdates++;
            else
                m_BeforeRenderUpdates++;
        }
    }
}
