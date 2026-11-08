using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.Paint
{
    public enum BrushStyle
    {
        // A flat ribbon turned to face where the painter was when it was drawn, like Tilt Brush's ink.
        Ink,
        // A round tube.
        Tube,
        // A tube drawn additively, so it glows against the dark and brightens where strokes cross.
        Glow,
    }

    // Builds a stroke's mesh from the points along it. Both shapes taper over their first and last
    // k_TaperPoints points, so strokes start and end in a point, as a brush's do.
    public static class StrokeMesh
    {
        public const int TubeSides = 8;
        public const int TaperPoints = 4;
        // The thinnest a tapered end gets, as a share of the full width.
        const float k_TaperMin = 0.25f;

        // points: the stroke's centerline. facing: for Ink, the direction toward the painter at each
        // point (the ribbon's face turns that way); unused for tubes. width: across a ribbon, or a
        // tube's diameter.
        public static void Build(IReadOnlyList<Vector3> points, IReadOnlyList<Vector3> facing, float width, BrushStyle style,
            List<Vector3> vertices, List<int> triangles)
        {
            vertices.Clear();
            triangles.Clear();
            if (points.Count < 2)
                return;
            if (style == BrushStyle.Ink)
                BuildRibbon(points, facing, width, vertices, triangles);
            else
                BuildTube(points, width, vertices, triangles);
        }

        public static float Taper(int index, int count)
        {
            var fromEnd = Mathf.Min(index, count - 1 - index);
            return Mathf.Lerp(k_TaperMin, 1f, Mathf.Clamp01(fromEnd / (float)TaperPoints));
        }

        public static Vector3 Tangent(IReadOnlyList<Vector3> points, int i)
        {
            var before = points[Mathf.Max(0, i - 1)];
            var after = points[Mathf.Min(points.Count - 1, i + 1)];
            var tangent = after - before;
            return tangent.sqrMagnitude > 1e-12f ? tangent.normalized : Vector3.forward;
        }

        static void BuildRibbon(IReadOnlyList<Vector3> points, IReadOnlyList<Vector3> facing, float width,
            List<Vector3> vertices, List<int> triangles)
        {
            var lastSide = Vector3.right;
            for (var i = 0; i < points.Count; i++)
            {
                var side = Vector3.Cross(Tangent(points, i), facing[i]);
                // Drawn straight toward or away from the painter, the face has no direction; keep the last.
                side = side.sqrMagnitude > 1e-8f ? side.normalized : lastSide;
                lastSide = side;
                var half = side * (width * Taper(i, points.Count) / 2f);
                vertices.Add(points[i] - half);
                vertices.Add(points[i] + half);
                if (i == 0)
                    continue;
                var a = (i - 1) * 2;
                triangles.Add(a);
                triangles.Add(a + 2);
                triangles.Add(a + 1);
                triangles.Add(a + 1);
                triangles.Add(a + 2);
                triangles.Add(a + 3);
            }
        }

        // Rings around the centerline, their orientation carried along from one to the next (parallel
        // transport) so the tube doesn't twist.
        static void BuildTube(IReadOnlyList<Vector3> points, float width, List<Vector3> vertices, List<int> triangles)
        {
            var tangent = Tangent(points, 0);
            var normal = Vector3.Cross(tangent, Mathf.Abs(tangent.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (var i = 0; i < points.Count; i++)
            {
                tangent = Tangent(points, i);
                normal = Vector3.ProjectOnPlane(normal, tangent);
                normal = normal.sqrMagnitude > 1e-8f ? normal.normalized
                    : Vector3.Cross(tangent, Mathf.Abs(tangent.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var binormal = Vector3.Cross(tangent, normal);
                var radius = width * Taper(i, points.Count) / 2f;
                for (var s = 0; s < TubeSides; s++)
                {
                    var angle = s * 2f * Mathf.PI / TubeSides;
                    vertices.Add(points[i] + (Mathf.Cos(angle) * normal + Mathf.Sin(angle) * binormal) * radius);
                }
                if (i == 0)
                    continue;
                var ring = i * TubeSides;
                var previous = ring - TubeSides;
                for (var s = 0; s < TubeSides; s++)
                {
                    var next = (s + 1) % TubeSides;
                    triangles.Add(previous + s);
                    triangles.Add(ring + s);
                    triangles.Add(previous + next);
                    triangles.Add(previous + next);
                    triangles.Add(ring + s);
                    triangles.Add(ring + next);
                }
            }
        }
    }
}
