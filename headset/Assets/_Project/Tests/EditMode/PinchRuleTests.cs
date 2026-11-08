using CardboardXR.Hands;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class PinchRuleTests
    {
        const float k_Open = 1.9f;
        const float k_Pinched = 0.23f;

        static PinchRule Steady()
        {
            var rule = new PinchRule();
            for (var i = 0; i < PinchSensor.k_SteadyResults; i++)
                rule.Update(true, k_Open);
            return rule;
        }

        [Test]
        public void ClosingFingertipsOnASteadyHandPinches()
        {
            var rule = Steady();
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));
            Assert.IsTrue(rule.IsPinched);
        }

        [Test]
        public void AHandJustArrivedDoesNotPinchYet()
        {
            var rule = new PinchRule();
            for (var i = 0; i < PinchSensor.k_SteadyResults; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
            // Held pinched long enough, it counts.
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));
        }

        [Test]
        public void OnePinchClicksOnceUntilTheFingersOpen()
        {
            var rule = Steady();
            rule.Update(true, k_Pinched);
            // In between the thresholds it stays pinched: no second click from jitter.
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, PinchSensor.k_CloseRatio + 0.1f));
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
            Assert.AreEqual(PinchRule.Change.Opened, rule.Update(true, k_Open));
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));
        }

        [Test]
        public void LosingTheHandOpensAndStartsTheWaitAgain()
        {
            var rule = Steady();
            rule.Update(true, k_Pinched);
            Assert.AreEqual(PinchRule.Change.Opened, rule.Update(false, 0f));
            Assert.IsFalse(rule.IsPinched);
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
        }
    }
}
