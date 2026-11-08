using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class PassthroughTests
    {
        [Test]
        public void CroppedDisplayTransform_WidensToTheWholeImage()
        {
            // A 4:3 image on a wider screen: all of its width, the middle 60% of its height.
            var display = Transform(new Vector2(1f, 0f), new Vector2(0f, 0.6f), new Vector2(0f, 0.2f));
            Assert.IsTrue(PassthroughController.TryWidenDisplayTransform(display, out var widened, out var min, out var max));

            AssertClose(new Vector2(0f, -1f / 3f), min);
            AssertClose(new Vector2(1f, 4f / 3f), max);
            AssertClose(new Vector2(0f, 0f), Apply(widened, new Vector2(0f, 0f)));
            AssertClose(new Vector2(1f, 1f), Apply(widened, new Vector2(1f, 1f)));
        }

        [Test]
        public void RotatedDisplayTransform_StillCoversEveryCorner()
        {
            // The image turned a quarter turn and cropped: tex = (0.2 + 0.6 v, 1 - u).
            var display = Transform(new Vector2(0f, -1f), new Vector2(0.6f, 0f), new Vector2(0.2f, 1f));
            Assert.IsTrue(PassthroughController.TryWidenDisplayTransform(display, out var widened, out _, out _));

            var corners = new HashSet<Vector2Int>();
            foreach (var screen in new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one })
            {
                var texture = Apply(widened, screen);
                var corner = new Vector2Int(Mathf.RoundToInt(texture.x), Mathf.RoundToInt(texture.y));
                AssertClose(corner, texture);
                corners.Add(corner);
            }
            Assert.AreEqual(4, corners.Count, "each screen corner lands on a different image corner");
        }

        // The ARCore shader's convention: tex = u * (m00, m01) + v * (m10, m11) + (m20, m21).
        static Matrix4x4 Transform(Vector2 perU, Vector2 perV, Vector2 offset)
        {
            var matrix = Matrix4x4.identity;
            matrix.m00 = perU.x;
            matrix.m01 = perU.y;
            matrix.m10 = perV.x;
            matrix.m11 = perV.y;
            matrix.m20 = offset.x;
            matrix.m21 = offset.y;
            return matrix;
        }

        static Vector2 Apply(Matrix4x4 m, Vector2 uv) =>
            new(uv.x * m.m00 + uv.y * m.m10 + m.m20, uv.x * m.m01 + uv.y * m.m11 + m.m21);

        static void AssertClose(Vector2 expected, Vector2 actual) =>
            Assert.That((actual - expected).magnitude, Is.LessThan(1e-5f), $"expected {expected}, got {actual}");
    }
}
