using CardboardXR.Hands;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos.__NAME__
{
    // __TITLE__: __DESCRIPTION__ Logs [__NAME__].
    public class __NAME__Demo : Demo, IPinchHandler
    {
        CoreServices m_Core;
        RectTransform m_Panel;
        TMP_Text m_Hint;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            // Nothing is placed on surfaces unless the demo sets a prefab here (see PortalDemo).
            core.Placer.ObjectPrefab = null;
            // Pinches that no button takes come here first (TryBeginPinch). Remove this, and the
            // interface, if the demo only uses buttons.
            PinchRouter.Handler = this;
            BuildPanel();
            Debug.Log("[__NAME__] Ready");
        }

        // DemoManager puts back Core's state, placed objects and the pinch handler. Anything the demo
        // parented into Core (such as under core.Boundary.transform) or subscribed to there is the
        // demo's to undo here.
        public override void OnDemoExit(CoreServices core)
        {
            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Clear();
            Debug.Log("[__NAME__] Leaving");
        }

        // A pinch began, with the gaze on gazeTarget (or nothing): true to take it. A Camera pinch
        // (3DoF) has no hand position, so turn it down if the demo needs one.
        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source)
        {
            return false;
        }

        // The pinch this took ended: the fingers opened, or the hand was lost. Losing the hand isn't
        // letting go, so end what the pinch was doing cleanly rather than undoing it.
        public void EndPinch()
        {
        }

        // A small Material panel ahead and a little below the eyes, with what to do. The lobby is on
        // the settings panel, so it isn't needed here.
        void BuildPanel()
        {
            const float distance = 1.5f;
            m_Panel = WorldUi.Panel("__NAME__ Panel", transform, new Vector2(440f, 120f), distance);
            m_Hint = M3.Text("Hint", m_Panel, "__TAGLINE__", M3Theme.Type.BodyLarge, Vector2.zero, new Vector2(392f, 72f));
            WorldUi.PlaceInFront(m_Panel, m_Core.Head, distance, -15f);
        }
    }
}
