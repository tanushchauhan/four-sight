using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Interaction
{
    // Makes a world-space canvas's UI respond to the GazePointer. Use this instead of a
    // GraphicRaycaster: that would let screen taps hit world-space UI at the tapped spot. It also
    // registers in edit mode, so gaze raycasts work in EditMode tests.
    [ExecuteAlways]
    [RequireComponent(typeof(Canvas))]
    public class GazeCanvas : MonoBehaviour
    {
        // Graphics this close along the ray count as one plane and are ordered by drawing order.
        const float k_SamePlaneDistance = 0.0001f;

        static readonly List<GazeCanvas> s_Active = new();
        static readonly List<CanvasGroup> s_Groups = new();

        Canvas m_Canvas;

        public static IReadOnlyList<GazeCanvas> Active => s_Active;

        void OnEnable()
        {
            m_Canvas = GetComponent<Canvas>();
            s_Active.Add(this);
        }

        void OnDisable() => s_Active.Remove(this);

        // The topmost raycast target the ray hits within maxDistance, following GraphicRaycaster's
        // rules: graphics facing away are skipped, and so are those under a CanvasGroup that doesn't
        // block raycasts.
        public bool Raycast(Ray ray, float maxDistance, out Graphic hit, out float hitDistance)
        {
            hit = null;
            hitDistance = maxDistance;
            var graphics = GraphicRegistry.GetRaycastableGraphicsForCanvas(m_Canvas);
            for (var i = 0; i < graphics.Count; i++)
            {
                var graphic = graphics[i];
                if (!graphic.raycastTarget || !graphic.isActiveAndEnabled || graphic.canvasRenderer.cull)
                    continue;

                var rect = graphic.rectTransform;
                if (Vector3.Dot(ray.direction, rect.forward) <= 0f)
                    continue;
                if (!new Plane(rect.forward, rect.position).Raycast(ray, out var distance) || distance > maxDistance)
                    continue;
                if (!rect.rect.Contains(rect.InverseTransformPoint(ray.GetPoint(distance))) || !BlocksRaycasts(rect))
                    continue;

                if (hit != null)
                {
                    var samePlane = Mathf.Abs(distance - hitDistance) <= k_SamePlaneDistance;
                    if (samePlane ? !IsDrawnAfter(rect, hit.rectTransform) : distance > hitDistance)
                        continue;
                }

                hit = graphic;
                hitDistance = distance;
            }

            return hit != null;
        }

        static bool BlocksRaycasts(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                t.GetComponents(s_Groups);
                foreach (var group in s_Groups)
                {
                    if (!group.enabled)
                        continue;
                    if (!group.blocksRaycasts)
                        return false;
                    if (group.ignoreParentGroups)
                        return true;
                }
            }
            return true;
        }

        // UI draws in hierarchy order, so later siblings and children cover what came before.
        static bool IsDrawnAfter(Transform a, Transform b)
        {
            if (a.IsChildOf(b))
                return true;
            if (b.IsChildOf(a))
                return false;
            return BranchUnderSharedAncestor(a, b).GetSiblingIndex() > BranchUnderSharedAncestor(b, a).GetSiblingIndex();
        }

        // The ancestor of t (or t itself) whose parent also contains other.
        static Transform BranchUnderSharedAncestor(Transform t, Transform other)
        {
            while (t.parent != null && !other.IsChildOf(t.parent))
                t = t.parent;
            return t;
        }
    }
}
