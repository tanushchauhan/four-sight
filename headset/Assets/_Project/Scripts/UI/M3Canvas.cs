using UnityEngine;

namespace CardboardXR.UI
{
    // Sizes a world-space canvas so one canvas unit is one dp at its distance from the viewer (see
    // M3Theme): UI is laid out in dp and looks the same size wherever it's placed. Set when it wakes,
    // so the "uiScale" launch option applies to canvases saved in a scene too.
    [RequireComponent(typeof(Canvas))]
    public class M3Canvas : MonoBehaviour
    {
        [Tooltip("How far from the viewer it's meant to be seen, in meters.")]
        [SerializeField] float m_Distance = M3Theme.k_PanelDistance;

        public float Distance => m_Distance;

        void Awake() => Apply();

        public void SetDistance(float distance)
        {
            m_Distance = distance;
            Apply();
        }

        public void Apply() => transform.localScale = Vector3.one * M3Theme.DpToMeters(1f, m_Distance);

        // A dp on this canvas, in meters: for moving it toward the viewer by a spatial elevation.
        public float Meters(float dp) => M3Theme.DpToMeters(dp, m_Distance);
    }
}
