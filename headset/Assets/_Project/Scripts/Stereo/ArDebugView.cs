using UnityEngine;

namespace CardboardXR.Stereo
{
    // A debug view for checking that ARCore's detected surfaces line up with the real ones. With the
    // "arView" launch option (1) it replaces the stereo view with the AR camera, which shows the
    // camera feed full screen and draws only the boundary layer over it, with every surface's grid
    // showing. It forces 6DoF, since the camera has to run.
    public class ArDebugView : MonoBehaviour
    {
        [SerializeField] StereoRig m_Rig;
        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] RoomBoundary m_Boundary;
        [SerializeField] Camera m_ArCamera;
        [Tooltip("Components that only run in the AR view, such as the camera background and its pose driver.")]
        [SerializeField] Behaviour[] m_ArViewOnly;

        const float k_LogIntervalSeconds = 5f;

        bool m_Active;
        float m_NextLog;

        // After the other components' OnEnable, so the mode set here sticks.
        void Start()
        {
            if (LaunchOptions.GetInt("arView", 0) == 0)
                return;
            m_Active = true;

            m_Fusion.SetMode(HeadPoseFusion.Mode.SixDof);
            m_Rig.enabled = false;
            foreach (var behaviour in m_ArViewOnly)
                behaviour.enabled = true;
            m_ArCamera.enabled = true;
            m_Boundary.ShowAll = true;
            Debug.Log("[Room] AR view: camera feed with every detected surface's grid");
        }

        // The AR camera and the head both come from ARCore's device pose, so they should be a few
        // centimeters apart; the camera's parents show where any extra offset comes from.
        void LateUpdate()
        {
            if (!m_Active || Time.unscaledTime < m_NextLog)
                return;
            m_NextLog = Time.unscaledTime + k_LogIntervalSeconds;
            var camera = m_ArCamera.transform;
            var offset = camera.parent;
            var origin = offset != null ? offset.parent : null;
            Debug.Log($"[Room] AR view: camera at {camera.position:F2} (local {camera.localPosition:F2}), " +
                      $"head at {m_Fusion.transform.position:F2}, camera offset local {(offset != null ? offset.localPosition : Vector3.zero):F2}, " +
                      $"origin at {(origin != null ? origin.position : Vector3.zero):F2}, " +
                      $"camera fov {m_ArCamera.fieldOfView:F1} deg");
        }
    }
}
