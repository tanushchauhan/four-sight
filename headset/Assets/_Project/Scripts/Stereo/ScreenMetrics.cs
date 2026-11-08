using UnityEngine;

namespace CardboardXR.Stereo
{
    // Physical screen size, which the lens distortion needs in meters.
    public static class ScreenMetrics
    {
        const float k_MetersPerInch = 0.0254f;
        const float k_FallbackDpi = 160f;

        // Width and height of the landscape screen in meters. `source` describes where the numbers
        // came from, for logging.
        public static Vector2 GetScreenSizeMeters(out string source)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (TryGetAndroidMetrics(out var widthPixels, out var heightPixels, out var xdpi, out var ydpi))
            {
                source = $"Android {widthPixels}x{heightPixels} px at {xdpi:F1} x {ydpi:F1} dpi";
                return new Vector2(widthPixels / xdpi, heightPixels / ydpi) * k_MetersPerInch;
            }
#endif
            var dpi = Screen.dpi > 0f ? Screen.dpi : k_FallbackDpi;
            source = $"Screen {Screen.width}x{Screen.height} px at {dpi:F1} dpi";
            return new Vector2(Screen.width, Screen.height) / dpi * k_MetersPerInch;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Real pixel counts and per-axis densities, like the Cardboard SDK's screen_params.cc.
        static bool TryGetAndroidMetrics(out int width, out int height, out float xdpi, out float ydpi)
        {
            width = height = 0;
            xdpi = ydpi = 0f;
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var windowManager = activity.Call<AndroidJavaObject>("getWindowManager");
                using var display = windowManager.Call<AndroidJavaObject>("getDefaultDisplay");
                using var metrics = new AndroidJavaObject("android.util.DisplayMetrics");
                display.Call("getRealMetrics", metrics);
                width = metrics.Get<int>("widthPixels");
                height = metrics.Get<int>("heightPixels");
                xdpi = metrics.Get<float>("xdpi");
                ydpi = metrics.Get<float>("ydpi");
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Stereo] Couldn't read display metrics: {e.Message}");
                return false;
            }

            // The viewer holds the phone in landscape, so the long side is the width.
            if (height > width)
            {
                (width, height) = (height, width);
                (xdpi, ydpi) = (ydpi, xdpi);
            }
            return width > 0 && height > 0 && xdpi > 0f && ydpi > 0f;
        }
#endif
    }
}
