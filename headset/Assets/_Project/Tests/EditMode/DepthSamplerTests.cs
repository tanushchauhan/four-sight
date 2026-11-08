using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class DepthSamplerTests
    {
        const int k_Width = 256;
        const int k_Height = 192;

        static float[] Filled(float depth)
        {
            var map = new float[k_Width * k_Height];
            for (var i = 0; i < map.Length; i++)
                map[i] = depth;
            return map;
        }

        static DepthSampler Sampler(float[] depth, byte[] confidence = null)
        {
            var sampler = new DepthSampler();
            sampler.Set(depth, confidence, k_Width, k_Height, 1);
            return sampler;
        }

        static Vector2 Uv(int x, int y) => new((x + 0.5f) / k_Width, (y + 0.5f) / k_Height);

        [Test]
        public void EvenDepth_IsReturned()
        {
            Assert.IsTrue(Sampler(Filled(0.25f)).TrySample(Uv(100, 80), 2, 1, out var meters, out var used));
            Assert.AreEqual(0.25f, meters, 1e-6f);
            Assert.AreEqual(25, used);
        }

        // A point in the camera image is at the same fraction across and down the depth map.
        [Test]
        public void ImagePoint_MapsToTheSameFractionOfTheDepthMap()
        {
            var map = new float[k_Width * k_Height];
            for (var y = 0; y < k_Height; y++)
                for (var x = 0; x < k_Width; x++)
                    map[y * k_Width + x] = 1f + x + 1000f * y;
            Assert.IsTrue(Sampler(map).TrySample(Uv(40, 150), 0, 0, out var meters, out _));
            Assert.AreEqual(1f + 40 + 1000f * 150, meters, 1e-3f);
        }

        // Pixels past the hand's edge see the room behind; the median ignores a minority of them.
        [Test]
        public void BackgroundPastTheEdge_DoesNotPullTheDepth()
        {
            var map = Filled(2.0f);
            for (var y = 78; y <= 82; y++)
                for (var x = 98; x <= 101; x++)
                    map[y * k_Width + x] = 0.25f;
            Assert.IsTrue(Sampler(map).TrySample(Uv(100, 80), 2, 1, out var meters, out _));
            Assert.AreEqual(0.25f, meters, 1e-6f);
        }

        [Test]
        public void InvalidDepths_AreSkipped()
        {
            var map = Filled(0.3f);
            map[80 * k_Width + 100] = 0f;
            map[80 * k_Width + 101] = float.NaN;
            map[81 * k_Width + 100] = float.PositiveInfinity;
            map[79 * k_Width + 100] = -1f;
            Assert.IsTrue(Sampler(map).TrySample(Uv(100, 80), 2, 1, out var meters, out var used));
            Assert.AreEqual(0.3f, meters, 1e-6f);
            Assert.AreEqual(21, used);
        }

        [Test]
        public void LowConfidence_IsSkipped()
        {
            var map = Filled(0.3f);
            var confidence = new byte[k_Width * k_Height];
            for (var i = 0; i < confidence.Length; i++)
                confidence[i] = 2;
            // The centre row is low confidence and reads far.
            for (var x = 98; x <= 102; x++)
            {
                map[80 * k_Width + x] = 5f;
                confidence[80 * k_Width + x] = 0;
            }
            Assert.IsTrue(Sampler(map, confidence).TrySample(Uv(100, 80), 2, 1, out var meters, out var used));
            Assert.AreEqual(0.3f, meters, 1e-6f);
            Assert.AreEqual(20, used);
        }

        [Test]
        public void NothingConfident_Fails()
        {
            var confidence = new byte[k_Width * k_Height];
            Assert.IsFalse(Sampler(Filled(0.3f), confidence).TrySample(Uv(100, 80), 2, 1, out _, out _));
        }

        [Test]
        public void EvenCount_AveragesTheMiddleTwo()
        {
            var map = Filled(1f);
            map[0] = 0.2f;
            map[1] = 0.4f;
            map[k_Width] = 0.6f;
            map[k_Width + 1] = 0.8f;
            // At the corner only a 2x2 patch is inside the map.
            Assert.IsTrue(Sampler(map).TrySample(Uv(0, 0), 1, 0, out var meters, out var used));
            Assert.AreEqual(4, used);
            Assert.AreEqual(0.5f, meters, 1e-6f);
        }

        [Test]
        public void OutsideTheImage_Fails()
        {
            var sampler = Sampler(Filled(0.3f));
            Assert.IsFalse(sampler.TrySample(new Vector2(-0.01f, 0.5f), 2, 1, out _, out _));
            Assert.IsFalse(sampler.TrySample(new Vector2(0.5f, 1f), 2, 1, out _, out _));
            Assert.IsFalse(sampler.TrySample(new Vector2(float.NaN, 0.5f), 2, 1, out _, out _));
        }

        [Test]
        public void NoDepthTaken_Fails()
        {
            Assert.IsFalse(new DepthSampler().TrySample(new Vector2(0.5f, 0.5f), 2, 1, out _, out _));
        }
    }
}
