using System.Collections.Generic;
using CardboardXR.Demos.Paint;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Paint.Tests
{
    public class StrokeMeshTests
    {
        static List<Vector3> Line(int count, Vector3 step)
        {
            var points = new List<Vector3>();
            for (var i = 0; i < count; i++)
                points.Add(step * i);
            return points;
        }

        static List<Vector3> Facing(int count, Vector3 direction)
        {
            var facing = new List<Vector3>();
            for (var i = 0; i < count; i++)
                facing.Add(direction);
            return facing;
        }

        [Test]
        public void OnePointMakesNothing()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            StrokeMesh.Build(Line(1, Vector3.right), Facing(1, Vector3.back), 0.01f, BrushStyle.Tube, vertices, triangles);
            Assert.AreEqual(0, vertices.Count);
            Assert.AreEqual(0, triangles.Count);
        }

        [Test]
        public void RibbonIsTwoVerticesAPointAndFacesThePainter()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            // Drawn left to right in front of a painter behind it (toward -z).
            StrokeMesh.Build(Line(10, Vector3.right * 0.01f), Facing(10, Vector3.back), 0.02f, BrushStyle.Ink, vertices, triangles);
            Assert.AreEqual(20, vertices.Count);
            Assert.AreEqual(9 * 6, triangles.Count);
            // Mid-stroke, full width, spread up and down: flat to the painter.
            var across = vertices[11] - vertices[10];
            Assert.AreEqual(0.02f, across.magnitude, 1e-5f);
            Assert.AreEqual(0f, across.z, 1e-6f);
            Assert.AreEqual(0f, across.x, 1e-6f);
        }

        [Test]
        public void TubeRingsHaveTheRadiusAndAreAcrossTheStroke()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var points = Line(12, Vector3.forward * 0.01f);
            StrokeMesh.Build(points, Facing(12, Vector3.up), 0.02f, BrushStyle.Tube, vertices, triangles);
            Assert.AreEqual(12 * StrokeMesh.TubeSides, vertices.Count);
            Assert.AreEqual(11 * StrokeMesh.TubeSides * 6, triangles.Count);
            var ring = 6 * StrokeMesh.TubeSides;
            for (var s = 0; s < StrokeMesh.TubeSides; s++)
            {
                var offset = vertices[ring + s] - points[6];
                Assert.AreEqual(0.01f, offset.magnitude, 1e-5f);
                Assert.AreEqual(0f, Vector3.Dot(offset, Vector3.forward), 1e-6f);
            }
        }

        [Test]
        public void EndsTaper()
        {
            Assert.Less(StrokeMesh.Taper(0, 20), StrokeMesh.Taper(2, 20));
            Assert.Less(StrokeMesh.Taper(19, 20), StrokeMesh.Taper(17, 20));
            Assert.AreEqual(1f, StrokeMesh.Taper(10, 20), 1e-6f);
            Assert.Greater(StrokeMesh.Taper(0, 20), 0f);
        }

        [Test]
        public void GlowIsATube()
        {
            var tube = new List<Vector3>();
            var glow = new List<Vector3>();
            var triangles = new List<int>();
            StrokeMesh.Build(Line(5, Vector3.up * 0.01f), Facing(5, Vector3.back), 0.01f, BrushStyle.Tube, tube, triangles);
            StrokeMesh.Build(Line(5, Vector3.up * 0.01f), Facing(5, Vector3.back), 0.01f, BrushStyle.Glow, glow, triangles);
            Assert.AreEqual(tube, glow);
        }
    }
}
