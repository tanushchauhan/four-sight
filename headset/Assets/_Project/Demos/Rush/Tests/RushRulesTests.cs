using CardboardXR.Demos.Rush;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Rush.Tests
{
    public class RushRulesTests
    {
        [Test]
        public void EverySpotIsInsideTheCamerasView()
        {
            // The camera covers 68 by 42 degrees; a spot plus the orb's radius must stay inside it at
            // the arrival distance, measured from the eyes (the camera sits close to them).
            var halfWidth = RushRules.k_ArrivalDistance * Mathf.Tan(34f * Mathf.Deg2Rad);
            var halfHeight = RushRules.k_ArrivalDistance * Mathf.Tan(21f * Mathf.Deg2Rad);
            var random = new System.Random(1);
            var previous = Vector2.zero;
            for (var i = 0; i < 500; i++)
            {
                var spot = RushRules.NextSpot(random, previous);
                var y = spot.y - RushRules.k_Drop;
                Assert.LessOrEqual(Mathf.Abs(spot.x) + RushRules.k_OrbRadius, halfWidth);
                Assert.LessOrEqual(Mathf.Abs(y) + RushRules.k_OrbRadius, halfHeight);
                previous = spot;
            }
        }

        [Test]
        public void ConsecutiveSpotsAreApart()
        {
            var random = new System.Random(2);
            var previous = RushRules.NextSpot(random, new Vector2(10f, 10f));
            for (var i = 0; i < 200; i++)
            {
                var spot = RushRules.NextSpot(random, previous);
                Assert.GreaterOrEqual(Vector2.Distance(spot, previous), RushRules.k_MinStep);
                previous = spot;
            }
        }

        [Test]
        public void JudgementDependsOnDistance()
        {
            Assert.AreEqual(Judgement.Perfect, RushRules.Judge(RushRules.k_ArrivalDistance));
            Assert.AreEqual(Judgement.Perfect, RushRules.Judge(RushRules.k_ArrivalDistance + RushRules.k_PerfectBand - 0.01f));
            Assert.AreEqual(Judgement.Good, RushRules.Judge(RushRules.k_ArrivalDistance + RushRules.k_PerfectBand + 0.05f));
            Assert.AreEqual(Judgement.Good, RushRules.Judge(RushRules.k_NearestHit + 0.01f));
            Assert.AreEqual(Judgement.None, RushRules.Judge(RushRules.k_FarthestHit + 0.1f));
            Assert.AreEqual(Judgement.None, RushRules.Judge(0.05f));
        }

        [Test]
        public void TouchNeedsAHandPointWithinReach()
        {
            var orb = new Vector3(0f, 0f, 0.45f);
            var across = RushRules.k_TouchAcross;
            Assert.IsTrue(RushRules.Touches(orb, new[] { orb + Vector3.right * (across - 0.005f) }));
            Assert.IsFalse(RushRules.Touches(orb, new[] { orb + Vector3.right * (across + 0.005f) }));
            Assert.IsTrue(RushRules.Touches(orb, new[] { Vector3.zero, orb + Vector3.up * 0.02f }));
            Assert.IsFalse(RushRules.Touches(orb, new Vector3[0]));
        }

        [Test]
        public void DepthIsForgivenMoreThanAcross()
        {
            var orb = new Vector3(0f, 0f, 0.45f);
            // A hand read 15 cm short of the orb, straight in front of it, still touches.
            Assert.IsTrue(RushRules.Touches(orb, new[] { orb + Vector3.back * 0.15f }));
            Assert.IsFalse(RushRules.Touches(orb, new[] { orb + Vector3.right * 0.15f }));
            Assert.IsFalse(RushRules.Touches(orb, new[] { orb + Vector3.back * (RushRules.k_TouchDepth + 0.01f) }));
            // Off to the side and short together: outside.
            Assert.IsFalse(RushRules.Touches(orb, new[] { orb + new Vector3(0.1f, 0f, -0.15f) }));
        }

        [Test]
        public void RoundGetsFasterAndCombosMultiply()
        {
            Assert.Greater(RushRules.Interval(0f), RushRules.Interval(1f));
            Assert.Less(RushRules.Speed(0f), RushRules.Speed(1f));
            Assert.AreEqual(RushRules.Interval(1f), RushRules.Interval(2f));
            Assert.AreEqual(RushRules.k_Perfect, RushRules.Points(Judgement.Perfect, 0));
            Assert.AreEqual(RushRules.k_Good * 2, RushRules.Points(Judgement.Good, 8));
            Assert.AreEqual(RushRules.k_Perfect * RushRules.k_MaxMultiplier, RushRules.Points(Judgement.Perfect, 1000));
            Assert.AreEqual(0, RushRules.Points(Judgement.None, 5));
        }
    }
}
