using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class AlignmentMarkerTests
    {
        // A Pixel 9 Pro: 2142x960 px at 369.5 x 370.1 dpi.
        static readonly Vector2Int k_Screen = new(2142, 960);
        static readonly Vector2 k_ScreenMeters = new Vector2(2142 / 369.5f, 960 / 370.1f) * 0.0254f;

        [Test]
        public void Pixel9Pro_Is4PixelsWideAnd24MillimetersTall()
        {
            var mesh = AlignmentMarker.CreateMesh(k_Screen, k_ScreenMeters);
            var vertices = mesh.vertices;
            Object.DestroyImmediate(mesh);

            // 2 dp at 369.5 dpi is 4.6 px, truncated like the Cardboard SDK; centered on 2142 px.
            Assert.AreEqual(1069f, PixelX(vertices[0].x), 1e-2f);
            Assert.AreEqual(1073f, PixelX(vertices[1].x), 1e-2f);
            // Up from the bottom edge: 24 mm at 370.1 dpi is 349.7 px.
            Assert.AreEqual(0f, PixelY(vertices[0].y), 1e-2f);
            Assert.AreEqual(349f, PixelY(vertices[2].y), 1e-2f);
        }

        [Test]
        public void TallerThanTheScreen_StopsAtTheTop()
        {
            var mesh = AlignmentMarker.CreateMesh(k_Screen, k_ScreenMeters, heightMeters: 1f);
            var top = mesh.vertices[2].y;
            Object.DestroyImmediate(mesh);
            Assert.AreEqual(1f, top, 1e-5f);
        }

        static float PixelX(float ndc) => (ndc + 1f) * 0.5f * k_Screen.x;

        static float PixelY(float ndc) => (ndc + 1f) * 0.5f * k_Screen.y;
    }
}
