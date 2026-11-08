using System.Collections.Generic;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    // Heights are in session space with the eyes at 0.
    public class RoomBoundaryTests
    {
        [Test]
        public void PicksTheLowestLargeHorizontalPlane()
        {
            var planes = new List<(bool, float, float)>
            {
                (true, -0.8f, 1.5f),   // table top
                (true, -1.5f, 4f),     // floor
                (true, -1.65f, 0.2f),  // small patch, too little to trust
                (false, -1.7f, 6f),    // wall
            };
            Assert.AreEqual(-1.5f, RoomBoundary.PickFloor(planes, 0f, 0.5f, 0.9f));
        }

        [Test]
        public void NothingLowEnough_IsNoFloor()
        {
            var planes = new List<(bool, float, float)> { (true, -0.6f, 3f) };
            Assert.IsNull(RoomBoundary.PickFloor(planes, 0f, 0.5f, 0.9f));
        }
    }
}
