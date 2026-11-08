using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Asks Android to run the display at the fastest refresh rate up to a target. A frame-rate target
    // alone doesn't change the panel's refresh rate; the window has to prefer a faster display mode.
    // Between modes with the same refresh rate it prefers the faster vsync: on a Pixel 9 Pro the
    // default 120 Hz mode (120 Hz vsync) stalled frames for ~75 ms whenever the display driver
    // switched its TE2 timing, while the 120 Hz mode on a 240 Hz vsync didn't.
    public static class DisplayRefreshRate
    {
        public static void Request(float maxHz)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                // Window attributes can only change on the UI thread.
                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    try
                    {
                        PreferFastestMode(activity, maxHz);
                    }
                    finally
                    {
                        activity.Dispose();
                    }
                }));
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Stereo] Couldn't request a display refresh rate: {e.Message}");
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static void PreferFastestMode(AndroidJavaObject activity, float maxHz)
        {
            try
            {
                using var window = activity.Call<AndroidJavaObject>("getWindow");
                using var windowManager = activity.Call<AndroidJavaObject>("getWindowManager");
                using var display = windowManager.Call<AndroidJavaObject>("getDefaultDisplay");
                using var current = display.Call<AndroidJavaObject>("getMode");
                var width = current.Call<int>("getPhysicalWidth");
                var height = current.Call<int>("getPhysicalHeight");

                var bestId = current.Call<int>("getModeId");
                var bestRate = current.Call<float>("getRefreshRate");
                var bestVsync = VsyncRate(current, bestRate, out var vsyncSource);
                var available = new StringBuilder();
                foreach (var mode in display.Call<AndroidJavaObject[]>("getSupportedModes"))
                {
                    using (mode)
                    {
                        // Keep the current resolution; only the refresh rate should change.
                        if (mode.Call<int>("getPhysicalWidth") != width || mode.Call<int>("getPhysicalHeight") != height)
                            continue;

                        var id = mode.Call<int>("getModeId");
                        var rate = mode.Call<float>("getRefreshRate");
                        var vsync = VsyncRate(mode, rate, out _);
                        available.Append($" {id}:{rate:F0}/{vsync:F0}");
                        var faster = rate > bestRate + 0.5f ||
                                     (Mathf.Abs(rate - bestRate) <= 0.5f && vsync > bestVsync + 0.5f);
                        if (faster && rate <= maxHz + 0.5f)
                        {
                            bestId = id;
                            bestRate = rate;
                            bestVsync = vsync;
                        }
                    }
                }

                using var attributes = window.Call<AndroidJavaObject>("getAttributes");
                attributes.Set("preferredDisplayModeId", bestId);
                window.Call("setAttributes", attributes);
                Debug.Log($"[Stereo] Preferred display mode {bestId}: {width}x{height} at {bestRate:F0} Hz, " +
                          $"vsync {bestVsync:F0} Hz (modes id:refresh/vsync{available}; vsync from {vsyncSource})");
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Stereo] Couldn't set the display mode: {e.Message}");
            }
        }

        // Display.Mode.getVsyncRate() is public from API 35. When it can't be called, the vsync is read
        // from Mode.toString(), which lists it too, and last of all assumed to equal the refresh rate.
        static float VsyncRate(AndroidJavaObject mode, float refreshRate, out string source)
        {
            try
            {
                source = "getVsyncRate";
                return mode.Call<float>("getVsyncRate");
            }
            catch (AndroidJavaException e)
            {
                source = $"Mode.toString (getVsyncRate failed: {e.Message.Split('\n')[0]})";
            }

            var match = Regex.Match(mode.Call<string>("toString"), @"vsync=([0-9.]+)");
            if (match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vsync))
                return vsync;

            source = "the refresh rate (no vsync available)";
            return refreshRate;
        }
#endif
    }
}
