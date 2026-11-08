using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    // Expected values come from a Python transcription of the Cardboard SDK's lens_distortion.cc,
    // polynomial_radial_distortion.cc and distortion_mesh.cc, run in double precision.
    public class LensDistortionTests
    {
        // Roughly a Pixel 9 Pro in landscape; wide enough that the viewer's own limits apply.
        const float k_PhoneWidth = 0.1466f;
        const float k_PhoneHeight = 0.0657f;
        // A narrower screen, where the screen edge limits the outer field of view.
        const float k_SmallWidth = 0.110f;
        const float k_SmallHeight = 0.062f;
        const float k_Tolerance = 1e-4f;

        static void AssertFov(Vector4 expectedDegrees, Vector4 actualRadians)
        {
            Assert.AreEqual(expectedDegrees.x, actualRadians.x * Mathf.Rad2Deg, 1e-3f, "left");
            Assert.AreEqual(expectedDegrees.y, actualRadians.y * Mathf.Rad2Deg, 1e-3f, "right");
            Assert.AreEqual(expectedDegrees.z, actualRadians.z * Mathf.Rad2Deg, 1e-3f, "bottom");
            Assert.AreEqual(expectedDegrees.w, actualRadians.w * Mathf.Rad2Deg, 1e-3f, "top");
        }

        [Test]
        public void FieldOfViewClampsToViewerLimits()
        {
            var lens = new LensDistortion(ViewerParams.CardboardV1, k_PhoneWidth, k_PhoneHeight);
            AssertFov(new Vector4(40f, 40f, 40f, 40f), lens.GetFieldOfView(Eye.Left));
            AssertFov(new Vector4(40f, 40f, 40f, 40f), lens.GetFieldOfView(Eye.Right));
        }

        [Test]
        public void NarrowScreenLimitsOuterFieldOfView()
        {
            var lens = new LensDistortion(ViewerParams.CardboardV1, k_SmallWidth, k_SmallHeight);
            AssertFov(new Vector4(34.98821f, 40f, 40f, 40f), lens.GetFieldOfView(Eye.Left));
            // The right eye mirrors the left: its outer edge is on the right.
            AssertFov(new Vector4(40f, 34.98821f, 40f, 40f), lens.GetFieldOfView(Eye.Right));
        }

        [Test]
        public void InverseDistortionRoundTrips()
        {
            var distortion = new PolynomialRadialDistortion(ViewerParams.CardboardV1.distortionCoefficients);
            var point = new Vector2(0.3f, -0.45f);
            var roundTrip = distortion.DistortInverse(distortion.Distort(point));
            Assert.AreEqual(point.x, roundTrip.x, k_Tolerance);
            Assert.AreEqual(point.y, roundTrip.y, k_Tolerance);
            Assert.AreEqual(Vector2.zero, distortion.DistortInverse(Vector2.zero));
        }

        [TestCase(Eye.Left, 0, 0, -0.940303f, -0.807604f)]
        [TestCase(Eye.Left, 39, 39, -0.087252f, 0.845199f)]
        [TestCase(Eye.Left, 20, 13, -0.479635f, -0.333381f)]
        [TestCase(Eye.Right, 0, 0, 0.087252f, -0.780683f)]
        [TestCase(Eye.Right, 39, 39, 0.940303f, 0.872120f)]
        [TestCase(Eye.Right, 20, 13, 0.508631f, -0.334169f)]
        public void MeshVerticesMatchReference(Eye eye, int col, int row, float expectedX, float expectedY)
        {
            var lens = new LensDistortion(ViewerParams.CardboardV1, k_SmallWidth, k_SmallHeight);
            var mesh = lens.CreateDistortionMesh(eye);
            try
            {
                var n = LensDistortion.MeshResolution;
                Assert.AreEqual(n * n, mesh.vertexCount);
                Assert.AreEqual((n - 1) * (n - 1) * 6, mesh.triangles.Length);

                var index = row * n + col;
                var vertex = mesh.vertices[index];
                Assert.AreEqual(expectedX, vertex.x, k_Tolerance, "x");
                Assert.AreEqual(expectedY, vertex.y, k_Tolerance, "y");
                Assert.AreEqual(new Vector2(col / (n - 1f), row / (n - 1f)), mesh.uv[index]);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(Eye.Left)]
        [TestCase(Eye.Right)]
        public void ScreenAndTextureUvsRoundTrip(Eye eye)
        {
            var lens = new LensDistortion(ViewerParams.CardboardV1, k_PhoneWidth, k_PhoneHeight);
            foreach (var textureUv in new[] { new Vector2(0.5f, 0.5f), new Vector2(0.1f, 0.9f), new Vector2(0.8f, 0.25f) })
            {
                var roundTrip = lens.ScreenUvToTextureUv(lens.TextureUvToScreenUv(textureUv, eye), eye);
                Assert.AreEqual(textureUv.x, roundTrip.x, k_Tolerance);
                Assert.AreEqual(textureUv.y, roundTrip.y, k_Tolerance);
            }
        }

        [Test]
        public void EyesSitHalfTheLensSeparationApart()
        {
            var lens = new LensDistortion(ViewerParams.CardboardV1, k_PhoneWidth, k_PhoneHeight);
            Assert.AreEqual(new Vector3(-0.03f, 0f, 0f), lens.GetEyeOffset(Eye.Left));
            Assert.AreEqual(new Vector3(0.03f, 0f, 0f), lens.GetEyeOffset(Eye.Right));
        }

        [TestCase(Eye.Left)]
        [TestCase(Eye.Right)]
        public void ProjectionMapsFieldOfViewEdgesToScreenEdges(Eye eye)
        {
            var lens = new LensDistortion(ViewerParams.CardboardV1, k_SmallWidth, k_SmallHeight);
            var fov = lens.GetFieldOfView(eye);
            var projection = lens.GetProjectionMatrix(eye, 0.1f, 100f);

            // Unity view space looks down -z.
            const float depth = 2f;
            Vector3 Edge(float x, float y) => projection.MultiplyPoint(new Vector3(x * depth, y * depth, -depth));

            Assert.AreEqual(-1f, Edge(-Mathf.Tan(fov.x), 0f).x, k_Tolerance, "left");
            Assert.AreEqual(1f, Edge(Mathf.Tan(fov.y), 0f).x, k_Tolerance, "right");
            Assert.AreEqual(-1f, Edge(0f, -Mathf.Tan(fov.z)).y, k_Tolerance, "bottom");
            Assert.AreEqual(1f, Edge(0f, Mathf.Tan(fov.w)).y, k_Tolerance, "top");
        }
    }
}
