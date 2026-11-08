using System;
using CardboardXR.Tracking;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class RotationMathTests
    {
        const double k_Tolerance = 1e-9;

        static void AssertVector(Vector3d expected, Vector3d actual, double tolerance = k_Tolerance)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, "x");
            Assert.AreEqual(expected.y, actual.y, tolerance, "y");
            Assert.AreEqual(expected.z, actual.z, tolerance, "z");
        }

        [Test]
        public void AxisAngleFollowsTheRightHandRule()
        {
            var quarterTurnAboutZ = Rotation.FromAxisAndAngle(new Vector3d(0, 0, 1), Math.PI / 2);
            AssertVector(new Vector3d(0, 1, 0), quarterTurnAboutZ * new Vector3d(1, 0, 0));
        }

        [Test]
        public void AxisAndAngleRoundTrip()
        {
            var axis = new Vector3d(1, 2, 3).Normalized;
            Rotation.FromAxisAndAngle(axis, 0.7).GetAxisAndAngle(out var outAxis, out var outAngle);
            AssertVector(axis, outAxis);
            Assert.AreEqual(0.7, outAngle, k_Tolerance);
        }

        [Test]
        public void RotateIntoTurnsOneDirectionIntoAnother()
        {
            AssertVector(new Vector3d(0, 3, 0).Normalized,
                (Rotation.RotateInto(new Vector3d(2, 0, 0), new Vector3d(0, 3, 0)) * new Vector3d(1, 0, 0)).Normalized);
            // Opposite directions take the 180 degree branch.
            AssertVector(new Vector3d(-1, 0, 0), Rotation.RotateInto(new Vector3d(1, 0, 0), new Vector3d(-1, 0, 0)) * new Vector3d(1, 0, 0));
        }

        [Test]
        public void ProductAppliesTheRightRotationFirst()
        {
            var a = Rotation.FromAxisAndAngle(new Vector3d(0, 1, 0), 0.4);
            var b = Rotation.FromAxisAndAngle(new Vector3d(1, 1, 0), 1.1);
            var v = new Vector3d(0.3, -0.5, 0.8);
            AssertVector(a * (b * v), (a * b) * v);
            AssertVector(v, a.Inverse * (a * v));
        }

        [Test]
        public void RotationMatrixMatchesQuaternion()
        {
            var r = Rotation.FromAxisAndAngle(new Vector3d(-1, 2, 0.5), 2.3);
            var v = new Vector3d(0.9, 0.1, -0.4);
            AssertVector(r * v, Matrix3x3d.FromRotation(r) * v);
        }

        [Test]
        public void MatrixInverseUndoesTheMatrix()
        {
            var m = new Matrix3x3d();
            double[] values = { 2, -1, 0.5, 0.3, 4, 1, -2, 0.7, 3 };
            for (var i = 0; i < 9; i++)
                m[i / 3, i % 3] = values[i];

            var product = m.Inverse() * m;
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                    Assert.AreEqual(row == col ? 1.0 : 0.0, product[row, col], 1e-12);
            }
        }

        [Test]
        public void LowpassFilterBlendsByTimeConstant()
        {
            var filter = new LowpassFilter(1.0);
            filter.AddSample(new Vector3d(0, 0, 0), 0);
            filter.AddSample(new Vector3d(1, 0, 0), 100_000_000);
            var alpha = 0.1 / (1.0 / (2 * Math.PI) + 0.1);
            Assert.AreEqual(alpha, filter.FilteredData.x, 1e-12);
        }
    }

    public class SensorFusionEkfTests
    {
        const double k_Gravity = 9.80665;
        const long k_NsPerSecond = 1_000_000_000;

        static SensorSample Sample(long timestampNs, Vector3d data) => new(timestampNs, timestampNs, data);

        // Feeds gyro samples at 200 Hz and accelerometer samples at 100 Hz for the given duration.
        static long Run(SensorFusionEkf fusion, long startNs, double seconds, Vector3d gyroscope, Func<long, Vector3d> accelerometer)
        {
            var endNs = startNs + (long)(seconds * k_NsPerSecond);
            for (var t = startNs; t <= endNs; t += 5_000_000)
            {
                if ((t - startNs) % 10_000_000 == 0)
                    fusion.ProcessAccelerometerSample(Sample(t, accelerometer(t)));
                fusion.ProcessGyroscopeSample(Sample(t, gyroscope));
            }
            return endNs;
        }

        [Test]
        public void FirstAccelerometerSampleAlignsStartSpaceUpWithGravity()
        {
            var fusion = new SensorFusionEkf();
            Assert.IsFalse(fusion.IsAlignedWithGravity);
            // Phone upright in portrait: the reaction to gravity points along its +y.
            fusion.ProcessAccelerometerSample(Sample(1, new Vector3d(0, k_Gravity, 0)));
            Assert.IsTrue(fusion.IsAlignedWithGravity);
            var up = fusion.SensorFromStart * new Vector3d(0, 0, 1);
            Assert.AreEqual(0, up.x, 1e-9);
            Assert.AreEqual(1, up.y, 1e-9);
            Assert.AreEqual(0, up.z, 1e-9);
        }

        [Test]
        public void IntegratesTheGyroscopeWithTheRightHandRule()
        {
            var fusion = new SensorFusionEkf();
            // Flat on its back, turning counterclockwise at 1 rad/s about its z axis for one second.
            Run(fusion, k_NsPerSecond, 1.0, new Vector3d(0, 0, 1), _ => new Vector3d(0, 0, k_Gravity));

            // The phone's x axis, seen in start space, should have turned 1 radian toward +y.
            var phoneX = fusion.SensorFromStart.Inverse * new Vector3d(1, 0, 0);
            Assert.AreEqual(Math.Cos(1.0), phoneX.x, 0.02);
            Assert.AreEqual(Math.Sin(1.0), phoneX.y, 0.02);
            Assert.AreEqual(0, phoneX.z, 0.01);
        }

        [Test]
        public void AccelerometerCorrectsTilt()
        {
            var fusion = new SensorFusionEkf();
            fusion.ProcessAccelerometerSample(Sample(1, new Vector3d(0, 0, k_Gravity)));
            // Gravity now says the phone is tilted 10 degrees about x; the gyroscope reports nothing.
            var tilted = Rotation.FromAxisAndAngle(new Vector3d(1, 0, 0), 10 * Math.PI / 180) * new Vector3d(0, 0, k_Gravity);
            Run(fusion, k_NsPerSecond, 3.0, Vector3d.Zero, _ => tilted);

            var up = fusion.SensorFromStart * new Vector3d(0, 0, 1);
            var errorDegrees = Math.Acos(Math.Clamp(Vector3d.Dot(up, tilted.Normalized), -1, 1)) * 180 / Math.PI;
            Assert.Less(errorDegrees, 1.0);
        }

        [Test]
        public void PredictsAheadWithTheLatestAngularVelocity()
        {
            var fusion = new SensorFusionEkf();
            var endNs = Run(fusion, k_NsPerSecond, 0.5, new Vector3d(0, 0, 2), _ => new Vector3d(0, 0, k_Gravity));

            var now = fusion.PredictRotation(0);
            var ahead = fusion.PredictRotation(endNs + k_NsPerSecond / 10);
            // 0.1 s at 2 rad/s is 0.2 rad further around z.
            (ahead * now.Inverse).GetAxisAndAngle(out var axis, out var angle);
            Assert.AreEqual(0.2, angle, 1e-6);
            Assert.AreEqual(-1, axis.z, 1e-6);
        }

        [Test]
        public void EstimatesGyroscopeBiasWhileStill()
        {
            var fusion = new SensorFusionEkf();
            var bias = new Vector3d(0.01, -0.02, 0.005);
            Run(fusion, k_NsPerSecond, 5.0, bias, _ => new Vector3d(0, 0, k_Gravity));

            Assert.AreEqual(bias.x, fusion.GyroscopeBias.x, 1e-4);
            Assert.AreEqual(bias.y, fusion.GyroscopeBias.y, 1e-4);
            Assert.AreEqual(bias.z, fusion.GyroscopeBias.z, 1e-4);
        }

        [Test]
        public void DropsSamplesThatAreNotNewer()
        {
            var fusion = new SensorFusionEkf();
            fusion.ProcessAccelerometerSample(Sample(1, new Vector3d(0, 0, k_Gravity)));
            fusion.ProcessGyroscopeSample(Sample(10_000_000, new Vector3d(0, 0, 1)));
            fusion.ProcessGyroscopeSample(Sample(20_000_000, new Vector3d(0, 0, 1)));
            var before = fusion.SensorFromStart;
            fusion.ProcessGyroscopeSample(Sample(20_000_000, new Vector3d(0, 0, 5)));
            Assert.AreEqual(before.w, fusion.SensorFromStart.w, 1e-15);
            Assert.AreEqual(before.z, fusion.SensorFromStart.z, 1e-15);
        }
    }
}
