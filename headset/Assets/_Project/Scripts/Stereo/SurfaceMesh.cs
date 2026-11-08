using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // A detected plane's mesh, made so the CardboardXR/Boundary shader can fade its edges rather than
    // cut them off: AR Foundation's own mesh (ARPlaneMeshVisualizer) only has the boundary's points,
    // so nothing inside it can tell how near the edge it is. This one has a ring inset m_Feather from
    // the boundary: the boundary's points carry a fade of 0 in UV1.x, the ring's and the middle's 1,
    // and the shader's alpha follows it. Shown while the plane is tracked and not merged into
    // another. PlaneCollider turns it off for planes too small to matter. Goes on the plane prefab.
    [RequireComponent(typeof(ARPlane), typeof(MeshFilter), typeof(MeshRenderer))]
    public class SurfaceMesh : MonoBehaviour
    {
        // The inset ring never goes more than this fraction of the way to the middle, so a small plane
        // still shows something.
        const float k_MaxInset = 0.8f;

        [Tooltip("How far in from the edge the surface fades in, in meters.")]
        [SerializeField] float m_Feather = 0.15f;

        static readonly List<Vector2> s_Boundary = new();
        static readonly List<Vector3> s_Vertices = new();
        static readonly List<Vector2> s_Fades = new();
        static readonly List<int> s_Triangles = new();

        ARPlane m_Plane;
        MeshRenderer m_Renderer;
        Mesh m_Mesh;

        void Awake()
        {
            m_Plane = GetComponent<ARPlane>();
            m_Renderer = GetComponent<MeshRenderer>();
            m_Mesh = new Mesh { name = "Surface" };
            m_Mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        }

        void OnEnable()
        {
            m_Plane.boundaryChanged += OnBoundaryChanged;
            Rebuild();
        }

        void OnDisable()
        {
            m_Plane.boundaryChanged -= OnBoundaryChanged;
            m_Renderer.enabled = false;
        }

        void OnDestroy() => Destroy(m_Mesh);

        void OnBoundaryChanged(ARPlaneBoundaryChangedEventArgs args) => Rebuild();

        // Tracking and merging can change without the boundary changing.
        void Update() => UpdateVisibility();

        void Rebuild()
        {
            s_Boundary.Clear();
            foreach (var point in m_Plane.boundary)
                s_Boundary.Add(point);
            m_Mesh.Clear();
            if (s_Boundary.Count >= 3)
            {
                Build(s_Boundary, m_Plane.centerInPlaneSpace, m_Feather, s_Vertices, s_Fades, s_Triangles);
                m_Mesh.SetVertices(s_Vertices);
                m_Mesh.SetUVs(1, s_Fades);
                m_Mesh.SetTriangles(s_Triangles, 0);
                m_Mesh.RecalculateBounds();
            }
            m_Renderer.sortingOrder = SortingOrder(transform.position.y);
            UpdateVisibility();
        }

        // Higher surfaces draw first, so a table's shadow pass claims its pixels before the floor's
        // (see CardboardXR/ShadowCatcher). Always negative, so surfaces still draw before UI panels.
        public static int SortingOrder(float height) => -10000 - Mathf.RoundToInt(Mathf.Clamp(height, -50f, 50f) * 100f);

        void UpdateVisibility()
        {
            var visible = enabled && m_Mesh.vertexCount > 0 && m_Plane.trackingState != TrackingState.None &&
                          m_Plane.subsumedBy == null;
            if (m_Renderer.enabled != visible)
                m_Renderer.enabled = visible;
        }

        // The mesh for a boundary in plane space (x and y, which are the plane's local x and z) around
        // its center: the center, then for each boundary point the point itself and one inset toward
        // the center by feather (at most k_MaxInset of the way). Triangles fan from the center to the
        // inset ring, and a strip joins the ring to the boundary. Fades go in x: 0 at the boundary,
        // 1 inside. Pure, for testing.
        public static void Build(IReadOnlyList<Vector2> boundary, Vector2 center, float feather, List<Vector3> vertices,
            List<Vector2> fades, List<int> triangles)
        {
            vertices.Clear();
            fades.Clear();
            triangles.Clear();
            vertices.Add(new Vector3(center.x, 0f, center.y));
            fades.Add(Vector2.right);
            var count = boundary.Count;
            for (var i = 0; i < count; i++)
            {
                var point = boundary[i];
                var toCenter = center - point;
                var distance = toCenter.magnitude;
                var inset = distance > 1e-5f ? Mathf.Min(feather, distance * k_MaxInset) / distance : 0f;
                var inner = point + toCenter * inset;
                vertices.Add(new Vector3(point.x, 0f, point.y));
                fades.Add(Vector2.zero);
                vertices.Add(new Vector3(inner.x, 0f, inner.y));
                fades.Add(Vector2.right);
            }
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                int outer = 1 + 2 * i, inner = 2 + 2 * i, nextOuter = 1 + 2 * next, nextInner = 2 + 2 * next;
                triangles.Add(0);
                triangles.Add(inner);
                triangles.Add(nextInner);
                triangles.Add(outer);
                triangles.Add(nextOuter);
                triangles.Add(nextInner);
                triangles.Add(outer);
                triangles.Add(nextInner);
                triangles.Add(inner);
            }
        }
    }
}
