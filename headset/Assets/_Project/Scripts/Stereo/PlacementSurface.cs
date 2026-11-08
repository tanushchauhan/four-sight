using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Makes a detected plane somewhere to put things. While the gaze is on it, a soft spot of it shows
    // in the highlight color where the gaze meets it (the shader's _GazePoint), even in VR where it
    // can't otherwise be seen; not the whole surface, which lit up the floor whenever it was looked at; the highlight eases in and
    // out (k_HighlightSeconds) rather than popping, and a plane eases in when it's first found or comes
    // back with 6DoF (k_AppearSeconds). A tap there anchors a
    // new object at that spot, through the AnchorPlacer on the XR Origin above it, and so does a pinch:
    // it clicks whatever the gaze is on (see HandInteractorRig). Goes on the plane prefab, with a
    // PlaneCollider.
    [RequireComponent(typeof(ARPlane), typeof(Renderer))]
    public class PlacementSurface : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        static readonly int k_HighlightId = Shader.PropertyToID("_Highlight");
        static readonly int k_AppearId = Shader.PropertyToID("_Appear");
        static readonly int k_GazePointId = Shader.PropertyToID("_GazePoint");
        static HeadPoseFusion s_Head;
        const float k_HighlightSeconds = 0.15f;
        const float k_AppearSeconds = 0.6f;

        ARPlane m_Plane;
        Renderer m_Renderer;
        MaterialPropertyBlock m_Block;
        AnchorPlacer m_Placer;
        bool m_Highlighted;
        float m_Highlight;
        // Starts at 0, so the plane eases in when it's first shown.
        float m_Appear;

        // Found on first use, since the plane may be parented under the XR Origin after Awake.
        AnchorPlacer Placer => m_Placer != null ? m_Placer : m_Placer = GetComponentInParent<AnchorPlacer>();

        void Awake()
        {
            m_Plane = GetComponent<ARPlane>();
            m_Renderer = GetComponent<Renderer>();
            m_Block = new MaterialPropertyBlock();
            Apply();
        }

        void Update()
        {
            // Hidden (too small yet, or merged), it eases in again when it's next shown.
            if (!m_Renderer.enabled)
            {
                if (m_Appear > 0f)
                {
                    m_Appear = 0f;
                    Apply();
                }
                return;
            }
            var highlight = Mathf.MoveTowards(m_Highlight, m_Highlighted ? 1f : 0f, Time.unscaledDeltaTime / k_HighlightSeconds);
            var appear = Mathf.MoveTowards(m_Appear, 1f, Time.unscaledDeltaTime / k_AppearSeconds);
            // Where the gaze meets the surface, for the highlight's spot, while it shows.
            var gazeMoved = highlight > 0f && UpdateGazePoint();
            if (highlight == m_Highlight && appear == m_Appear && !gazeMoved)
                return;
            m_Highlight = highlight;
            m_Appear = appear;
            Apply();
        }

        Vector3 m_GazePoint;

        // The head's line of sight meeting this plane, in world space; whether it moved.
        bool UpdateGazePoint()
        {
            if (s_Head == null)
                s_Head = FindAnyObjectByType<HeadPoseFusion>();
            if (s_Head == null)
                return false;
            var head = s_Head.transform;
            if (!new Plane(transform.up, transform.position).Raycast(new Ray(head.position, head.forward), out var distance))
                return false;
            var point = head.position + head.forward * distance;
            if ((point - m_GazePoint).sqrMagnitude < 1e-6f)
                return false;
            m_GazePoint = point;
            return true;
        }

        void Apply()
        {
            m_Block.SetVector(k_GazePointId, m_GazePoint);
            // Eased, as the shader takes them in a straight line.
            m_Block.SetFloat(k_HighlightId, Mathf.SmoothStep(0f, 1f, m_Highlight));
            m_Block.SetFloat(k_AppearId, Mathf.SmoothStep(0f, 1f, m_Appear));
            m_Renderer.SetPropertyBlock(m_Block);
        }

        public void OnPointerEnter(PointerEventData eventData) => SetHighlighted(true);

        public void OnPointerExit(PointerEventData eventData) => SetHighlighted(false);

        public void OnPointerClick(PointerEventData eventData) => PlaceAt(eventData.pointerCurrentRaycast.worldPosition);

        // Anchors a new object at a world point on the surface.
        public void PlaceAt(Vector3 point)
        {
            if (Placer != null)
                Placer.Place(m_Plane, point);
        }

        public void SetHighlighted(bool highlighted) => m_Highlighted = highlighted;
    }
}
