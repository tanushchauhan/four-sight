using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.ProviderImplementation;

namespace CardboardXR.Hands
{
    // Hands for XR Hands, and through it the XR Interaction Toolkit, from the camera-based tracking:
    // the one hand HandTracker follows, as the left or right hand, with the pointing, grip, pinch and
    // poke poses and the pinch that XRI's hand interactors read through XRHandDevice. HandSubsystemFeed
    // creates the subsystem and supplies the hand. No XR loader is involved: AR Foundation's is the
    // only one.
    public class CardboardHandProvider : XRHandSubsystemProvider
    {
        public const string Id = "CardboardXR-Hands";

        // Where the hand comes from, while a HandSubsystemFeed is enabled.
        internal static HandSubsystemFeed Feed { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() => XRHandSubsystemDescriptor.Register(new XRHandSubsystemDescriptor.Cinfo
        {
            id = Id,
            providerType = typeof(CardboardHandProvider),
            supportsAimPose = true,
            supportsGripPose = true,
            supportsPinchPose = true,
            supportsPinchValue = true,
            supportsPokePose = true,
        });

        public override void Start() { }

        public override void Stop() { }

        public override void Destroy() { }

        // Every joint: the ones MediaPipe doesn't track are placed between the ones it does (see
        // HandSkeleton).
        public override void GetHandLayout(NativeArray<bool> handJointsInLayout)
        {
            for (var i = 0; i < handJointsInLayout.Length; i++)
                handJointsInLayout[i] = true;
        }

        // A hand left out of the returned flags is reported as not tracked.
        public override XRHandSubsystem.UpdateSuccessFlags TryUpdateHands(XRHandSubsystem.UpdateType updateType,
            ref Pose leftHandRootPose, NativeArray<XRHandJoint> leftHandJoints,
            ref Pose rightHandRootPose, NativeArray<XRHandJoint> rightHandJoints)
        {
            if (Feed == null || !Feed.TryGetJoints(out var handedness, out var joints))
                return XRHandSubsystem.UpdateSuccessFlags.None;
            if (handedness == Handedness.Left)
            {
                leftHandRootPose = Fill(handedness, joints, leftHandJoints);
                return XRHandSubsystem.UpdateSuccessFlags.LeftHandRootPose | XRHandSubsystem.UpdateSuccessFlags.LeftHandJoints;
            }
            rightHandRootPose = Fill(handedness, joints, rightHandJoints);
            return XRHandSubsystem.UpdateSuccessFlags.RightHandRootPose | XRHandSubsystem.UpdateSuccessFlags.RightHandJoints;
        }

        // Copies the joints in and returns the root pose, which is the wrist's.
        static Pose Fill(Handedness handedness, Pose[] joints, NativeArray<XRHandJoint> output)
        {
            for (var i = 0; i < output.Length && i < joints.Length; i++)
            {
                output[i] = XRHandProviderUtility.CreateJoint(handedness, XRHandJointTrackingState.Pose,
                    XRHandJointIDUtility.FromIndex(i), joints[i]);
            }
            return joints[HandSkeleton.Wrist];
        }

        public override bool canSurfaceCommonPoseData => true;

        public override bool TryGetAimPose(Handedness handedness, out Pose aimPose) =>
            TryGetPose(handedness, HandSubsystemFeed.PoseKind.Aim, out aimPose);

        public override bool TryGetGripPose(Handedness handedness, out Pose gripPose) =>
            TryGetPose(handedness, HandSubsystemFeed.PoseKind.Grip, out gripPose);

        public override bool TryGetPinchPose(Handedness handedness, out Pose pinchPose) =>
            TryGetPose(handedness, HandSubsystemFeed.PoseKind.Pinch, out pinchPose);

        public override bool TryGetPokePose(Handedness handedness, out Pose pokePose) =>
            TryGetPose(handedness, HandSubsystemFeed.PoseKind.Poke, out pokePose);

        public override bool TryGetPinchValue(Handedness handedness, out float pinchValue)
        {
            pinchValue = 0f;
            return Feed != null && Feed.TryGetPinch(handedness, out pinchValue, out _);
        }

        public override bool TryGetPinchTouchedState(Handedness handedness, out bool isPinched)
        {
            isPinched = false;
            return Feed != null && Feed.TryGetPinch(handedness, out _, out isPinched);
        }

        static bool TryGetPose(Handedness handedness, HandSubsystemFeed.PoseKind kind, out Pose pose)
        {
            pose = Pose.identity;
            return Feed != null && Feed.TryGetPose(handedness, kind, out pose);
        }
    }
}
