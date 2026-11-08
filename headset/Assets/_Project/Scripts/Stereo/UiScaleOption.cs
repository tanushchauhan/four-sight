using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // The "uiScale" launch option: how much bigger than Android XR's sizes the Material UI is drawn, in
    // percent (default M3Theme.k_DefaultCardboardScale), for tuning legibility through a viewer
    // without a rebuild. Set before the first scene loads, so every M3Canvas wakes with it.
    static class UiScaleOption
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
            var percent = LaunchOptions.GetInt("uiScale", 0);
            if (percent <= 0)
                return;
            M3Theme.CardboardScale = percent / 100f;
            Debug.Log($"[Stereo] UI scale {M3Theme.CardboardScale:F2} times Android XR's sizes");
        }
    }
}
