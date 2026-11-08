using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class PalmCalibrationTests
    {
        static readonly Vector2 k_Focal = new(453f, 453f);
        static readonly Vector2 k_Principal = new(320f, 240f);

        // A hand-sized shape with some depth to it, so the fit has something to work with.
        static Vector3[] Shape()
        {
            var shape = new Vector3[HandGeometry.JointCount];
            for (var i = 0; i < shape.Length; i++)
                shape[i] = new Vector3(0.012f * (i % 5) - 0.024f, 0.015f * (i / 5) - 0.04f, 0.004f * (i * 7 % 5) - 0.008f);
            return shape;
        }

        static Vector2[] Project(Vector3[] shape, Vector3 position)
        {
            var image = new Vector2[shape.Length];
            for (var i = 0; i < shape.Length; i++)
            {
                var p = shape[i] + position;
                image[i] = new Vector2(k_Focal.x * p.x / p.z + k_Principal.x, k_Focal.y * p.y / p.z + k_Principal.y);
            }
            return image;
        }

        static float PalmDepth(Vector3[] shape, Vector3 position)
        {
            var joints = new Vector3[shape.Length];
            for (var i = 0; i < shape.Length; i++)
                joints[i] = shape[i] + position;
            return HandGeometry.PalmDepth(joints);
        }

        // The premise of calibrating from depth: assume the hand is k times its real size and the fit
        // puts it k times as far away, since both make the same image.
        [Test]
        public void FitDepth_ScalesWithTheAssumedSize()
        {
            var shape = Shape();
            var position = new Vector3(0.02f, -0.01f, 0.3f);
            var image = Project(shape, position);
            var scaled = new Vector3[shape.Length];
            for (var i = 0; i < shape.Length; i++)
                scaled[i] = shape[i] * 1.1f;

            var fitted = HandGeometry.FitPosition(image, scaled, HandGeometry.Palm, k_Focal, k_Principal, out var error);
            Assert.Less(error, 0.01f);
            Assert.AreEqual(1.1f * PalmDepth(shape, position), PalmDepth(scaled, fitted), 1e-4f);
        }

        // One measured depth recovers the real palm size from a wrong assumed one.
        [Test]
        public void MeasuredDepth_RecoversTheRealPalmSize()
        {
            var shape = Shape();
            var position = new Vector3(-0.03f, 0.02f, 0.28f);
            var image = Project(shape, position);
            var realSize = HandGeometry.PalmSize(shape);

            foreach (var assumed in new[] { 0.8f, 1.1f, 1.3f })
            {
                var scaled = new Vector3[shape.Length];
                for (var i = 0; i < shape.Length; i++)
                    scaled[i] = shape[i] * assumed;
                var fitted = HandGeometry.FitPosition(image, scaled, HandGeometry.Palm, k_Focal, k_Principal, out _);
                var recovered = HandGeometry.PalmSizeForDepth(HandGeometry.PalmSize(scaled),
                    PalmDepth(scaled, fitted), PalmDepth(shape, position));
                Assert.AreEqual(realSize, recovered, realSize * 1e-3f, $"assumed {assumed}x");
            }
        }

        // The 30 cm ruler hold on 2026-09-24: a 5.8 cm palm put the hand at 27.5 cm, and the LiDAR saw
        // skin at 30 cm, so joints at 31: the real palm is about 6.5 cm.
        [Test]
        public void RulerHold_At30cm_ImpliesA6_5cmPalm()
        {
            Assert.AreEqual(0.0654f, HandGeometry.PalmSizeForDepth(0.058f, 0.275f, 0.30f + 0.01f), 1e-4f);
        }
    }
}

namespace CardboardXR.Stereo.Tests
{
    public class HandReachTests
    {
        // A hand at full stretch in front of a phone held at the face.
        [Test]
        public void ArmsLength_IsKept()
        {
            Assert.IsTrue(CardboardXR.Hands.HandTracker.WithinReach(0.65f, float.NaN));
            Assert.IsTrue(CardboardXR.Hands.HandTracker.WithinReach(0.60f, 0.65f));
        }

        // The strays of 2026-09-24, with and without a depth sensor.
        [Test]
        public void StrayDetections_AreDropped()
        {
            Assert.IsFalse(CardboardXR.Hands.HandTracker.WithinReach(1.49f, 1.14f));
            Assert.IsFalse(CardboardXR.Hands.HandTracker.WithinReach(1.68f, float.NaN));
            Assert.IsFalse(CardboardXR.Hands.HandTracker.WithinReach(0.90f, float.NaN));
        }

        // The sensor decides where it has a reading, in either direction: a real hand whose estimate
        // went wrong stays, and something small and far that the estimate put close goes.
        [Test]
        public void Measurement_OverrulesTheEstimate()
        {
            Assert.IsTrue(CardboardXR.Hands.HandTracker.WithinReach(1.5f, 0.45f));
            Assert.IsFalse(CardboardXR.Hands.HandTracker.WithinReach(0.4f, 1.2f));
        }
    }
}
