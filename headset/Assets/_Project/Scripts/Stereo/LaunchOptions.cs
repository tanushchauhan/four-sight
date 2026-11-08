using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Development overrides passed when launching the app, so settings can be compared without a
    // rebuild. The launch scripts take them the same way on both platforms:
    //   zsh Tools/build_and_run.sh --ei dof 6 --ei passthrough 1       (Android)
    //   zsh Tools/ios_launch.sh --ei dof 6 --ei passthrough 1          (iOS)
    // On Android they arrive as intent extras from `adb shell am start --ei`. iOS has no intents, so
    // there they arrive as environment variables named with k_EnvironmentPrefix, which devicectl sets
    // on launch; the same goes for the Editor and anything else. Only a launch from the Mac carries
    // them: opened from the home screen, the app takes its defaults.
    public static class LaunchOptions
    {
        public const string k_EnvironmentPrefix = "CARDBOARDXR_";

        public static int GetInt(string name, int fallback)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<int>("getIntExtra", name, fallback);
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Stereo] Couldn't read launch option {name}: {e.Message}");
            }
            return fallback;
#else
            return TryParse(Environment.GetEnvironmentVariable(k_EnvironmentPrefix + name), out var value) ? value : fallback;
#endif
        }

        // A text option, such as the robot stream URL. Android receives it as a string extra
        // (`--es stream http://...`); iOS and the Editor use CARDBOARDXR_<name>.
        public static string GetString(string name, string fallback)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                if (!intent.Call<bool>("hasExtra", name))
                    return fallback;
                var text = intent.Call<string>("getStringExtra", name);
                return string.IsNullOrEmpty(text) ? fallback : text;
            }
            catch (AndroidJavaException e)
            {
                Debug.LogWarning($"[Stereo] Couldn't read launch option {name}: {e.Message}");
            }
            return fallback;
#else
            var text = Environment.GetEnvironmentVariable(k_EnvironmentPrefix + name);
            return string.IsNullOrEmpty(text) ? fallback : text;
#endif
        }

        public static bool TryParse(string text, out int value)
        {
            value = 0;
            return text != null && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

#if !UNITY_ANDROID || UNITY_EDITOR
        // Lists the options the environment carries, so a misspelled one shows rather than quietly
        // leaving a default in place.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void LogOptions()
        {
            var found = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = (string)entry.Key;
                if (!key.StartsWith(k_EnvironmentPrefix, StringComparison.Ordinal))
                    continue;
                var text = (string)entry.Value;
                var option = key.Substring(k_EnvironmentPrefix.Length);
                found.Add(TryParse(text, out _) ? $"{option} {text}" : $"{option}={text}");
            }
            if (found.Count > 0)
                Debug.Log($"[Stereo] Launch options: {string.Join(", ", found)}");
        }
#endif
    }
}
