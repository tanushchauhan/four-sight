using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CardboardXR.Stereo.Tests
{
    public class CameraClockTests
    {
        const long k_Second = 1_000_000_000;
        const long k_Latency = 40_000_000;

        // Android: ARCore and the native sensors share the boot-time clock.
        [Test]
        public void SameClockAsTracker_PassesThrough()
        {
            var clock = new CameraClock();
            var trackerNow = 5_000 * k_Second;
            var systemNow = 123 * k_Second;
            Assert.IsTrue(clock.TryToTrackerClock(trackerNow - k_Latency, trackerNow, systemNow, out var converted));
            Assert.AreEqual(CameraClock.Base.Tracker, clock.Source);
            Assert.AreEqual(trackerNow - k_Latency, converted);
            Assert.AreEqual(k_Latency, clock.ArrivalAgeNs);
        }

        // iOS: ARKit stamps on the system clock, the Input System on its own, an hour apart here.
        [Test]
        public void SystemClock_IsConvertedByTheOffsetBetweenTheClocks()
        {
            var clock = new CameraClock();
            var trackerNow = 20 * k_Second;
            var systemNow = 3_620 * k_Second;
            Assert.IsTrue(clock.TryToTrackerClock(systemNow - k_Latency, trackerNow, systemNow, out var converted));
            Assert.AreEqual(CameraClock.Base.System, clock.Source);
            Assert.AreEqual(trackerNow - k_Latency, converted);
            Assert.AreEqual(k_Latency, clock.ArrivalAgeNs);
        }

        // The choice holds, and later frames convert with the offset read at their own arrival.
        [Test]
        public void Choice_IsKeptForLaterFrames()
        {
            var clock = new CameraClock();
            clock.TryToTrackerClock(3_600 * k_Second - k_Latency, 10 * k_Second, 3_600 * k_Second, out _);
            Assert.IsTrue(clock.TryToTrackerClock(3_601 * k_Second - k_Latency, 11 * k_Second, 3_601 * k_Second,
                out var converted));
            Assert.AreEqual(CameraClock.Base.System, clock.Source);
            Assert.AreEqual(11 * k_Second - k_Latency, converted);
        }

        // When both are plausible, as when the Input System happens to share the system clock, the
        // tracker's wins: nothing is converted that doesn't need to be.
        [Test]
        public void BothPlausible_PrefersTheTrackersClock()
        {
            var clock = new CameraClock();
            var now = 50 * k_Second;
            Assert.IsTrue(clock.TryToTrackerClock(now - k_Latency, now, now + 1_000_000, out var converted));
            Assert.AreEqual(CameraClock.Base.Tracker, clock.Source);
            Assert.AreEqual(now - k_Latency, converted);
        }

        // Neither clock fits: refuse, rather than match poses to the wrong rotations, and say so once.
        [Test]
        public void NeitherClock_RefusesAndWarnsOnce()
        {
            var clock = new CameraClock();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("match neither"));
            for (var i = 0; i < 100; i++)
                Assert.IsFalse(clock.TryToTrackerClock(i * k_Latency, 1_000 * k_Second, 2_000 * k_Second, out _));
            Assert.AreEqual(CameraClock.Base.Unknown, clock.Source);
            LogAssert.NoUnexpectedReceived();
        }

        // HandTracker asks about the same image every rendered frame. A slow first result asked about
        // many times is one frame, not grounds to give up.
        [Test]
        public void SameFrameAskedRepeatedly_CountsOnce()
        {
            var clock = new CameraClock();
            for (var i = 0; i < 100; i++)
                Assert.IsFalse(clock.TryToTrackerClock(0, 1_000 * k_Second, 2_000 * k_Second, out _));
            LogAssert.NoUnexpectedReceived();
        }

        // HandTracker's reference is the native sensors' clock, which reads 0 where there are none, as
        // on iOS. That must fail the check, leaving the system clock.
        [Test]
        public void ZeroReferenceClock_FallsToTheSystemClock()
        {
            var clock = new CameraClock("[Hands]", "the native sensors' clock");
            var systemNow = 230_000 * k_Second;
            Assert.IsTrue(clock.TryToTrackerClock(systemNow - 150_000_000, 0, systemNow, out _));
            Assert.AreEqual(CameraClock.Base.System, clock.Source);
            Assert.AreEqual(150_000_000, clock.ArrivalAgeNs);
        }

        // The age is re-measured at every call, so asking later about the same image says it's older.
        [Test]
        public void Age_IsMeasuredAtEachCall()
        {
            var clock = new CameraClock();
            var image = 100 * k_Second;
            clock.TryToTrackerClock(image, 0, image + k_Latency, out _);
            clock.TryToTrackerClock(image, 0, image + 3 * k_Latency, out _);
            Assert.AreEqual(3 * k_Latency, clock.ArrivalAgeNs);
        }

        // A stale frame at the start of a session doesn't settle anything; a good one later does.
        [Test]
        public void StaleFirstFrame_IsSkippedUntilOneFits()
        {
            var clock = new CameraClock();
            Assert.IsFalse(clock.TryToTrackerClock(10 * k_Second, 20 * k_Second, 99 * k_Second, out _));
            Assert.IsTrue(clock.TryToTrackerClock(21 * k_Second - k_Latency, 21 * k_Second, 99 * k_Second, out _));
            Assert.AreEqual(CameraClock.Base.Tracker, clock.Source);
        }

        [Test]
        public void Reset_ForgetsTheChoice()
        {
            var clock = new CameraClock();
            clock.TryToTrackerClock(10 * k_Second - k_Latency, 10 * k_Second, 0, out _);
            clock.Reset();
            Assert.AreEqual(CameraClock.Base.Unknown, clock.Source);
        }

        [Test]
        public void SystemNow_AdvancesInNanoseconds()
        {
            var before = CameraClock.SystemNowNs();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 20) { }
            var elapsed = CameraClock.SystemNowNs() - before;
            Assert.That(elapsed, Is.InRange(15_000_000L, 500_000_000L));
        }
    }
}
