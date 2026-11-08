using UnityEngine;

namespace CardboardXR.Hands
{
    // XR Hands' 26 joints from MediaPipe's 21 landmarks, in XR Hands' order (XRHandJointID.ToIndex):
    // the wrist, the palm, then the thumb's four joints and each finger's five, from base to tip.
    // MediaPipe has no palm or finger metacarpals, so the palm goes midway between the wrist and the
    // middle finger's knuckle, and each metacarpal a quarter of the way from the wrist to its knuckle.
    // Each joint looks along its bone toward the fingertip (+Z) with +Y out of the back of the hand,
    // as XR Hands' joints do in Unity's axes; a fingertip keeps the direction of the bone before it.
    // Which way the back of the hand faces depends on which hand it is: seen from the back of a right
    // hand, with its fingers up, the knuckles run from the index finger on the left to the little
    // finger on the right.
    public static class HandSkeleton
    {
        public const int JointCount = 26;
        public const int Wrist = 0;
        public const int Palm = 1;
        public const int IndexTip = 10;
        // Where the thumb's and the fingers' joints start, and how many each finger has.
        const int k_ThumbBase = 2;
        const int k_FingerBase = 6;
        const int k_FingerJoints = 5;
        // How far from the wrist to its knuckle a finger's metacarpal joint is placed.
        const float k_MetacarpalPosition = 0.25f;

        static readonly Vector3[] s_Chain = new Vector3[k_FingerJoints];

        // The joints' poses in the landmarks' space.
        public static void Build(Vector3[] landmarks, bool rightHand, Pose[] joints)
        {
            var wrist = landmarks[0];
            var middleKnuckle = landmarks[9];
            var along = middleKnuckle - wrist;
            var across = landmarks[17] - landmarks[5];
            var back = Vector3.Cross(along, across) * (rightHand ? 1f : -1f);
            if (back.sqrMagnitude < 1e-12f)
                back = Vector3.up;
            var handRotation = Look(along, back, Vector3.forward);
            joints[Wrist] = new Pose(wrist, handRotation);
            joints[Palm] = new Pose((wrist + middleKnuckle) / 2f, handRotation);

            for (var i = 0; i < 4; i++)
                s_Chain[i] = landmarks[1 + i];
            PlaceChain(s_Chain, 4, back, along, joints, k_ThumbBase);
            for (var finger = 0; finger < 4; finger++)
            {
                var knuckle = 5 + finger * 4;
                s_Chain[0] = Vector3.Lerp(wrist, landmarks[knuckle], k_MetacarpalPosition);
                for (var i = 0; i < 4; i++)
                    s_Chain[1 + i] = landmarks[knuckle + i];
                PlaceChain(s_Chain, k_FingerJoints, back, along, joints, k_FingerBase + finger * k_FingerJoints);
            }
        }

        static void PlaceChain(Vector3[] points, int count, Vector3 back, Vector3 fallback, Pose[] joints, int first)
        {
            var forward = fallback;
            for (var i = 0; i < count; i++)
            {
                if (i + 1 < count)
                {
                    var bone = points[i + 1] - points[i];
                    if (bone.sqrMagnitude > 1e-12f)
                        forward = bone;
                }
                joints[first + i] = new Pose(points[i], Look(forward, back, fallback));
            }
        }

        static Quaternion Look(Vector3 forward, Vector3 up, Vector3 fallback) =>
            Quaternion.LookRotation(forward.sqrMagnitude > 1e-12f ? forward : fallback, up);
    }
}
