using System;
using System.Collections.Generic;
using CardboardXR.Tracking;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class PositionFilterTests
    {
        const long k_ImuStepNs = 2_500_000;
        const long k_CameraStepNs = 33_333_333;
        const double k_ArDelaySeconds = 0.07;

        [Test]
        public void StandingStill_StaysPut()
        {
            var still = new Vector3d(1, 2, 3);
            var filter = Simulate(_ => still, _ => Vector3d.Zero, Vector3d.Zero, 2, k_ArDelaySeconds, out var endNs);
            AssertClose(still, filter.PredictPosition(endNs), 1e-3);
        }

        [Test]
        public void ConstantVelocity_PredictsPastTheLateFixes()
        {
            var velocity = new Vector3d(1, 0, -0.5);
            var filter = Simulate(t => velocity * t, _ => Vector3d.Zero, Vector3d.Zero, 2, k_ArDelaySeconds, out var endNs);
            AssertClose(velocity * (endNs * 1e-9), filter.PredictPosition(endNs), 0.01);
            AssertClose(velocity, filter.Velocity, 0.02);
        }

        [Test]
        public void AccelerometerBias_IsEstimated()
        {
            var acceleration = new Vector3d(0, 0.5, 0);
            var bias = new Vector3d(0, 0.2, -0.1);
            var filter = Simulate(t => acceleration * (0.5 * t * t), _ => acceleration, bias, 4, k_ArDelaySeconds, out var endNs);
            AssertClose(bias, filter.AccelerometerBias, 0.05);
            var seconds = endNs * 1e-9;
            AssertClose(acceleration * (0.5 * seconds * seconds), filter.PredictPosition(endNs), 0.02);
        }

        [Test]
        public void LateFixes_MatchFixesThatArriveOnTime()
        {
            Vector3d Wobble(double t) => new(Math.Sin(t * 3), 0.2 * Math.Cos(t * 5), 0.1 * t);
            Vector3d WobbleAcceleration(double t) => new(-9 * Math.Sin(t * 3), -5 * Math.Cos(t * 5), 0);

            var onTime = Simulate(Wobble, WobbleAcceleration, Vector3d.Zero, 1.5, 0, out var endNs);
            var late = Simulate(Wobble, WobbleAcceleration, Vector3d.Zero, 1.5, k_ArDelaySeconds, out _);
            AssertClose(onTime.PredictPosition(endNs), late.PredictPosition(endNs), 1e-9);
            AssertClose(onTime.Velocity, late.Velocity, 1e-9);
        }

        [Test]
        public void FixThatJumps_IsRejected()
        {
            var filter = Simulate(_ => Vector3d.Zero, _ => Vector3d.Zero, Vector3d.Zero, 1, 0, out var endNs);
            Assert.IsFalse(filter.AddPosition(endNs, new Vector3d(1, 0, 0)));
            Assert.AreEqual(1, filter.RejectedPositions);
            AssertClose(new Vector3d(1, 0, 0), filter.LastRejectedInnovation, 1e-3);
            AssertClose(Vector3d.Zero, filter.PredictPosition(endNs), 1e-3);
        }

        [Test]
        public void Relocating_KeepsTheLearnedBias()
        {
            var bias = new Vector3d(0, 0.2, -0.1);
            var filter = Simulate(_ => Vector3d.Zero, _ => Vector3d.Zero, bias, 4, k_ArDelaySeconds, out var endNs);
            var learned = filter.AccelerometerBias;
            AssertClose(bias, learned, 0.05);

            var there = new Vector3d(5, 5, 5);
            filter.Relocate(endNs, there);
            AssertClose(learned, filter.AccelerometerBias, 1e-12);
            AssertClose(Vector3d.Zero, filter.Velocity, 1e-12);
            AssertClose(there, filter.PredictPosition(endNs + 50_000_000), 1e-9);
        }

        [Test]
        public void RelocatingWithoutTheBias_StartsItOver()
        {
            var filter = Simulate(_ => Vector3d.Zero, _ => Vector3d.Zero, new Vector3d(0, 0.2, 0), 4, k_ArDelaySeconds, out var endNs);
            filter.Relocate(endNs, Vector3d.Zero, keepBias: false);
            AssertClose(Vector3d.Zero, filter.AccelerometerBias, 1e-12);
        }

        [Test]
        public void BiasEstimate_StaysBounded()
        {
            // An accelerometer reading 2 m/s^2 while the fixes say the phone is still.
            var filter = Simulate(_ => Vector3d.Zero, _ => Vector3d.Zero, new Vector3d(2, 0, 0), 4, k_ArDelaySeconds, out _);
            Assert.That(filter.AccelerometerBias.Length, Is.LessThanOrEqualTo(0.5 + 1e-9));
        }

        [Test]
        public void FixesThatSlideAway_AreRejected()
        {
            // Standing still while the fixes slide off at 3 m/s, as ARCore did on a Pixel 9 Pro in
            // poor light.
            var filter = Simulate(t => t < 1 ? Vector3d.Zero : new Vector3d(0, -3 * (t - 1), 0),
                _ => Vector3d.Zero, Vector3d.Zero, 1.5, 0, out var endNs);
            Assert.That(filter.RejectedPositions, Is.GreaterThan(10));
            AssertClose(Vector3d.Zero, filter.PredictPosition(endNs), 0.02);
        }

        [Test]
        public void FixOlderThanTheHistory_IsDropped()
        {
            var filter = Simulate(_ => Vector3d.Zero, _ => Vector3d.Zero, Vector3d.Zero, 2, 0, out var endNs);
            var before = filter.PredictPosition(endNs);
            filter.AddPosition(endNs - 1_000_000_000, new Vector3d(5, 5, 5));
            Assert.AreEqual(1, filter.DroppedPositions);
            AssertClose(before, filter.PredictPosition(endNs), 1e-12);
        }

        // IMU samples every 2.5 ms and camera fixes every 33 ms that are delivered delaySeconds after
        // capture. Fixes still in flight at the end are delivered then.
        static PositionFilter Simulate(Func<double, Vector3d> position, Func<double, Vector3d> acceleration,
            Vector3d accelerometerBias, double seconds, double delaySeconds, out long endNs)
        {
            var filter = new PositionFilter();
            var inFlight = new Queue<(long timeNs, Vector3d position)>();
            var delayNs = (long)(delaySeconds * 1e9);
            long nextFixNs = 0;
            endNs = 0;
            for (long t = 0; t <= (long)(seconds * 1e9); t += k_ImuStepNs)
            {
                for (; nextFixNs <= t; nextFixNs += k_CameraStepNs)
                    inFlight.Enqueue((nextFixNs, position(nextFixNs * 1e-9)));
                while (inFlight.Count > 0 && t - inFlight.Peek().timeNs >= delayNs)
                {
                    var fix = inFlight.Dequeue();
                    filter.AddPosition(fix.timeNs, fix.position);
                }
                filter.AddAcceleration(t, acceleration(t * 1e-9) + accelerometerBias);
                endNs = t;
            }

            while (inFlight.Count > 0)
            {
                var fix = inFlight.Dequeue();
                filter.AddPosition(fix.timeNs, fix.position);
            }
            return filter;
        }

        static void AssertClose(Vector3d expected, Vector3d actual, double tolerance)
        {
            Assert.That((actual - expected).Length, Is.LessThan(tolerance), $"expected {expected}, got {actual}");
        }
    }

    public class GyroDriftEstimatorTests
    {
        static readonly Vector3d k_Up = new(0, 1, 0);

        [Test]
        public void SteadyDrift_BecomesBiasAlongTheVertical()
        {
            var estimator = new GyroDriftEstimator(windowSeconds: 5, gain: 0.5);
            var up = new Vector3d(0, 0.9, 0.3).Normalized;
            for (var i = 0; i <= 160; i++)
            {
                var seconds = i / 30.0;
                estimator.AddSample((long)(seconds * 1e9), 0.004 * seconds, up);
            }
            Assert.AreEqual(1, estimator.Updates);
            Assert.AreEqual(0.004, estimator.LastDriftRate, 1e-9);
            Assert.That((estimator.Bias - up * 0.002).Length, Is.LessThan(1e-9));
        }

        [Test]
        public void OneWindowMovesTheBiasOnlySoFar()
        {
            var estimator = new GyroDriftEstimator(windowSeconds: 5, gain: 0.5);
            for (var i = 0; i <= 160; i++)
            {
                var seconds = i / 30.0;
                estimator.AddSample((long)(seconds * 1e9), 0.02 * seconds, k_Up);
            }
            Assert.AreEqual(0.02, estimator.LastDriftRate, 1e-9);
            Assert.AreEqual(0.003, Vector3d.Dot(estimator.Bias, k_Up), 1e-9);
        }

        [Test]
        public void FirstWindowAfterRestart_OnlyMeasures()
        {
            var estimator = new GyroDriftEstimator(windowSeconds: 1, gain: 1);
            estimator.Restart();
            for (var i = 0; i <= 30; i++)
                estimator.AddSample((long)(i / 30.0 * 1e9), 0.004 * i / 30.0, k_Up);
            Assert.AreEqual(0.004, estimator.LastDriftRate, 1e-9);
            Assert.AreEqual(0, estimator.Updates);
            Assert.AreEqual(0, estimator.Bias.Length, 1e-12);

            for (var i = 31; i <= 61; i++)
                estimator.AddSample((long)(i / 30.0 * 1e9), 0.004 * i / 30.0, k_Up);
            Assert.AreEqual(1, estimator.Updates);
        }

        [Test]
        public void DriftAcrossTheHalfTurn_IsUnwrapped()
        {
            var estimator = new GyroDriftEstimator(windowSeconds: 5, gain: 1);
            for (var i = 0; i <= 160; i++)
            {
                var seconds = i / 30.0;
                var heading = Math.PI - 0.01 + 0.007 * seconds;
                estimator.AddSample((long)(seconds * 1e9), Math.IEEERemainder(heading, 2 * Math.PI), k_Up);
            }
            Assert.AreEqual(0.007, estimator.LastDriftRate, 1e-9);
        }

        [Test]
        public void CorrectingItsOwnMeasurements_Converges()
        {
            // The tracker drifts by whatever bias is left uncorrected along the vertical.
            const double trueBias = 0.007;
            var estimator = new GyroDriftEstimator(windowSeconds: 5, gain: 0.5);
            double heading = 0;
            for (var i = 0; i < 30 * 60; i++)
            {
                heading += (trueBias - Vector3d.Dot(estimator.Bias, k_Up)) / 30.0;
                estimator.AddSample((long)(i / 30.0 * 1e9), heading, k_Up);
            }
            Assert.AreEqual(trueBias, Vector3d.Dot(estimator.Bias, k_Up), 1e-4);
        }

        [Test]
        public void Restart_KeepsTheBias()
        {
            var estimator = new GyroDriftEstimator(windowSeconds: 1, gain: 1);
            for (var i = 0; i <= 30; i++)
                estimator.AddSample((long)(i / 30.0 * 1e9), 0.01 * i / 30.0, k_Up);
            var learned = estimator.Bias;
            estimator.Restart();
            estimator.AddSample(2_000_000_000, 3, k_Up);
            Assert.That((estimator.Bias - learned).Length, Is.LessThan(1e-12));
        }
    }

    public class YawAlignerTests
    {
        static readonly Vector3d k_Up = new(0, 1, 0);

        [Test]
        public void RecoversTheHeadingOffset_WhateverTheHeadDoes()
        {
            var offset = Rotation.FromAxisAndAngle(k_Up, 40 * Math.PI / 180);
            var aligner = new YawAligner();
            for (var i = 0; i < 30; i++)
            {
                var tracked = Rotation.FromAxisAndAngle(new Vector3d(1, 0.3 * i, 0.5), 0.05 * i);
                aligner.AddSample(i * 33_333_333L, offset * tracked, tracked);
            }
            Assert.IsTrue(aligner.IsInitialized);
            Assert.AreEqual(40, aligner.YawRadians * 180 / Math.PI, 1e-6);
        }

        [Test]
        public void AveragesAcrossTheHalfTurn()
        {
            var aligner = new YawAligner();
            for (var i = 0; i < 20; i++)
            {
                var degrees = i % 2 == 0 ? 179 : -179;
                aligner.AddSample(i * 33_333_333L, Rotation.FromAxisAndAngle(k_Up, degrees * Math.PI / 180), Rotation.Identity);
            }
            Assert.That(Math.Abs(aligner.YawRadians) * 180 / Math.PI, Is.GreaterThan(178.9));
        }

        [Test]
        public void StartsOnlyOnceSamplesAgree()
        {
            var aligner = new YawAligner();
            AddYaw(aligner, 0, 150);
            AddYaw(aligner, 1, 40);
            AddYaw(aligner, 2, 40);
            Assert.IsFalse(aligner.IsInitialized, "the stray first sample reset the count");
            AddYaw(aligner, 3, 40);
            Assert.IsTrue(aligner.IsInitialized);
            Assert.AreEqual(40, aligner.YawRadians * 180 / Math.PI, 1e-6);
        }

        [Test]
        public void OutlierIsIgnored()
        {
            var aligner = new YawAligner();
            for (var i = 0; i < 10; i++)
                AddYaw(aligner, i, 40);
            AddYaw(aligner, 10, 170);
            Assert.AreEqual(40, aligner.YawRadians * 180 / Math.PI, 1e-6);
            Assert.AreEqual(1, aligner.Outliers);
        }

        [Test]
        public void LastingChange_StartsOver()
        {
            var aligner = new YawAligner();
            for (var i = 0; i < 10; i++)
                AddYaw(aligner, i, 40);
            for (var i = 10; i < 40; i++)
                AddYaw(aligner, i, 100);
            Assert.IsTrue(aligner.IsInitialized);
            Assert.AreEqual(100, aligner.YawRadians * 180 / Math.PI, 1e-6);
        }

        static void AddYaw(YawAligner aligner, int index, double degrees) =>
            aligner.AddSample(index * 33_333_333L, Rotation.FromAxisAndAngle(k_Up, degrees * Math.PI / 180), Rotation.Identity);

        [Test]
        public void SpacesThatDisagreeAboutDown_AreIgnored()
        {
            var aligner = new YawAligner();
            var tilted = Rotation.FromAxisAndAngle(new Vector3d(1, 0, 0), 60 * Math.PI / 180);
            aligner.AddSample(0, tilted, Rotation.Identity);
            Assert.IsFalse(aligner.IsInitialized);
        }
    }
}
