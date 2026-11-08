using CardboardXR.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Uses ARCore's detected planes in 6DoF. It lines the virtual floor up with the real one, and
    // shows the surfaces it finds while scanning; between scans only the surface pointed at shows
    // (PlacementSurface's highlight). m_WarnWhenNear adds a warning in VR between scans, like a
    // headset's guardian boundary: a grid on real surfaces within reach across the room, except at
    // floor level. The
    // planes' GameObjects live in ARCore's session space under this XR Origin, so the origin follows
    // HeadPoseFusion's session-to-world pose. Put it on the XR Origin with the ARPlaneManager; the
    // planes use the CardboardXR/Boundary shader.
    // Plane detection only runs while scanning: running all the time, ARCore's plane finding and the
    // constant outline and mesh updates take CPU time the hand tracker and the frame rate need. A scan
    // starts each time 6DoF does and ends a few seconds after the floor is found, which is all most
    // demos need. A demo that uses other surfaces (DemoRequirements.ScanSurfaces) asks for a room
    // scan instead (ScanRoom), which ends once it stops finding anything new. Either way a scan gives
    // up after m_MaxScanSeconds. Only time ARCore spends tracking counts, since nothing can be found
    // before it is; ARCore can take minutes to start in a plain room. While scanning, every surface
    // found shows its grid. Planes stay in use between scans; they just stop being refined. The
    // warning is off in passthrough, where the room can be seen. The settings panel's Scan Room
    // (ToggleScan) starts a room scan or ends one; it only shows in 6DoF, since there's no scanning
    // without AR.
    [DefaultExecutionOrder(-80)]
    public class RoomBoundary : MonoBehaviour
    {
        static readonly int k_HeadId = Shader.PropertyToID("_BoundaryHead");
        static readonly int k_RadiusId = Shader.PropertyToID("_BoundaryRadius");
        static readonly int k_FloorId = Shader.PropertyToID("_BoundaryFloor");
        static readonly int k_ScanId = Shader.PropertyToID("_BoundaryScan");
        static readonly int k_WaveId = Shader.PropertyToID("_BoundaryWave");
        static readonly int k_WorldToSessionId = Shader.PropertyToID("_BoundaryWorldToSession");
        // A scan shows surfaces in a wave spreading out from where it began at this speed, in meters a
        // second of tracking (like an odradek scan's), and they fade away over k_HideSeconds when it
        // ends rather than vanishing.
        const float k_WaveSpeed = 2.5f;
        const float k_ShowSeconds = 0.3f;
        const float k_HideSeconds = 1.2f;
        // Matches the shader: surfaces less than this above the floor count as floor.
        const float k_FloorBand = 0.1f;
        const float k_FloorCheckSeconds = 0.5f;
        // A [Room] status line every this many floor checks (5 s).
        const int k_ChecksPerStatus = 10;
        // Floor changes smaller than this aren't logged.
        const float k_FloorLogChange = 0.02f;
        // How long a scan runs on after finding the floor, to fill it out, in seconds.
        const float k_SettleSeconds = 3f;
        const float k_HandPauseSeconds = 8f;
        // A room scan tracks for at least k_MinScanSeconds, and ends once it has found
        // no more than k_NewArea square meters of new surface in k_NothingNewSeconds of tracking.
        const float k_MinScanSeconds = 5f;
        const float k_NothingNewSeconds = 5f;
        const float k_NewArea = 0.1f;

        static readonly List<(bool horizontalUp, float height, float area)> s_Planes = new();

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] ARPlaneManager m_Planes;
        [Tooltip("Between scans in VR, show the grid on surfaces within the warning distance. Off: surfaces only show while scanning and under the pointer.")]
        [SerializeField] bool m_WarnWhenNear;
        [Tooltip("With the warning on, real surfaces closer than this to the head, across the room, show their grid.")]
        [SerializeField] float m_WarningDistance = 1f;
        [Tooltip("The smallest horizontal plane taken for the floor, in square meters.")]
        [SerializeField] float m_MinFloorArea = 0.5f;
        [Tooltip("How far below the eyes the floor must be, so tables and beds don't count.")]
        [SerializeField] float m_MinFloorDepth = 0.9f;
        [Tooltip("Optional: the settings panel's Scan Room, shown in 6DoF and selected while scanning.")]
        [SerializeField] M3Button m_ScanButton;
        [Tooltip("The longest a scan runs, in seconds.")]
        [SerializeField] float m_MaxScanSeconds = 60f;
        [Tooltip("Optional: with passthrough on the room can be seen, so surfaces don't warn.")]
        [SerializeField] PassthroughController m_Passthrough;

        bool m_WasSixDof;
        bool m_Scanning;
        // How much the scan's surfaces show, easing to 1 while scanning and to 0 after, and where the
        // scan's wave started, in world space.
        float m_ScanShown;
        Vector3 m_WaveOrigin;
        // Whether this scan ends by itself once the floor is found.
        bool m_StopWhenFloorFound;
        float m_ScanStart;
        float m_FloorFoundAt;
        // How many surfaces ARCore is tracking, and their total area in square meters.
        int m_TrackedSurfaces;
        float m_SurfaceArea;
        // The most area this scan has seen, how long ARCore has tracked during it, and for how much of
        // that the area hasn't grown.
        float m_MostArea;
        float m_ScanTime;
        float m_TimeSinceGrowth;

        float m_NextFloorCheck;
        float m_LoggedFloor = float.NaN;
        // The floor's height in session space, once found.
        float? m_Floor;
        int m_Checks;
        // The head's closest approach, across the room, to a surface above the floor since the last
        // status line, and that surface.
        float m_Nearest = float.PositiveInfinity;
        ARPlane m_NearestPlane;

        // Shows every surface's grid, however far away and at floor level too; for ArDebugView.
        public bool ShowAll { get; set; }

        // Whether a scan for surfaces is running.
        public bool IsScanning => m_Scanning;

        // What's been found: tracked surfaces big enough to count, their area in square meters, and
        // whether one is the floor.
        public int SurfaceCount => m_TrackedSurfaces;
        public float SurfaceArea => m_SurfaceArea;
        public bool HasFloor => m_Floor.HasValue;

        // Whether hand tracking should pause, leaving the CPU to plane detection: at the start of a scan,
        // for at most k_HandPauseSeconds of tracking. A floor scan with the floor never in view paused
        // the hands for a whole minute, which looked like hand tracking failing.
        public bool PausesHands => m_Scanning && m_ScanTime < k_HandPauseSeconds;

        // Whether a room scan (ScanRoom) has run its course since 6DoF began, so another isn't needed.
        public bool HasScannedRoom { get; private set; }

        void OnEnable()
        {
            // The "boundaryCm" launch option turns the warning on, at that distance.
            var boundaryCm = LaunchOptions.GetInt("boundaryCm", 0);
            if (boundaryCm > 0)
            {
                m_WarnWhenNear = true;
                m_WarningDistance = boundaryCm / 100f;
            }
        }

        void OnDisable() => Shader.SetGlobalFloat(k_RadiusId, 0f);

        void Update()
        {
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (sixDof != m_WasSixDof)
            {
                m_WasSixDof = sixDof;
                // Out of 6DoF the origin stops following ARCore, so the planes and their colliders
                // are hidden until ARCore is back.
                foreach (var plane in m_Planes.trackables)
                    plane.gameObject.SetActive(sixDof);
                if (!sixDof)
                    HasScannedRoom = false;
                // The floor scan, unless a room scan was asked for as 6DoF started.
                if (sixDof && !(m_Scanning && !m_StopWhenFloorFound))
                    StartScan(true);
            }
            UpdateScan(sixDof);
            if (m_ScanButton != null)
            {
                if (m_ScanButton.gameObject.activeSelf != sixDof)
                    m_ScanButton.gameObject.SetActive(sixDof);
                if (m_ScanButton.Selected != m_Scanning)
                    m_ScanButton.Selected = m_Scanning;
            }
            if (!sixDof)
            {
                m_ScanShown = 0f;
                Shader.SetGlobalFloat(k_RadiusId, 0f);
                Shader.SetGlobalFloat(k_ScanId, 0f);
                return;
            }

            var sessionToWorld = m_Fusion.SessionToWorld;
            transform.SetPositionAndRotation(sessionToWorld.position, sessionToWorld.rotation);
            var head = m_Fusion.transform.position;
            Shader.SetGlobalVector(k_HeadId, head);
            Shader.SetGlobalMatrix(k_WorldToSessionId, transform.worldToLocalMatrix);
            // While scanning, every surface shows, so the user can see what's been found: revealed by
            // the wave, which only spreads while tracking, since nothing is found before.
            var showScan = ShowAll || m_Scanning;
            m_ScanShown = Mathf.MoveTowards(m_ScanShown, showScan ? 1f : 0f,
                Time.unscaledDeltaTime / (showScan ? k_ShowSeconds : k_HideSeconds));
            Shader.SetGlobalFloat(k_ScanId, Mathf.SmoothStep(0f, 1f, m_ScanShown));
            Shader.SetGlobalVector(k_WaveId, new Vector4(m_WaveOrigin.x, m_WaveOrigin.y, m_WaveOrigin.z,
                ShowAll ? 1000f : m_ScanTime * k_WaveSpeed));
            if (!m_WarnWhenNear || (m_Passthrough != null && m_Passthrough.IsOn))
            {
                // Otherwise only scans and the surface pointed at show.
                Shader.SetGlobalFloat(k_RadiusId, 0f);
            }
            else
            {
                Shader.SetGlobalFloat(k_RadiusId, m_WarningDistance);
                // The session-to-world rotation is a turn about the vertical, so heights just
                // shift. Until the floor is found, assume it's as deep as the shallowest floor
                // PickFloor takes.
                Shader.SetGlobalFloat(k_FloorId, m_Floor.HasValue ? sessionToWorld.position.y + m_Floor.Value : head.y - m_MinFloorDepth);
            }

            if (Time.unscaledTime < m_NextFloorCheck)
                return;
            m_NextFloorCheck = Time.unscaledTime + k_FloorCheckSeconds;
            UpdateFloor();
        }

        // A room scan: finds surfaces until nothing new turns up, not just the floor. Turns a floor
        // scan that's running into one; asked for before 6DoF starts, it runs once it does.
        public void ScanRoom()
        {
            if (m_Scanning && !m_StopWhenFloorFound)
                return;
            if (m_Scanning)
            {
                m_StopWhenFloorFound = false;
                Debug.Log("[Room] Scanning the whole room, not just for the floor");
                return;
            }
            StartScan(false);
        }

        // The settings panel's Scan Room: a room scan, or the end of the one running.
        public void ToggleScan()
        {
            if (m_Scanning)
                FinishScan();
            else
                ScanRoom();
        }

        // Ends a scan now, as the lobby's Skip does.
        public void FinishScan()
        {
            if (m_Scanning)
                StopScan("ended early");
        }

        void StartScan(bool stopWhenFloorFound)
        {
            m_Scanning = true;
            m_StopWhenFloorFound = stopWhenFloorFound;
            m_WaveOrigin = m_Fusion.transform.position;
            m_ScanStart = Time.unscaledTime;
            m_FloorFoundAt = m_Floor.HasValue ? m_ScanStart : -1f;
            m_MostArea = m_SurfaceArea;
            m_ScanTime = 0f;
            m_TimeSinceGrowth = 0f;
            Debug.Log(stopWhenFloorFound ? "[Room] Scanning for surfaces until the floor is found" : "[Room] Scanning the whole room");
        }

        void StopScan(string how)
        {
            m_Scanning = false;
            // Ended early still counts once the floor's in: that's the lobby's Done.
            if (!m_StopWhenFloorFound && (how != "ended early" || m_Floor.HasValue))
                HasScannedRoom = true;
            Debug.Log($"[Room] Scan {how} after {m_ScanTime:F0} s of tracking ({Time.unscaledTime - m_ScanStart:F0} s in all): " +
                      $"{m_TrackedSurfaces} surfaces tracked " +
                      $"({m_Planes.trackables.count} planes in all), {m_SurfaceArea:F1} m2, " + (m_Floor.HasValue ? "floor found" : "no floor"));
        }

        void UpdateScan(bool sixDof)
        {
            var now = Time.unscaledTime;
            if (m_Scanning && sixDof)
            {
                // Only time ARCore spends tracking counts: nothing can be found until it is.
                if (m_Fusion.IsArTracking)
                {
                    m_ScanTime += Time.unscaledDeltaTime;
                    m_TimeSinceGrowth += Time.unscaledDeltaTime;
                }
                if (m_Floor.HasValue && m_FloorFoundAt < 0f)
                    m_FloorFoundAt = now;
                if (m_StopWhenFloorFound && m_FloorFoundAt >= 0f && now - m_FloorFoundAt >= k_SettleSeconds)
                    StopScan("found the floor");
                // Not before the floor: finding nothing at all yet isn't finding nothing new. (A
                // room scan once ended after 5 s with no surfaces.)
                else if (!m_StopWhenFloorFound && m_Floor.HasValue && m_ScanTime >= k_MinScanSeconds &&
                         m_TimeSinceGrowth >= k_NothingNewSeconds)
                    StopScan("found nothing new");
                else if (m_ScanTime >= m_MaxScanSeconds)
                    StopScan("gave up");
            }
            var detecting = sixDof && m_Scanning;
            if (m_Planes.enabled != detecting)
                m_Planes.enabled = detecting;
        }

        void UpdateFloor()
        {
            s_Planes.Clear();
            var vertical = 0;
            var head = m_Fusion.transform.position;
            foreach (var plane in m_Planes.trackables)
            {
                if (plane.subsumedBy != null || plane.trackingState != TrackingState.Tracking)
                    continue;
                if (plane.alignment == PlaneAlignment.Vertical)
                    vertical++;
                // The planes sit under the origin's trackables parent, which has no offset, so their
                // local position is in session space.
                var height = plane.transform.localPosition.y;
                var atFloor = plane.alignment == PlaneAlignment.HorizontalUp && m_Floor.HasValue && height < m_Floor.Value + k_FloorBand;
                if (!atFloor)
                {
                    var across = AcrossToPlane(plane, head);
                    if (across < m_Nearest)
                    {
                        m_Nearest = across;
                        m_NearestPlane = plane;
                    }
                }
                s_Planes.Add((plane.alignment == PlaneAlignment.HorizontalUp, height, plane.size.x * plane.size.y));
            }

            var eyesHeight = m_Fusion.EyesInSession.y;
            var floor = PickFloor(s_Planes, eyesHeight, m_MinFloorArea, m_MinFloorDepth);
            if (floor.HasValue)
                m_Floor = floor;
            m_SurfaceArea = 0f;
            foreach (var (_, _, area) in s_Planes)
                m_SurfaceArea += area;
            m_TrackedSurfaces = s_Planes.Count;
            if (m_SurfaceArea > m_MostArea + k_NewArea)
            {
                m_MostArea = m_SurfaceArea;
                m_TimeSinceGrowth = 0f;
            }
            if (++m_Checks % k_ChecksPerStatus == 0)
                LogStatus(vertical, eyesHeight, floor);
            if (!floor.HasValue)
                return;

            m_Fusion.SetFloor(floor.Value);
            if (float.IsNaN(m_LoggedFloor) || Mathf.Abs(floor.Value - m_LoggedFloor) > k_FloorLogChange)
            {
                m_LoggedFloor = floor.Value;
                Debug.Log($"[Room] Floor {eyesHeight - floor.Value:F2} m below the eyes, from {s_Planes.Count} planes");
            }
        }

        // Every upward-facing horizontal plane, as depth below the eyes and area, for tuning the floor
        // pick.
        void LogStatus(int vertical, float eyesHeight, float? floor)
        {
            var horizontal = new System.Text.StringBuilder();
            foreach (var (horizontalUp, height, area) in s_Planes)
            {
                if (horizontalUp)
                    horizontal.Append($" {eyesHeight - height:F2} m below/{area:F1} m2");
            }
            var nearest = float.IsPositiveInfinity(m_Nearest) ? "none" : $"{m_Nearest:F2} m";
            // The floor in use, which is kept once found, not just this check's pick: one check can
            // miss it, as when the eyes dip within m_MinFloorDepth of it, without it being lost.
            var floorText = !m_Floor.HasValue ? "no floor yet"
                : floor.HasValue ? $"floor {eyesHeight - m_Floor.Value:F2} m below the eyes"
                : $"floor {eyesHeight - m_Floor.Value:F2} m below the eyes (kept from an earlier check)";
            Debug.Log($"[Room] {s_Planes.Count} planes, {vertical} vertical; {floorText}" +
                      $"; nearest surface above the floor {nearest} across (warning " +
                      (m_WarnWhenNear ? $"within {m_WarningDistance:F2} m)" : "off)") +
                      (horizontal.Length > 0 ? $"; horizontal:{horizontal}" : ""));

            // Where the nearest surface and the head are in session and world space, to check the
            // planes are placed like the head. Their offsets should differ only by the origin's turn.
            if (m_NearestPlane != null)
            {
                var plane = m_NearestPlane.transform;
                var origin = transform;
                var head = m_Fusion.transform.position;
                var planeInOrigin = origin.InverseTransformPoint(plane.position);
                Debug.Log($"[Room] Nearest surface: parent \"{(plane.parent != null ? plane.parent.name : "none")}\", " +
                          $"in origin {planeInOrigin:F2} vs eyes in session {m_Fusion.EyesInSession:F2}; " +
                          $"world {plane.position:F2} vs head {head:F2}; origin at {origin.position:F2} " +
                          $"turned {origin.eulerAngles.y:F0} deg, session-to-world {m_Fusion.SessionToWorld.position:F2} " +
                          $"turned {m_Fusion.SessionToWorld.rotation.eulerAngles.y:F0} deg");
            }
            m_Nearest = float.PositiveInfinity;
            m_NearestPlane = null;
        }

        // Horizontal distance from a world point to the plane's rectangle, which ARCore's boundary
        // polygon fits inside, as the shader measures it.
        static float AcrossToPlane(ARPlane plane, Vector3 point)
        {
            var local = plane.transform.InverseTransformPoint(point);
            var center = plane.centerInPlaneSpace;
            var extents = plane.extents;
            var closest = plane.transform.TransformPoint(new Vector3(
                Mathf.Clamp(local.x, center.x - extents.x, center.x + extents.x),
                0f,
                Mathf.Clamp(local.z, center.y - extents.y, center.y + extents.y)));
            return Vector2.Distance(new Vector2(point.x, point.z), new Vector2(closest.x, closest.z));
        }

        // The height of the lowest upward-facing horizontal plane that is at least minArea square
        // meters and at least minDepth below the eyes, or null if there is none.
        public static float? PickFloor(IReadOnlyList<(bool horizontalUp, float height, float area)> planes,
            float eyesHeight, float minArea, float minDepth)
        {
            float? floor = null;
            foreach (var (horizontalUp, height, area) in planes)
            {
                if (!horizontalUp || area < minArea || height > eyesHeight - minDepth)
                    continue;
                if (!floor.HasValue || height < floor.Value)
                    floor = height;
            }
            return floor;
        }
    }
}
