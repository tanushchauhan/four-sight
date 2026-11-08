using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Keeps a detected plane out of the way until it's big enough to matter, and gives it a thin box
    // collider over its rectangle so the gaze and the hand's ray can land on it. ARCore finds many
    // small surfaces, such as books, chair seats and bits of wall, that clutter the view and catch the
    // pointer; below m_MinArea a plane has no grid, no mesh updates and no collider. A MeshCollider
    // would follow the outline exactly, but its physics shape is rebuilt on the main thread every time
    // ARCore changes the outline, which stalls frames; the box is only resized. Its top face is the
    // plane's surface. Goes on the plane prefab, after its SurfaceMesh.
    [RequireComponent(typeof(ARPlane), typeof(BoxCollider))]
    public class PlaneCollider : MonoBehaviour
    {
        const float k_Thickness = 0.01f;

        [Tooltip("Planes smaller than this, in square meters, are left out.")]
        [SerializeField] float m_MinArea = 0.2f;

        ARPlane m_Plane;
        BoxCollider m_Box;
        SurfaceMesh m_Visualizer;

        void Awake()
        {
            m_Plane = GetComponent<ARPlane>();
            m_Box = GetComponent<BoxCollider>();
            m_Visualizer = GetComponent<SurfaceMesh>();
            m_Plane.boundaryChanged += OnBoundaryChanged;
            Fit();
        }

        void OnDestroy() => m_Plane.boundaryChanged -= OnBoundaryChanged;

        void OnBoundaryChanged(ARPlaneBoundaryChangedEventArgs args) => Fit();

        void Fit()
        {
            var size = m_Plane.size;
            var usable = size.x * size.y >= m_MinArea;
            // The mesh shows only while it's enabled, and stops rebuilding while it isn't; enabled
            // again, it rebuilds.
            if (m_Visualizer != null && m_Visualizer.enabled != usable)
                m_Visualizer.enabled = usable;
            m_Box.enabled = usable;
            if (!usable)
                return;

            // Plane space's x and y are the plane's local x and z.
            var center = m_Plane.centerInPlaneSpace;
            m_Box.center = new Vector3(center.x, -k_Thickness / 2f, center.y);
            m_Box.size = new Vector3(size.x, k_Thickness, size.y);
        }
    }
}
