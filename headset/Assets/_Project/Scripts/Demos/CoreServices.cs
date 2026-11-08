using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using UnityEngine;

namespace CardboardXR.Demos
{
    // The Core scene's shared pieces, for demo scenes to reach: a reference saved in one scene can't
    // point into another, so a demo finds them here. Lives in the Core scene, which never unloads.
    public class CoreServices : MonoBehaviour
    {
        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] StereoRig m_Rig;
        [SerializeField] PassthroughController m_Passthrough;
        [SerializeField] RoomBoundary m_Boundary;
        [SerializeField] AnchorPlacer m_Placer;
        [SerializeField] GazePointer m_Gaze;
        [SerializeField] HandTracker m_HandTracker;
        [Tooltip("Runs pinch-to-click in 3DoF (see PinchSensor), and holds the user's switch for it.")]
        [SerializeField] PinchInput m_PinchInput;
        [SerializeField] HandInteractorRig m_HandRig;
        [SerializeField] DemoManager m_Demos;
        [Tooltip("The app's settings panel, summoned by looking at a palm and pinching.")]
        [SerializeField] SettingsSummoner m_Settings;

        public static CoreServices Instance { get; private set; }

        public HeadPoseFusion Fusion => m_Fusion;
        public StereoRig Rig => m_Rig;
        public PassthroughController Passthrough => m_Passthrough;
        public RoomBoundary Boundary => m_Boundary;
        public AnchorPlacer Placer => m_Placer;
        public GazePointer Gaze => m_Gaze;
        public HandTracker HandTracker => m_HandTracker;
        public PinchInput PinchInput => m_PinchInput;
        // Hand grabbing and pinch clicks in 6DoF; a demo takes pinches through PinchRouter.Handler.
        public HandInteractorRig HandRig => m_HandRig;
        public DemoManager Demos => m_Demos;
        public SettingsSummoner Settings => m_Settings;
        // The head: where the user is and which way they look.
        public Transform Head => m_Fusion.transform;

        public CoreState State => new(
            m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof, m_Passthrough.IsOn, m_HandTracker.enabled);

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // Switches to the state, in an order that holds: hands first, since they need nothing else;
        // then 6DoF, which passthrough needs; then passthrough, as a choice 6DoF won't undo.
        public void Apply(CoreState state)
        {
            m_HandTracker.enabled = state.Hands;
            if (!state.Passthrough)
                m_Passthrough.Choose(false);
            m_Fusion.SetMode(state.SixDof ? HeadPoseFusion.Mode.SixDof : HeadPoseFusion.Mode.ThreeDof);
            if (state.Passthrough)
                m_Passthrough.Choose(true);
        }
    }
}
