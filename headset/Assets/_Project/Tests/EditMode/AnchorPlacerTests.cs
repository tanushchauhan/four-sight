using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    // The head is at the origin.
    public class AnchorPlacerTests
    {
        [Test]
        public void OnATable_StandsUpAndFacesTheHead()
        {
            var point = new Vector3(0f, -0.8f, 1f);
            var pose = AnchorPlacer.PlacementPose(point, Vector3.up, Vector3.zero);

            AssertClose(point, pose.position);
            AssertClose(Vector3.up, pose.up);
            AssertClose(Vector3.back, pose.forward);
        }

        [Test]
        public void OnAWall_PointsOutOfItAndFacesUpTowardTheHead()
        {
            // A wall ahead, facing the head, with the point below eye level.
            var pose = AnchorPlacer.PlacementPose(new Vector3(0f, -0.5f, 2f), Vector3.back, Vector3.zero);

            AssertClose(Vector3.back, pose.up);
            AssertClose(Vector3.up, pose.forward);
        }

        [Test]
        public void StraightBelowTheHead_StillLiesAlongTheSurface()
        {
            var pose = AnchorPlacer.PlacementPose(new Vector3(0f, -1.6f, 0f), Vector3.up * 3f, Vector3.zero);

            AssertClose(Vector3.up, pose.up);
            Assert.AreEqual(0f, Vector3.Dot(pose.forward, Vector3.up), 1e-5f);
        }

        static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.That((actual - expected).magnitude, Is.LessThan(1e-5f), $"expected {expected}, got {actual}");
    }
}
