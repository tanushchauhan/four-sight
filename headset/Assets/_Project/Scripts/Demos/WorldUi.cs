using CardboardXR.Interaction;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Demos
{
    // Helpers for demos that build their panels at runtime with the Material kit (CardboardXR.UI.M3):
    // a panel the gaze pointer works (look, then pinch or tap the screen), laid out in dp, and where
    // to put it.
    public static class WorldUi
    {
        // A Material panel the gaze works, laid out in dp at its distance (see M3Canvas): a rounded surface
        // that stops the gaze, the surface container by default. Seen from the front, as GazeCanvas expects.
        public static RectTransform Panel(string name, Transform parent, Vector2 sizeDp, float distance, Color? color = null,
            float radius = M3Theme.k_ShapePanel)
        {
            var panel = M3.Canvas(name, parent, sizeDp, distance, color ?? M3Theme.SurfaceContainer, radius);
            panel.gameObject.AddComponent<GazeCanvas>();
            return panel;
        }

        // Puts a panel ahead of the head at a distance, turned yawDegrees from where it faces and
        // pitchDegrees above (+) or below (-) the eyes, facing it.
        public static void PlaceInFront(RectTransform panel, Transform head, float distance, float pitchDegrees, float yawDegrees = 0f)
        {
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
            var position = head.position + yaw * Quaternion.Euler(-pitchDegrees, yawDegrees, 0f) * Vector3.forward * distance;
            panel.SetPositionAndRotation(position, Quaternion.LookRotation(position - head.position, Vector3.up));
        }

        // An empty area to group elements, such as one page of a panel.
        public static RectTransform CreateGroup(string name, RectTransform parent) => M3.Group(name, parent);
    }
}
