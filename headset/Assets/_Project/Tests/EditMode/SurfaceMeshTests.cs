using System.Collections.Generic;
using CardboardXR.Stereo;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class SurfaceMeshTests
    {
        static readonly Vector2[] k_Square = { new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f) };

        [Test]
        public void TheBoundaryFadesOutAndTheInsideIsSolid()
        {
            var vertices = new List<Vector3>();
            var fades = new List<Vector2>();
            var triangles = new List<int>();
            SurfaceMesh.Build(k_Square, Vector2.zero, 0.2f, vertices, fades, triangles);

            Assert.AreEqual(1 + 2 * k_Square.Length, vertices.Count);
            Assert.AreEqual(vertices.Count, fades.Count);
            Assert.AreEqual(9 * k_Square.Length, triangles.Count);
            Assert.AreEqual(1f, fades[0].x, "center");
            for (var i = 0; i < k_Square.Length; i++)
            {
                Assert.AreEqual(0f, fades[1 + 2 * i].x, "boundary");
                Assert.AreEqual(1f, fades[2 + 2 * i].x, "inset");
                Assert.AreEqual(new Vector3(k_Square[i].x, 0f, k_Square[i].y), vertices[1 + 2 * i]);
                // The inset point is the feather in from the boundary point, toward the center.
                Assert.AreEqual(0.2f, Vector3.Distance(vertices[1 + 2 * i], vertices[2 + 2 * i]), 1e-5f);
            }
        }

        [Test]
        public void ASmallPlaneIsntInsetPastItsMiddle()
        {
            var tiny = new[] { new Vector2(-0.05f, -0.05f), new Vector2(0.05f, -0.05f), new Vector2(0f, 0.05f) };
            var vertices = new List<Vector3>();
            SurfaceMesh.Build(tiny, Vector2.zero, 0.2f, vertices, new List<Vector2>(), new List<int>());
            for (var i = 0; i < tiny.Length; i++)
            {
                var boundary = vertices[1 + 2 * i];
                var inset = vertices[2 + 2 * i];
                Assert.Less(Vector3.Distance(boundary, inset), boundary.magnitude);
                Assert.Greater(inset.magnitude, 0f);
            }
        }
    }
}
