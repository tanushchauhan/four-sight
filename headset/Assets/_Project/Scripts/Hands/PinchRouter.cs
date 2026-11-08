using CardboardXR.Interaction;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Hands
{
    // Where the hand's pinches come from: hand tracking in 6DoF, which knows where the hand is, or the
    // 3DoF pinch sensor, which only sees that a pinch happened.
    public enum PinchSource
    {
        Hand,
        Camera,
    }

    // What a pinch did (PinchRouter.Order).
    public enum PinchRoute
    {
        Settings,
        Button,
        Demo,
        Grab,
        Click,
        Ignored,
    }

    // Takes pinches that would otherwise click, such as painting in the air (PinchRouter.Handler).
    public interface IPinchHandler
    {
        // A pinch began with the gaze on gazeTarget (or nothing): true to take it, false to let it go on.
        // A Camera pinch has no hand position, so a handler that needs one declines it.
        bool TryBeginPinch(GameObject gazeTarget, PinchSource source);

        // The pinch it took ended: the fingers opened or the hand was lost.
        void EndPinch();
    }

    // Every pinch in every scene, from either source, goes through here in one order:
    //   1. the palm's settings gear, while it shows, opens the settings panel;
    //   2. a button under the gaze is clicked, so panels work in every demo whatever it does with
    //      pinches (Orb Rush takes all of them during a round, and was swallowing the panel's);
    //   3. the scene's demo (Handler) is asked, unless the settings panel is open;
    //   4. something the hand's ray is on is picked up (6DoF);
    //   5. otherwise the gaze clicks what it's on, except a placed object in 6DoF, which only the
    //      hand takes, so looking at a cube and pinching elsewhere doesn't remove it by surprise.
    // A pinch that would click (or open settings) within k_ClickLockoutSeconds of the last click does
    // nothing: both sources sometimes see one pinch as two, opening and closing again 0.1-0.3 s apart,
    // which clicked a button two or three times (cycling the video format through three settings).
    // Demo pinches and grabs aren't locked out. Only one source runs at a time (PinchInput hands the
    // camera over between modes). Logs [Pinch].
    public static class PinchRouter
    {
        public const float k_ClickLockoutSeconds = 0.35f;

        static IPinchHandler s_Claimed;
        static float s_LastClick = float.NegativeInfinity;

        // The current scene's taker of pinches, or null. Demos set it on entering and DemoManager
        // clears it on leaving.
        public static IPinchHandler Handler { get; set; }

        // The order, pure, for testing. demoTakes is asked only once it's the demo's turn. lockedOut:
        // a click was too recent, so what would click does nothing.
        public static PinchRoute Order(bool settingsButton, bool settingsOpen, bool onButton, System.Func<bool> demoTakes,
            bool canGrab, bool onHandOnlyObject, bool lockedOut = false)
        {
            if (settingsButton && !lockedOut)
                return PinchRoute.Settings;
            if (onButton)
                return lockedOut ? PinchRoute.Ignored : PinchRoute.Button;
            if (!settingsOpen && demoTakes != null && demoTakes())
                return PinchRoute.Demo;
            if (canGrab)
                return PinchRoute.Grab;
            if (onHandOnlyObject || lockedOut)
                return PinchRoute.Ignored;
            return PinchRoute.Click;
        }

        // A pinch closed. canGrab: whether the hand's interactor is on something it can pick up, which
        // the caller then does when this returns Grab.
        public static PinchRoute Begin(PinchSource source, GazePointer gaze, bool canGrab)
        {
            End();
            var target = gaze.Target;
            var handler = Handler;
            var now = Time.unscaledTime;
            var lockedOut = now - s_LastClick < k_ClickLockoutSeconds;
            var route = Order(!lockedOut && SettingsSummoner.TryConsumePinch(), SettingsSummoner.IsPanelOpen, IsButton(target),
                handler != null ? () => handler.TryBeginPinch(target, source) : null,
                canGrab, source == PinchSource.Hand && target != null && target.GetComponentInParent<PlacedObject>() != null, lockedOut);
            if (route is PinchRoute.Settings or PinchRoute.Button or PinchRoute.Click)
                s_LastClick = now;
            var name = target != null ? target.name : "nothing";
            switch (route)
            {
                case PinchRoute.Button:
                case PinchRoute.Click:
                    Debug.Log($"[Pinch] {source} pinch: clicking {name}");
                    gaze.TriggerDown();
                    gaze.TriggerUp();
                    break;
                case PinchRoute.Demo:
                    s_Claimed = handler;
                    break;
                case PinchRoute.Ignored when lockedOut:
                    Debug.Log($"[Pinch] {source} pinch {(now - s_LastClick) * 1000f:F0} ms after the last click: taken as the same pinch, ignored");
                    break;
                case PinchRoute.Ignored:
                    Debug.Log($"[Pinch] {source} pinch with the gaze on {name} but the hand's ray elsewhere; " +
                              "placed objects are taken by hand, so this did nothing");
                    break;
            }
            return route;
        }

        // The pinch opened, or its hand was lost: ends a demo's claim on it.
        public static void End()
        {
            var claimed = s_Claimed;
            s_Claimed = null;
            claimed?.EndPinch();
        }

        // Clears the scene's handler, ending any pinch it holds: on leaving a scene.
        public static void Clear()
        {
            End();
            Handler = null;
        }

        // A button or other control, which a pinch always clicks.
        public static bool IsButton(GameObject target) =>
            target != null && target.GetComponentInParent<Selectable>() != null;
    }
}
