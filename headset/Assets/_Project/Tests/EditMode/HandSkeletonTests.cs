using System.Linq;
using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace CardboardXR.Stereo.Tests
{
    public class HandSkeletonTests
    {
        // A flat right hand, palm down with the fingers ahead (+z) and the thumb to the left (-x), in
        // MediaPipe's order, in meters.
        static Vector3[] RightHandPalmDown() => new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(-0.025f, 0f, 0.025f), new Vector3(-0.045f, 0f, 0.045f), new Vector3(-0.06f, 0f, 0.065f), new Vector3(-0.07f, 0f, 0.085f),
            new Vector3(-0.03f, 0f, 0.09f), new Vector3(-0.032f, 0f, 0.13f), new Vector3(-0.033f, 0f, 0.155f), new Vector3(-0.034f, 0f, 0.175f),
            new Vector3(-0.01f, 0f, 0.095f), new Vector3(-0.01f, 0f, 0.14f), new Vector3(-0.01f, 0f, 0.168f), new Vector3(-0.01f, 0f, 0.19f),
            new Vector3(0.01f, 0f, 0.09f), new Vector3(0.012f, 0f, 0.13f), new Vector3(0.013f, 0f, 0.156f), new Vector3(0.014f, 0f, 0.178f),
            new Vector3(0.028f, 0f, 0.08f), new Vector3(0.032f, 0f, 0.11f), new Vector3(0.034f, 0f, 0.128f), new Vector3(0.036f, 0f, 0.145f),
        };

        static Pose[] Build(Vector3[] landmarks, bool rightHand)
        {
            var joints = new Pose[HandSkeleton.JointCount];
            HandSkeleton.Build(landmarks, rightHand, joints);
            return joints;
        }

        static Pose Joint(Pose[] joints, XRHandJointID id) => joints[id.ToIndex()];

        static void AssertDirection(Vector3 expected, Vector3 actual, string what) =>
            Assert.That(Vector3.Angle(expected, actual), Is.LessThan(1f), $"{what}: expected {expected}, got {actual}");

        [Test]
        public void Layout_MatchesXRHands()
        {
            Assert.That(HandSkeleton.JointCount, Is.EqualTo(XRHandJointID.EndMarker.ToIndex()));
            Assert.That(HandSkeleton.Wrist, Is.EqualTo(XRHandJointID.Wrist.ToIndex()));
            Assert.That(HandSkeleton.Palm, Is.EqualTo(XRHandJointID.Palm.ToIndex()));
            Assert.That(HandSkeleton.IndexTip, Is.EqualTo(XRHandJointID.IndexTip.ToIndex()));
        }

        [Test]
        public void Joints_SitOnTheirLandmarks()
        {
            var landmarks = RightHandPalmDown();
            var joints = Build(landmarks, true);
            Assert.That(Joint(joints, XRHandJointID.Wrist).position, Is.EqualTo(landmarks[0]));
            Assert.That(Joint(joints, XRHandJointID.ThumbMetacarpal).position, Is.EqualTo(landmarks[1]));
            Assert.That(Joint(joints, XRHandJointID.ThumbTip).position, Is.EqualTo(landmarks[4]));
            Assert.That(Joint(joints, XRHandJointID.IndexProximal).position, Is.EqualTo(landmarks[5]));
            Assert.That(Joint(joints, XRHandJointID.IndexTip).position, Is.EqualTo(landmarks[8]));
            Assert.That(Joint(joints, XRHandJointID.MiddleIntermediate).position, Is.EqualTo(landmarks[10]));
            Assert.That(Joint(joints, XRHandJointID.RingDistal).position, Is.EqualTo(landmarks[15]));
            Assert.That(Joint(joints, XRHandJointID.LittleProximal).position, Is.EqualTo(landmarks[17]));
            Assert.That(Joint(joints, XRHandJointID.LittleTip).position, Is.EqualTo(landmarks[20]));
            // The joints MediaPipe doesn't have lie between the ones it does.
            Assert.That(Joint(joints, XRHandJointID.Palm).position, Is.EqualTo((landmarks[0] + landmarks[9]) / 2f));
            Assert.That(Joint(joints, XRHandJointID.IndexMetacarpal).position, Is.EqualTo(Vector3.Lerp(landmarks[0], landmarks[5], 0.25f)));
        }

        [Test]
        public void Joints_LookAlongTheirBones()
        {
            var landmarks = RightHandPalmDown();
            var joints = Build(landmarks, true);
            AssertDirection(landmarks[9] - landmarks[0], Joint(joints, XRHandJointID.Wrist).rotation * Vector3.forward, "wrist");
            AssertDirection(landmarks[6] - landmarks[5], Joint(joints, XRHandJointID.IndexProximal).rotation * Vector3.forward, "index proximal");
            AssertDirection(landmarks[3] - landmarks[2], Joint(joints, XRHandJointID.ThumbProximal).rotation * Vector3.forward, "thumb proximal");
            // A tip looks the way its last bone does.
            AssertDirection(landmarks[8] - landmarks[7], Joint(joints, XRHandJointID.IndexTip).rotation * Vector3.forward, "index tip");
        }

        [Test]
        public void RightHandPalmDown_BackFacesUp()
        {
            var joints = Build(RightHandPalmDown(), true);
            AssertDirection(Vector3.up, Joint(joints, XRHandJointID.Wrist).rotation * Vector3.up, "wrist");
            AssertDirection(Vector3.up, Joint(joints, XRHandJointID.MiddleProximal).rotation * Vector3.up, "middle proximal");
        }

        [Test]
        public void LeftHandPalmDown_BackFacesUp()
        {
            // The right hand mirrored left to right is a left hand, still palm down.
            var landmarks = RightHandPalmDown().Select(p => new Vector3(-p.x, p.y, p.z)).ToArray();
            var joints = Build(landmarks, false);
            AssertDirection(Vector3.up, Joint(joints, XRHandJointID.Wrist).rotation * Vector3.up, "wrist");
        }

        [Test]
        public void RightHandPalmUp_BackFacesDown()
        {
            // Turned over about the fingers' direction: palm up, thumb to the right.
            var landmarks = RightHandPalmDown().Select(p => new Vector3(-p.x, -p.y, p.z)).ToArray();
            var joints = Build(landmarks, true);
            AssertDirection(Vector3.down, Joint(joints, XRHandJointID.Wrist).rotation * Vector3.up, "wrist");
        }
    }
}
