using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.Paint
{
    // One stroke, growing while the pinch lasts. Points closer than k_MinSpacing to the last one are
    // skipped, and the mesh is rebuilt only when one is added. Its points are in world space when
    // added and kept in the parent's space, so a stroke stays fixed in the room as tracking corrects
    // it (the parent is the XR Origin; see PaintDemo).
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class PaintStroke : MonoBehaviour
    {
        public const float k_MinSpacing = 0.004f;
        // A stroke longer than this many points is ended and a new one begun where it left off, so
        // rebuilding the mesh never gets slow.
        public const int k_MaxPoints = 600;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        readonly List<Vector3> m_Points = new();
        readonly List<Vector3> m_Facing = new();
        readonly List<Vector3> m_Vertices = new();
        readonly List<int> m_Triangles = new();
        Mesh m_Mesh;
        float m_Width;
        BrushStyle m_Style;

        public int PointCount => m_Points.Count;
        public bool IsFull => m_Points.Count >= k_MaxPoints;
        public Vector3 LastPoint => m_Points.Count > 0 ? transform.TransformPoint(m_Points[^1]) : transform.position;

        public float Length
        {
            get
            {
                var length = 0f;
                for (var i = 1; i < m_Points.Count; i++)
                    length += Vector3.Distance(m_Points[i - 1], m_Points[i]);
                return length;
            }
        }

        public void Begin(BrushStyle style, float width, Color color, Material material)
        {
            m_Style = style;
            m_Width = width;
            m_Mesh = new Mesh { name = "Stroke" };
            m_Mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = m_Mesh;
            var renderer = GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var block = new MaterialPropertyBlock();
            block.SetColor(k_BaseColor, color);
            renderer.SetPropertyBlock(block);
        }

        // Adds a world point, with the direction toward the painter there. False if it was too close
        // to the last one to add.
        public bool Add(Vector3 world, Vector3 towardPainter)
        {
            var local = transform.InverseTransformPoint(world);
            if (m_Points.Count > 0 && (local - m_Points[^1]).sqrMagnitude < k_MinSpacing * k_MinSpacing)
                return false;
            m_Points.Add(local);
            m_Facing.Add(transform.InverseTransformDirection(towardPainter));
            StrokeMesh.Build(m_Points, m_Facing, m_Width, m_Style, m_Vertices, m_Triangles);
            m_Mesh.Clear();
            m_Mesh.SetVertices(m_Vertices);
            m_Mesh.SetTriangles(m_Triangles, 0);
            m_Mesh.RecalculateBounds();
            return true;
        }

        void OnDestroy()
        {
            if (m_Mesh != null)
                Destroy(m_Mesh);
        }
    }
}
