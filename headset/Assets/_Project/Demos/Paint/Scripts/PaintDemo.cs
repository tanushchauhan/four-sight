using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Demos.Paint
{
    // Paint in the air with your hand, like Tilt Brush. Pinch anywhere to paint: the stroke follows
    // the point between your thumb and index fingertip for as long as the pinch lasts. A small ball
    // there shows the brush's color and size before you start. The tool panel, floating to your left,
    // picks the color, brush (Ink, Tube, Glow) and size, and has Undo, Reset (press twice), the room
    // (VR or passthrough); look at it and pinch. The lobby is on the settings panel. A pinch looking at the
    // panel clicks rather than paints, so Core's own panel is hidden: a stroke drawn in front of it
    // would otherwise press its buttons. Strokes are kept in the room's space (under the XR Origin),
    // so they stay put as tracking corrects itself, and are removed on leaving. Runs in 6DoF, starting
    // in passthrough (painting in your own room); Room switches to VR, on a dark floor grid. Logs [Paint].
    public class PaintDemo : Demo, IPinchHandler
    {
        static readonly Color[] k_Colors =
        {
            Color.white,
            new(1f, 0.25f, 0.25f),
            new(1f, 0.6f, 0.15f),
            new(1f, 0.92f, 0.2f),
            new(0.3f, 0.95f, 0.4f),
            new(0.2f, 0.9f, 1f),
            new(0.3f, 0.45f, 1f),
            new(0.95f, 0.35f, 0.95f),
        };
        static readonly (string label, float width)[] k_Sizes = { ("S", 0.004f), ("M", 0.01f), ("L", 0.022f) };
        static readonly string[] k_BrushNames = { "Ink", "Tube", "Glow" };
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        // Glow strokes are drawn a little wider, since their edges fade.
        const float k_GlowWidthScale = 1.6f;
        // The panel comes back to the left of the head after it's been more than this far from the
        // way the head faces, in degrees, for k_ReturnSeconds.
        const float k_PanelYaw = -40f;
        const float k_PanelAwayDegrees = 100f;
        const float k_ReturnSeconds = 1.5f;
        const float k_PanelDistance = 1.1f;
        const float k_ResetConfirmSeconds = 3f;
        // The brush tip moving farther than this in one frame is the tracker's estimate jumping (most
        // often in depth), not the hand: the stroke ends there and a new one starts where the tip is,
        // rather than a straight streak across the room. It allows at least k_MaxHandSpeed, so a
        // quick flick or a slow frame isn't cut. On the Pixel, streaks of 26-48 cm in a single step
        // showed up in the first session.
        const float k_JumpDistance = 0.06f;
        const float k_MaxHandSpeed = 3f;
        // A stroke shorter and quicker than these is a pinch that flickered on for a moment as the
        // fingers moved, not a mark: dropped. The first session had eight of 1-3 cm in about 0.1 s.
        const float k_BlipLength = 0.03f;
        const float k_BlipSeconds = 0.2f;

        [Tooltip("Opaque unlit, double-sided, for Ink and Tube.")]
        [SerializeField] Material m_SolidMaterial;
        [Tooltip("Additive unlit, for Glow.")]
        [SerializeField] Material m_GlowMaterial;

        CoreServices m_Core;
        Transform m_StrokesRoot;
        readonly List<PaintStroke> m_Strokes = new();
        PaintStroke m_Current;
        float m_StrokeStart;
        // Smooths the fingertips' point on top of the tracker's own smoothing: at a pinch the tips
        // hide each other, and their point jumps by several millimeters a result.
        readonly OneEuroFilter m_Smoothing = new(2f, 1.5f);
        bool m_WasTracking;

        // Blue to start (k_Colors[6]).
        int m_Color = 6;
        BrushStyle m_Brush = BrushStyle.Ink;
        int m_Size = 1;

        Transform m_Cursor;
        MaterialPropertyBlock m_CursorBlock;
        MeshRenderer m_CursorRenderer;

        RectTransform m_Panel;
        M3Button m_Reset;
        M3Button m_Room;
        readonly List<M3Button> m_Swatches = new();
        M3Button[] m_BrushButtons;
        M3Button[] m_SizeButtons;
        float m_AwayFor;
        float m_ResetArmedUntil = float.NegativeInfinity;

        float Width => k_Sizes[m_Size].width * (m_Brush == BrushStyle.Glow ? k_GlowWidthScale : 1f);

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            core.Placer.ObjectPrefab = null;
            PinchRouter.Handler = this;
            m_StrokesRoot = new GameObject("Paint Strokes").transform;
            // In the room's space, which the XR Origin is.
            m_StrokesRoot.SetParent(core.Boundary.transform, false);
            BuildCursor();
            BuildPanel();
            PlacePanel();
            UpdatePanel();
            Debug.Log("[Paint] Ready: pinch in the air to paint");
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Clear();
            Debug.Log($"[Paint] Leaving with {m_Strokes.Count} strokes");
            if (m_StrokesRoot != null)
                Destroy(m_StrokesRoot.gameObject);
        }

        // A pinch looking at a panel clicks; any other pinch paints.
        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source)
        {
            // Painting needs the hand's position, which only hand tracking has.
            if (source != PinchSource.Hand || (gazeTarget != null && gazeTarget.GetComponentInParent<Canvas>() != null))
                return false;
            BeginStroke(TipPosition(), log: true);
            return true;
        }

        public void EndPinch() => FinishStroke("");

        // Ends the current stroke, dropping it if it's a blip.
        void FinishStroke(string why)
        {
            if (m_Current == null)
                return;
            var stroke = m_Current;
            m_Current = null;
            var seconds = Time.unscaledTime - m_StrokeStart;
            var length = stroke.Length;
            if (stroke.PointCount < 2 || (length < k_BlipLength && seconds < k_BlipSeconds))
            {
                m_Strokes.Remove(stroke);
                Destroy(stroke.gameObject);
                if (stroke.PointCount >= 2)
                    Debug.Log($"[Paint] Dropped a blip: {length * 100f:F1} cm in {seconds:F2} s");
                return;
            }
            Debug.Log($"[Paint] Stroke {m_Strokes.Count}: {k_BrushNames[(int)m_Brush]}, {stroke.PointCount} points, " +
                      $"{length * 100f:F0} cm in {seconds:F1} s{why}");
        }

        void BeginStroke(Vector3 start, bool log)
        {
            var strokeObject = new GameObject($"Stroke {m_Strokes.Count + 1}", typeof(MeshFilter), typeof(MeshRenderer));
            strokeObject.transform.SetParent(m_StrokesRoot, false);
            var stroke = strokeObject.AddComponent<PaintStroke>();
            stroke.Begin(m_Brush, Width, k_Colors[m_Color], m_Brush == BrushStyle.Glow ? m_GlowMaterial : m_SolidMaterial);
            stroke.Add(start, TowardPainter(start));
            m_Strokes.Add(stroke);
            m_Current = stroke;
            if (log)
                m_StrokeStart = Time.unscaledTime;
        }

        void Update()
        {
            if (m_Core == null)
                return;
            var tracker = m_Core.HandTracker;
            var tracking = tracker != null && tracker.isActiveAndEnabled && tracker.IsTracking;
            if (tracking && !m_WasTracking)
                m_Smoothing.Reset();
            m_WasTracking = tracking;

            var tip = tracking ? TipPosition() : Vector3.zero;
            if (m_Current != null && tracking)
            {
                var jump = Vector3.Distance(tip, m_Current.LastPoint);
                if (jump > Mathf.Max(k_JumpDistance, k_MaxHandSpeed * Time.unscaledDeltaTime))
                {
                    FinishStroke($", ended where the tip jumped {jump * 100f:F0} cm");
                    BeginStroke(tip, log: true);
                }
                m_Current.Add(tip, TowardPainter(tip));
                // A very long stroke carries on as a new one from where it got to.
                if (m_Current.IsFull)
                    BeginStroke(m_Current.LastPoint, log: false);
            }

            m_Cursor.gameObject.SetActive(tracking);
            if (tracking)
            {
                m_Cursor.position = tip;
                m_Cursor.localScale = Vector3.one * Mathf.Max(Width, 0.008f);
            }

            UpdatePanelPlacement();
            if (m_Reset != null && Time.unscaledTime > m_ResetArmedUntil && m_Reset.Text != "Reset")
                m_Reset.Text = "Reset";
        }

        Vector3 TipPosition() => m_Smoothing.Filter(m_Core.HandTracker.PinchPoint, Time.unscaledTimeAsDouble);

        Vector3 TowardPainter(Vector3 point)
        {
            var toward = m_Core.Head.position - point;
            return toward.sqrMagnitude > 1e-6f ? toward.normalized : -m_Core.Head.forward;
        }

        void Undo()
        {
            if (m_Current != null || m_Strokes.Count == 0)
                return;
            var last = m_Strokes[^1];
            m_Strokes.RemoveAt(m_Strokes.Count - 1);
            Destroy(last.gameObject);
            Debug.Log($"[Paint] Undo: {m_Strokes.Count} strokes left");
        }

        // The first press asks, the second within k_ResetConfirmSeconds clears everything. (Not named
        // Reset: Unity calls that in the Editor when the component is added.)
        void ClearAll()
        {
            if (Time.unscaledTime > m_ResetArmedUntil)
            {
                m_ResetArmedUntil = Time.unscaledTime + k_ResetConfirmSeconds;
                m_Reset.Text = "Sure?";
                return;
            }
            m_ResetArmedUntil = float.NegativeInfinity;
            m_Reset.Text = "Reset";
            EndPinch();
            foreach (var stroke in m_Strokes)
                Destroy(stroke.gameObject);
            Debug.Log($"[Paint] Reset: cleared {m_Strokes.Count} strokes");
            m_Strokes.Clear();
        }

        void ToggleRoom()
        {
            m_Core.Passthrough.Toggle();
            UpdatePanel();
        }

        void BuildCursor()
        {
            var cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cursor.name = "Brush Tip";
            Destroy(cursor.GetComponent<Collider>());
            cursor.transform.SetParent(transform, false);
            m_CursorRenderer = cursor.GetComponent<MeshRenderer>();
            m_CursorRenderer.sharedMaterial = m_SolidMaterial;
            m_CursorRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_CursorBlock = new MaterialPropertyBlock();
            m_Cursor = cursor.transform;
            cursor.SetActive(false);
        }

        // A Material panel, laid out in dp: the colors as swatches, the brush and size as segmented
        // buttons, then Undo, Reset (which asks "Sure?" itself) and the room toggle.
        void BuildPanel()
        {
            m_Panel = WorldUi.Panel("Paint Tools", transform, new Vector2(440f, 364f), k_PanelDistance);

            for (var i = 0; i < k_Colors.Length; i++)
            {
                var index = i;
                var position = new Vector2(-96f + (i % 4) * 64f, 134f - (i / 4) * 60f);
                m_Swatches.Add(M3.Swatch($"Color {i}", m_Panel, k_Colors[i], position, 48f, () => SetColor(index)));
            }
            m_BrushButtons = M3.Segments("Brush", m_Panel, k_BrushNames, new Vector2(0f, 6f), 128f, index => SetBrush((BrushStyle)index));
            var sizes = new string[k_Sizes.Length];
            for (var i = 0; i < sizes.Length; i++)
                sizes[i] = k_Sizes[i].label;
            m_SizeButtons = M3.Segments("Size", m_Panel, sizes, new Vector2(0f, -58f), 128f, SetSize);

            const float width = 124f;
            M3.Button("Undo", m_Panel, "Undo", M3Button.Variant.Tonal, new Vector2(-134f, -130f), Undo, "undo", width);
            m_Reset = M3.Button("Reset", m_Panel, "Reset", M3Button.Variant.Tonal, new Vector2(0f, -130f), ClearAll, "delete", width);
            m_Room = M3.Button("Room", m_Panel, "Room", M3Button.Variant.Tonal, new Vector2(134f, -130f), ToggleRoom, "visibility", width);
        }

        void SetColor(int index)
        {
            m_Color = index;
            UpdatePanel();
        }

        void SetBrush(BrushStyle brush)
        {
            m_Brush = brush;
            UpdatePanel();
        }

        void SetSize(int index)
        {
            m_Size = index;
            UpdatePanel();
        }

        // Marks the chosen color, brush and size, and colors the brush tip to match.
        void UpdatePanel()
        {
            for (var i = 0; i < m_Swatches.Count; i++)
                m_Swatches[i].Selected = i == m_Color;
            for (var i = 0; i < m_BrushButtons.Length; i++)
                m_BrushButtons[i].Selected = i == (int)m_Brush;
            for (var i = 0; i < m_SizeButtons.Length; i++)
                m_SizeButtons[i].Selected = i == m_Size;
            // Selected while the room shows (passthrough).
            m_Room.Selected = m_Core.Passthrough.IsOn;
            m_CursorBlock.SetColor(k_BaseColor, k_Colors[m_Color]);
            m_CursorRenderer.SetPropertyBlock(m_CursorBlock);
        }


        // To the left of where the head faces, a little below the eyes, turned to face it.
        void PlacePanel()
        {
            var head = m_Core.Head;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
            var position = head.position + yaw * Quaternion.Euler(8f, k_PanelYaw, 0f) * Vector3.forward * k_PanelDistance;
            m_Panel.SetPositionAndRotation(position, Quaternion.LookRotation(position - head.position, Vector3.up));
            m_AwayFor = 0f;
        }

        // Brings the panel back beside the head once the user has turned well away from it for a while.
        void UpdatePanelPlacement()
        {
            var head = m_Core.Head;
            var toPanel = Vector3.ProjectOnPlane(m_Panel.position - head.position, Vector3.up);
            var facing = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (toPanel.sqrMagnitude < 1e-4f || facing.sqrMagnitude < 1e-4f)
                return;
            m_AwayFor = Vector3.Angle(toPanel, facing) > k_PanelAwayDegrees ? m_AwayFor + Time.unscaledDeltaTime : 0f;
            if (m_AwayFor >= k_ReturnSeconds && m_Current == null)
                PlacePanel();
        }
    }
}
