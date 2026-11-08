using System.Linq;
using CardboardXR.Hands;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class HandTrackingTests
    {
        [Test]
        public void Anchors_FollowMediaPipesGrids()
        {
            var anchors = PalmDecoder.CreateAnchors();

            Assert.AreEqual(2016, anchors.Length);
            AssertClose(new Vector2(0.5f / 24f, 0.5f / 24f), anchors[0]);
            AssertClose(anchors[0], anchors[1], "two anchors per cell in the fine grid");
            AssertClose(new Vector2(1.5f / 24f, 0.5f / 24f), anchors[2]);
            AssertClose(new Vector2(0.5f / 12f, 0.5f / 12f), anchors[1152], "the coarse grid follows");
            AssertClose(new Vector2(11.5f / 12f, 11.5f / 12f), anchors[2015]);
        }

        [Test]
        public void BestPalm_IsDecodedFromItsAnchor()
        {
            var anchors = PalmDecoder.CreateAnchors();
            var boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            var scores = Enumerable.Repeat(-10f, PalmDecoder.AnchorCount).ToArray();
            const int best = 1500;
            scores[best] = 3f;
            var o = best * PalmDecoder.ValuesPerAnchor;
            // In input pixels: 192 is the whole input.
            boxes[o] = 9.6f;
            boxes[o + 1] = -19.2f;
            boxes[o + 2] = 38.4f;
            boxes[o + 3] = 19.2f;
            boxes[o + 5] = 19.2f;
            boxes[o + 9] = -19.2f;

            Assert.IsTrue(PalmDecoder.TryDecodeBest(boxes, scores, anchors, 0.5f, out var palm));
            Assert.AreEqual(PalmDecoder.Sigmoid(3f), palm.score, 1e-5f);
            AssertClose(anchors[best] + new Vector2(0.05f, -0.1f), palm.center);
            AssertClose(new Vector2(0.2f, 0.1f), palm.size);
            AssertClose(anchors[best] + new Vector2(0f, 0.1f), palm.wrist);
            AssertClose(anchors[best] + new Vector2(0f, -0.1f), palm.middleKnuckle);

            scores[best] = -1f;
            Assert.IsFalse(PalmDecoder.TryDecodeBest(boxes, scores, anchors, 0.5f, out _));
        }

        [Test]
        public void Rotation_TurnsTheFingersUp()
        {
            Assert.AreEqual(0f, HandRegion.RotationFrom(new Vector2(100f, 200f), new Vector2(100f, 100f)), 1e-5f, "fingers up");
            Assert.AreEqual(Mathf.PI / 2f, HandRegion.RotationFrom(new Vector2(100f, 100f), new Vector2(200f, 100f)), 1e-5f, "fingers right");

            var region = new HandRegion { center = new Vector2(100f, 100f), size = 50f, rotation = Mathf.PI / 2f };
            AssertClose(new Vector2(125f, 100f), region.CropToImage(new Vector2(0.5f, 0f)), "the crop's top is toward the fingers");
            var point = new Vector2(117f, 83f);
            AssertClose(point, region.CropToImage(region.ImageToCrop(point)), "round trip", 1e-3f);
        }

        [Test]
        public void PalmRegion_CoversTheHandAboveThePalm()
        {
            // A 640x360 image fits the 192 input at 0.3 scale, 42 pixels down.
            var letterbox = Letterbox.Fit(640, 360, PalmDecoder.InputSize);
            AssertClose(new Vector2(320f, 180f), letterbox.ToImage(new Vector2(0.5f, 0.5f)));

            var palm = new Palm
            {
                score = 0.9f,
                center = new Vector2(0.5f, 0.5f),
                size = new Vector2(0.1f, 0.1f),
                wrist = new Vector2(0.5f, 0.55f),
                middleKnuckle = new Vector2(0.5f, 0.45f),
            };
            var region = HandRegion.FromPalm(palm, letterbox);

            Assert.AreEqual(0f, region.rotation, 1e-5f);
            // A tenth of the input is 64 image pixels: moved half of that up, 2.6 times as big.
            AssertClose(new Vector2(320f, 148f), region.center, "", 1e-3f);
            Assert.AreEqual(64f * 2.6f, region.size, 1e-3f);
        }

        [Test]
        public void LandmarkRegion_BoundsThePalmAndFingerBases()
        {
            var landmarks = Enumerable.Repeat(new Vector2(100f, 150f), 21).ToArray();
            landmarks[0] = new Vector2(100f, 200f);
            landmarks[1] = new Vector2(80f, 190f);
            landmarks[2] = new Vector2(80f, 170f);
            landmarks[3] = new Vector2(80f, 160f);
            landmarks[5] = new Vector2(90f, 110f);
            landmarks[6] = new Vector2(90f, 100f);
            landmarks[9] = new Vector2(100f, 110f);
            landmarks[10] = new Vector2(100f, 100f);
            landmarks[13] = new Vector2(110f, 110f);
            landmarks[14] = new Vector2(110f, 100f);
            landmarks[17] = new Vector2(120f, 120f);
            landmarks[18] = new Vector2(120f, 110f);

            var region = HandRegion.FromLandmarks(landmarks);

            Assert.AreEqual(0f, region.rotation, 1e-5f);
            // Bounds 40 x 100 around (100, 150): moved up a tenth of the height, twice the long side.
            AssertClose(new Vector2(100f, 140f), region.center, "", 1e-3f);
            Assert.AreEqual(200f, region.size, 1e-3f);
        }

        [Test]
        public void RegionSampling_ReadsTheTurnedCrop()
        {
            const int size = 8;
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var p = (y * size + x) * 4;
                    pixels[p] = (byte)(x * 30);
                    pixels[p + 1] = (byte)(y * 30);
                    pixels[p + 3] = 255;
                }
            }
            using var image = new NativeArray<byte>(pixels, Allocator.Temp);
            var output = new float[size * size * 3];

            var upright = new HandRegion { center = new Vector2(4f, 4f), size = size, rotation = 0f };
            ImageSampler.SampleRegion(image, size, size, upright, size, output);
            AssertPixel(output, size, 3, 2, 3, 2, "crop pixel (3, 2) is image pixel (3, 2)");

            var turned = upright;
            turned.rotation = Mathf.PI / 2f;
            ImageSampler.SampleRegion(image, size, size, turned, size, output);
            AssertPixel(output, size, 3, 2, 5, 3, "turned a quarter, crop pixel (x, y) is image pixel (7 - y, x)");
        }

        [Test]
        public void LetterboxSampling_PadsAboveAndBelow()
        {
            var pixels = new byte[4 * 2 * 4];
            for (var i = 0; i < 8; i++)
                pixels[i * 4] = 200;
            using var image = new NativeArray<byte>(pixels, Allocator.Temp);
            var output = new float[4 * 4 * 3];

            ImageSampler.SampleLetterbox(image, 4, 2, Letterbox.Fit(4, 2, 4), output);

            Assert.AreEqual(0f, output[0], 1e-5f, "the top row is padding");
            Assert.AreEqual(200f / 255f, output[(1 * 4 + 1) * 3], 1e-4f, "the image's first row");
            Assert.AreEqual(0f, output[(3 * 4 + 1) * 3], 1e-5f, "the bottom row is padding");
        }

        [Test]
        public void HandPosition_IsFitFromItsShapeHoweverItsTurned([Values(0f, 80f)] float turnDegrees)
        {
            // A made-up hand shape, turned about the vertical (80 degrees is nearly edge-on).
            const float focal = 500f;
            var principal = new Vector2(320f, 180f);
            var position = new Vector3(0.05f, -0.03f, 0.4f);
            var turn = Quaternion.Euler(0f, turnDegrees, 0f);
            var metric = new Vector3[21];
            for (var i = 0; i < 21; i++)
                metric[i] = turn * new Vector3((i % 5 - 2) * 0.02f, (i / 5 - 2) * 0.02f, (i % 3 - 1) * 0.01f);
            var truth = metric.Select(m => m + position).ToArray();
            var image = truth.Select(t => new Vector2(t.x, t.y) * focal / t.z + principal).ToArray();

            var fit = HandGeometry.FitPosition(image, metric, HandGeometry.All, new Vector2(focal, focal), principal, out var error);
            Assert.That((fit - position).magnitude, Is.LessThan(1e-4f), $"fit {fit}");
            fit = HandGeometry.FitPosition(image, metric, HandGeometry.Palm, new Vector2(focal, focal), principal, out error);
            Assert.That((fit - position).magnitude, Is.LessThan(1e-4f), $"fit {fit}");
            Assert.That(error, Is.LessThan(0.01f));

            var joints = new Vector3[21];
            HandGeometry.PlaceJoints(image, metric, fit.z, new Vector2(focal, focal), principal, joints);
            for (var i = 0; i < 21; i++)
                Assert.That((joints[i] - truth[i]).magnitude, Is.LessThan(1e-3f), $"joint {i}");
        }

        [Test]
        public void OneEuroFilter_HoldsStillValuesAndFollowsFastOnes()
        {
            // 15 samples a second, as the hand tracker gives them.
            const double step = 1.0 / 15.0;
            var still = new OneEuroFilter(1f, 4f);
            still.Filter(Vector3.zero, 0.0);
            var afterJump = still.Filter(new Vector3(0.05f, 0f, 0f), step);
            Assert.That(afterJump.x, Is.GreaterThan(0f).And.LessThan(0.03f), "a sudden 5 cm jump is mostly held back");

            // A hand moving steadily at 1 m/s: with beta the filter lags far less than without.
            var fast = new OneEuroFilter(1f, 4f);
            var plain = new OneEuroFilter(1f, 0f);
            Vector3 fastValue = default, plainValue = default;
            for (var i = 0; i <= 15; i++)
            {
                var value = new Vector3((float)(i * step), 0f, 0f);
                fastValue = fast.Filter(value, i * step);
                plainValue = plain.Filter(value, i * step);
            }
            Assert.That(1f - fastValue.x, Is.LessThan((1f - plainValue.x) * 0.5f));

            still.Reset();
            Assert.AreEqual(0.3f, still.Filter(new Vector3(0.3f, 0f, 0f), 10.0).x, 1e-6f, "after a reset the next value is taken as it is");
        }

        [Test]
        public void Pinch_StartsCloserThanItEnds()
        {
            var pinch = new PinchDetector(0.025f, 0.045f);

            Assert.IsFalse(pinch.Update(0.06f));
            Assert.IsFalse(pinch.Update(0.03f), "between the thresholds an open hand stays open");
            Assert.IsTrue(pinch.Update(0.02f));
            Assert.IsTrue(pinch.Update(0.04f), "between the thresholds a pinch holds");
            Assert.IsFalse(pinch.Update(0.05f));

            pinch.Update(0.01f);
            pinch.Reset();
            Assert.IsFalse(pinch.IsPinched);
        }

        [Test]
        public void PalmSize_StaysTheSameAsTheHandTurns()
        {
            var shape = new Vector3[21];
            for (var i = 0; i < 21; i++)
                shape[i] = new Vector3((i % 5 - 2) * 0.02f, (i / 5 - 2) * 0.02f, (i % 3 - 1) * 0.01f);
            var turned = shape.Select(p => Quaternion.Euler(30f, 70f, 10f) * p).ToArray();

            Assert.AreEqual(HandGeometry.PalmSize(shape), HandGeometry.PalmSize(turned), 1e-5f);
        }

        [Test]
        public void ImageAxes_FollowTheDisplayMatrix()
        {
            // Texture UV is screen UV with v flipped: the image's rows run down the screen.
            var flipped = Matrix4x4.identity;
            flipped.m11 = -1f;
            flipped.m21 = 1f;
            HandGeometry.ImageAxesOnScreen(flipped, out var imageX, out var imageY);
            AssertClose(Vector2.right, imageX);
            AssertClose(Vector2.down, imageY);

            // Texture u runs up the screen and v across it.
            var turned = Matrix4x4.identity;
            turned.m00 = 0f;
            turned.m01 = 1f;
            turned.m10 = 1f;
            turned.m11 = 0f;
            HandGeometry.ImageAxesOnScreen(turned, out imageX, out imageY);
            AssertClose(Vector2.up, imageX);
            AssertClose(Vector2.right, imageY);
        }

        [Test]
        public void ImageAxesVariants_AreEightDistinctRightAngledPairs()
        {
            var pairs = Enumerable.Range(1, 8).Select(v =>
            {
                HandGeometry.ImageAxesVariant(v, out var x, out var y);
                Assert.AreEqual(0f, Vector2.Dot(x, y), 1e-5f, $"variant {v}");
                return (x, y);
            }).ToArray();
            Assert.AreEqual(8, pairs.Distinct().Count());
        }

        static void AssertPixel(float[] output, int size, int x, int y, int imageX, int imageY, string message)
        {
            var i = (y * size + x) * 3;
            Assert.AreEqual(imageX * 30 / 255f, output[i], 1e-4f, message);
            Assert.AreEqual(imageY * 30 / 255f, output[i + 1], 1e-4f, message);
        }

        static void AssertClose(Vector2 expected, Vector2 actual, string message = "", float tolerance = 1e-5f) =>
            Assert.That((actual - expected).magnitude, Is.LessThan(tolerance), $"{message} expected {expected}, got {actual}");    }
}
