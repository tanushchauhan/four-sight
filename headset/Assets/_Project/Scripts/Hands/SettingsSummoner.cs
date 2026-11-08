using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Hands
{
    // The app's settings panel (Core's toolbar), summoned as visionOS offers its controls: look at your
    // palm and a round gear button appears between your thumb and index finger, following the hand;
    // pinch while it shows and the panel opens in front of you. Its X button closes it, and so does
    // moving to another scene (Close). The panel is the same in every scene, so demos need no controls
    // of their own for what it does. While open, the panel follows the user lazily (Follow): always
    // M3Theme.k_OverlayDistance from the head, in front of any scene's panels, and turning to stay in
    // front once the head has turned past the panel's edge (DeadZone), so looking at any of its buttons,
    // even the outermost, doesn't make it slide away.
    //   Which hand pipeline is used follows the mode: in 6DoF HandTracker, whose 3D joints place the
    //   button; in 3DoF PinchSensor, which only sees the hand in the image, so the button goes in that
    //   direction at k_ButtonDistance (the hand isn't drawn in 3DoF anyway). Either way the palm test
    //   is PalmGesture's, on the image. The gear's pinch opens the panel and nothing else
    //   (TryConsumePinch, which PinchRouter asks first). The phone-screen menu can open it too
    //   (SettingsPanelHub), for when there's no hand. Put it on the XR Origin. Logs [Settings].
    [DefaultExecutionOrder(-115)]
    public class SettingsSummoner : MonoBehaviour
    {
        // Where the 3DoF button goes, in meters along the hand's direction.
        const float k_ButtonDistance = 0.4f;
        // The 3DoF camera's view, as PinchSensor's 4:3 frames see it, in degrees.
        const float k_CameraHorizontal = 64f;
        const float k_CameraVertical = 50f;
        // The button sits this far in front of the hand, toward the eyes, in meters.
        const float k_InFront = 0.03f;
        // The panel sits at least this far below the line of sight (level, at the head's height), and
        // farther if the panel above it (the app library, at eye level) needs the room: k_PanelGap
        // between them. Both face the head; the library stands upright, the panel tilts up to it.
        public const float k_MinPanelDrop = 15f;
        const float k_PanelGap = 1.5f;
        // The panel turns to follow the head once the head is past its edge by this much, in degrees,
        // and keeps turning until it's within k_FollowSettled. A fixed 15 degrees was less than the
        // toolbar's own half-width (about 23), so aiming at its outer buttons set it moving away.
        public const float k_FollowMargin = 10f;
        const float k_FollowSettled = 1f;
        // How it catches up: a damped spring that eases out of rest and into place, taking about this
        // long, no faster than k_FollowMaxSpeed degrees a second. A plain exponential catch-up (about
        // 0.12 s) felt locked to the head the moment the dead zone was left.
        public const float k_FollowSmoothTime = 0.5f;
        const float k_FollowMaxSpeed = 90f;
        // How long the panel takes to follow the head's position, for a step or a lean.
        const float k_PositionSmoothTime = 0.25f;
        // Thumb and index joints between which the button sits.
        const int k_ThumbJoint = 3;
        const int k_IndexJoint = 6;
        const int k_MiddleKnuckle = 9;
        // How much each palm-facing frame moves the upright vote (see Upright).
        const float k_VoteRate = 0.1f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] HandTracker m_Tracker;
        [SerializeField] PinchSensor m_Sensor;
        [Tooltip("The settings panel, hidden until summoned.")]
        [SerializeField] RectTransform m_Panel;
        [Tooltip("The gear button's canvas, shown while a palm faces the user.")]
        [SerializeField] RectTransform m_Button;
        [Tooltip("Optional: shown at eye level above the panel, as part of it (the app library).")]
        [SerializeField] RectTransform m_Above;

        static SettingsSummoner s_Instance;

        readonly PalmFacingState m_Palm = new();
        readonly OneEuroFilter m_ButtonFilter = new(2f, 2f);
        bool m_ButtonShown;

        // The panel's heading around the user, in degrees, whether it's turning to catch up and how fast,
        // and where the head is taken to be, following it smoothly.
        float m_PanelYaw;
        bool m_Following;
        float m_YawSpeed;
        Vector3 m_Center;
        Vector3 m_CenterVelocity;
        // Whether PinchSensor's image is upright, as the hand shows it: +1 upright, -1 upside down. A
        // safeguard for a camera that turns its image without saying so: turning doesn't change the
        // palm test, only where the button goes. (The iPhone's image was mirrored instead, which
        // PinchSensor now corrects.)
        float m_UprightVote;
        bool m_LoggedUpright;

        public bool IsOpen => m_Panel != null && M3Fade.IsShown(m_Panel.gameObject);

        // Whether the panel is open, for PinchRouter: demos don't take pinches meanwhile.
        public static bool IsPanelOpen => s_Instance != null && s_Instance.IsOpen;

        // The panel opened (true) or closed (false). A demo can pause while it's open, as Orb Rush does.
        public event System.Action<bool> OpenChanged;

        void Awake()
        {
            s_Instance = this;
            m_Panel.gameObject.SetActive(false);
            m_Button.gameObject.SetActive(false);
        }

        void OnEnable() => SettingsPanelHub.OpenRequested += Open;

        void OnDisable()
        {
            SettingsPanelHub.OpenRequested -= Open;
            ShowButton(false);
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
        }

        // Called by PinchRouter for each pinch, before anything else: true if the gear was showing, in which case
        // the pinch opened the panel and should do nothing more.
        public static bool TryConsumePinch()
        {
            if (s_Instance == null || !s_Instance.m_ButtonShown)
                return false;
            Debug.Log("[Settings] Pinched the palm button");
            s_Instance.Open();
            return true;
        }

        // Opens the panel in front of the user, a little below eye level, facing them.
        public void Open()
        {
            m_PanelYaw = HeadYaw();
            m_Following = false;
            m_YawSpeed = 0f;
            m_Center = m_Fusion.transform.position;
            m_CenterVelocity = Vector3.zero;
            PlacePanel();
            var wasOpen = IsOpen;
            M3Fade.Show(m_Panel.gameObject);
            if (!wasOpen)
            {
                Debug.Log("[Settings] Opened");
                OpenChanged?.Invoke(true);
            }
            ShowButton(false);
            m_Palm.Reset();
        }

        // The panel's X button, and any move to another scene.
        public void Close()
        {
            if (!IsOpen)
                return;
            M3Fade.Hide(m_Panel.gameObject);
            Debug.Log("[Settings] Closed");
            OpenChanged?.Invoke(false);
        }

        // Which way the head faces around the vertical, in degrees.
        float HeadYaw()
        {
            var flat = Vector3.ProjectOnPlane(m_Fusion.transform.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : m_PanelYaw;
        }

        // How far the head can turn from the panel before it follows: past its edge, as seen from the
        // head, by k_FollowMargin. It narrows in the lobby, where the toolbar has fewer items.
        float DeadZone()
        {
            var halfWidth = m_Panel.rect.width / 2f * m_Panel.lossyScale.x;
            return Mathf.Atan2(halfWidth, M3Theme.k_OverlayDistance) * Mathf.Rad2Deg + k_FollowMargin;
        }

        // Where the panel's heading should be next: unchanged while the head is within the dead zone,
        // then springing toward the head until it's nearly there and nearly still. Pure, for testing.
        public static float FollowYaw(float panelYaw, float headYaw, float deadZone, ref bool following, ref float speed,
            float deltaTime)
        {
            var off = Mathf.Abs(Mathf.DeltaAngle(panelYaw, headYaw));
            if (!following && off > deadZone)
                following = true;
            if (following && off < k_FollowSettled && Mathf.Abs(speed) < 5f)
            {
                following = false;
                speed = 0f;
            }
            if (!following)
                return panelYaw;
            return Mathf.SmoothDampAngle(panelYaw, headYaw, ref speed, k_FollowSmoothTime, k_FollowMaxSpeed, deltaTime);
        }

        // M3Theme.k_OverlayDistance from the head along the panel's heading, a little below eye level, facing it.
        void PlacePanel() => Place(m_Panel, m_Above, m_Center, m_PanelYaw);

        // How far below the line of sight the panel goes, in degrees, for a panel and the one above it
        // of these heights in dp: k_MinPanelDrop, or enough that they clear each other by k_PanelGap.
        public static float PanelDrop(float panelHeight, float aboveHeight)
        {
            var dpDegrees = Mathf.Atan(M3Theme.DpToMeters(1f, M3Theme.k_OverlayDistance) / M3Theme.k_OverlayDistance) * Mathf.Rad2Deg;
            return Mathf.Max(k_MinPanelDrop, (aboveHeight / 2f + panelHeight / 2f) * dpDegrees + k_PanelGap);
        }

        // The panel below the line of sight from a head at center, facing it, and what's above it at
        // eye level, upright and facing it too, both M3Theme.k_OverlayDistance away in the heading yaw.
        public static void Place(RectTransform panel, RectTransform above, Vector3 center, float yaw)
        {
            var drop = PanelDrop(panel.rect.height, above != null ? above.rect.height : 0f);
            var direction = Quaternion.Euler(drop, yaw, 0f) * Vector3.forward;
            panel.SetPositionAndRotation(center + direction * M3Theme.k_OverlayDistance, Quaternion.LookRotation(direction, Vector3.up));
            if (above == null)
                return;
            var ahead = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            above.SetPositionAndRotation(center + ahead * M3Theme.k_OverlayDistance, Quaternion.LookRotation(ahead, Vector3.up));
        }

        void LateUpdate()
        {
            if (!IsOpen)
                return;
            var deltaTime = Time.unscaledDeltaTime;
            m_PanelYaw = FollowYaw(m_PanelYaw, HeadYaw(), DeadZone(), ref m_Following, ref m_YawSpeed, deltaTime);
            m_Center = Vector3.SmoothDamp(m_Center, m_Fusion.transform.position, ref m_CenterVelocity, k_PositionSmoothTime,
                Mathf.Infinity, deltaTime);
            PlacePanel();
        }

        void Update()
        {
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            var seen = false;
            var facing = 0f;
            var place = Vector3.zero;
            if (sixDof && m_Tracker.isActiveAndEnabled && m_Tracker.IsTracking)
            {
                seen = TryFacing(m_Tracker.ImageLandmarks, m_Tracker.HandednessScore, out facing);
                var joints = m_Tracker.Joints;
                place = (joints[k_ThumbJoint] + joints[k_IndexJoint]) / 2f;
            }
            else if (!sixDof && m_Sensor.isActiveAndEnabled && m_Sensor.SeesHand)
            {
                seen = TryFacing(m_Sensor.ImageLandmarks, m_Sensor.RightHandScore, out facing);
                var size = m_Sensor.ImageSize;
                var landmarks = m_Sensor.ImageLandmarks;
                var between = (landmarks[k_ThumbJoint] + landmarks[k_IndexJoint]) / 2f;
                var fraction = new Vector2(between.x / size.x, between.y / size.y);
                if (seen && facing > PalmFacingState.k_Hide)
                    VoteUpright(landmarks);
                if (m_UprightVote < 0f)
                    fraction = Vector2.one - fraction;
                var ray = PalmGesture.Ray(fraction, k_CameraHorizontal, k_CameraVertical);
                var head = m_Fusion.transform;
                place = head.position + head.rotation * ray * k_ButtonDistance;
            }

            // Not while the panel's open: it's already there.
            if (m_Palm.Update(seen && !IsOpen, facing, Time.unscaledDeltaTime))
            {
                Debug.Log($"[Settings] Palm {(m_Palm.IsFacing ? "toward" : "away from")} the user " +
                          $"(facing {facing:F2}, {(sixDof ? "hand tracking" : "pinch sensor")})");
                ShowButton(m_Palm.IsFacing);
            }

            if (m_ButtonShown && seen)
                PlaceButton(place);
        }

        // A palm held up to look at has its fingers above its wrist, so in an upright image (y down) the
        // wrist is lower than the middle knuckle. The vote follows that, and settles which way up the
        // image is.
        void VoteUpright(System.Collections.Generic.IReadOnlyList<Vector2> landmarks)
        {
            var fingersUp = landmarks[PalmGesture.k_Wrist].y > landmarks[k_MiddleKnuckle].y ? 1f : -1f;
            m_UprightVote = Mathf.Clamp(m_UprightVote + fingersUp * k_VoteRate, -1f, 1f);
            if (!m_LoggedUpright && Mathf.Abs(m_UprightVote) >= 1f)
            {
                m_LoggedUpright = true;
                Debug.Log($"[Settings] The pinch sensor's image is {(m_UprightVote > 0f ? "upright" : "upside down")}, " +
                          "by where the wrist is when a palm is held up");
            }
        }

        static bool TryFacing(System.Collections.Generic.IReadOnlyList<Vector2> landmarks, float rightScore, out float facing)
        {
            facing = 0f;
            if (landmarks == null || landmarks.Count < 21)
                return false;
            facing = PalmGesture.Facing(landmarks[PalmGesture.k_Wrist], landmarks[PalmGesture.k_IndexKnuckle],
                landmarks[PalmGesture.k_PinkyKnuckle], rightScore >= 0.5f);
            return true;
        }

        void ShowButton(bool show)
        {
            m_ButtonShown = show;
            if (m_Button != null)
                M3Fade.SetShown(m_Button.gameObject, show);
            if (show)
                m_ButtonFilter.Reset();
        }

        // Between the thumb and index finger, a little toward the eyes, facing them.
        void PlaceButton(Vector3 hand)
        {
            var eyes = m_Fusion.transform.position;
            var toEyes = (eyes - hand).normalized;
            var position = m_ButtonFilter.Filter(hand + toEyes * k_InFront, Time.unscaledTimeAsDouble);
            m_Button.SetPositionAndRotation(position, Quaternion.LookRotation(position - eyes, Vector3.up));
        }
    }
}
