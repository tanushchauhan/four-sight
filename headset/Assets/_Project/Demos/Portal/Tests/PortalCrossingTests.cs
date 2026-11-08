using CardboardXR.Demos.Portal;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Portal.Tests
{
    public class PortalCrossingTests
    {
        const float k_Half = PortalWorld.k_DoorWidth / 2f;
        const float k_Height = PortalWorld.k_DoorHeight;

        static int Cross(Vector3 before, Vector3 after) => PortalWorld.Crossing(before, after, k_Half, k_Height);

        [Test]
        public void WalkingThroughTheDoorwayGoesInAndBackOut()
        {
            Assert.AreEqual(1, Cross(new Vector3(0f, 1.6f, -0.05f), new Vector3(0f, 1.6f, 0.05f)));
            Assert.AreEqual(-1, Cross(new Vector3(0.1f, 1.6f, 0.05f), new Vector3(0.1f, 1.6f, -0.05f)));
        }

        [Test]
        public void StayingOnOneSideIsNoCrossing()
        {
            Assert.AreEqual(0, Cross(new Vector3(0f, 1.6f, -1f), new Vector3(0f, 1.6f, -0.2f)));
            Assert.AreEqual(0, Cross(new Vector3(0f, 1.6f, 0.2f), new Vector3(0f, 1.6f, 1f)));
        }

        [Test]
        public void GoingAroundTheFrameIsNoCrossing()
        {
            Assert.AreEqual(0, Cross(new Vector3(0.9f, 1.6f, -0.05f), new Vector3(0.9f, 1.6f, 0.05f)));
            Assert.AreEqual(0, Cross(new Vector3(-0.8f, 1.6f, 0.05f), new Vector3(-0.8f, 1.6f, -0.05f)));
            // Over the top, as a tall head leaning over the lintel would.
            Assert.AreEqual(0, Cross(new Vector3(0f, 2.4f, -0.05f), new Vector3(0f, 2.4f, 0.05f)));
        }

        [Test]
        public void ACrossingIsJudgedWhereItMeetsTheDoorway()
        {
            // A big step: out of the frame before and after, but through it on the way.
            Assert.AreEqual(1, Cross(new Vector3(-1f, 1.6f, -0.5f), new Vector3(1f, 1.6f, 0.5f)));
            // Clipping the frame's edge counts, within the margin.
            Assert.AreEqual(1, Cross(new Vector3(k_Half + 0.05f, 1.6f, -0.05f), new Vector3(k_Half + 0.05f, 1.6f, 0.05f)));
        }
    }
}
