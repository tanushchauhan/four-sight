using System;
using System.IO;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Copies the app's tagged log lines ([Pose], [AR], [Tracking] and so on) and any errors to
    // Application.persistentDataPath/session.log, replaced at each launch with the previous one kept
    // as session-previous.log. Logcat on a Pixel only holds a minute or two, and misses everything
    // while the phone is unplugged for a walk-around test. The path is logged at startup. Each demo's
    // tag is added by DemoManager from the catalog (KeepTag), so a new demo needs no change here.
    public static class SessionLog
    {
        // Core's tags, then each demo's (KeepTag). Replaced whole, never changed in place, since lines
        // arrive on any thread.
        static volatile string[] s_Tags = { "[Pose]", "[AR]", "[Room]", "[Anchor]", "[Hands]", "[Pinch]", "[Perf]", "[Passthrough]", "[Tracking]", "[Gaze]", "[Hitch]", "[Frames]", "[Stereo]", "[Viewer]", "[Demos]", "[Settings]", "[Light]", "[Thermal]" };
        // Unity's own warnings when job scratch memory outlives four frames, which the hand models on
        // the CPU cause whenever a run takes longer than that (on a Pixel 9 Pro at 60 Hz, most runs):
        // the memory is freed late, not lost. Written every time, they buried the log, three lines per
        // run, dozens a second, so the first is kept and the rest are counted (see ReportNoise).
        static readonly string[] k_NoisePrefixes =
        {
            "Internal: JobTempAlloc has allocations that are more than the maximum lifespan",
            "To Debug, run app with -diag-job-temp-memory-leak-validation",
            "Internal: deleting an allocation that is older than its permitted lifetime",
        };
        const double k_NoiseReportSeconds = 5.0;
        static readonly object s_Lock = new();
        static StreamWriter s_Writer;
        static int s_Noise;
        static bool s_NoiseSeen;
        static DateTime s_NoiseSince;

        // Before the scene loads, so the components' startup lines are included.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Begin()
        {
            try
            {
                var path = Path.Combine(Application.persistentDataPath, "session.log");
                if (File.Exists(path))
                    File.Copy(path, Path.Combine(Application.persistentDataPath, "session-previous.log"), overwrite: true);
                s_Writer = new StreamWriter(path, append: false) { AutoFlush = true };
                Application.logMessageReceivedThreaded += OnLog;
                Debug.Log($"[Stereo] Session log: {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Stereo] No session log: {e.Message}");
            }
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            // Warnings are kept too: XR Plug-in Management and the AR providers report a loader that
            // won't start as a warning, not an error, so dropping them hid why ARKit never began.
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Warning && !IsTagged(message))
                return;
            if (type == LogType.Warning && IsNoise(message))
            {
                lock (s_Lock)
                {
                    if (!s_NoiseSeen)
                    {
                        s_NoiseSeen = true;
                        s_NoiseSince = DateTime.Now;
                        s_Writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {type} {message} " +
                                            "(more like it are counted, not written: see [Stereo] Job memory lines)");
                    }
                    else
                    {
                        s_Noise++;
                    }
                }
                return;
            }
            ReportNoise();
            var line = $"{DateTime.Now:HH:mm:ss.fff} {type} {message}";
            // An exception's first frames say where it came from; the whole trace goes to logcat.
            if (type == LogType.Exception && !string.IsNullOrEmpty(stackTrace))
                line += " | " + FirstLines(stackTrace, 3);
            lock (s_Lock)
                s_Writer?.WriteLine(line);
        }

        // How many of Unity's late-freed job memory warnings there were since the last report, at
        // most every k_NoiseReportSeconds, written before the next line that's kept.
        static void ReportNoise()
        {
            lock (s_Lock)
            {
                var now = DateTime.Now;
                if (s_Noise == 0 || (now - s_NoiseSince).TotalSeconds < k_NoiseReportSeconds)
                    return;
                s_Writer?.WriteLine($"{now:HH:mm:ss.fff} Log [Stereo] Job memory freed late: {s_Noise} Unity warnings in the last " +
                                    $"{(now - s_NoiseSince).TotalSeconds:F0} s (the hand models taking longer than 4 frames; harmless)");
                s_Noise = 0;
                s_NoiseSince = now;
            }
        }

        static bool IsNoise(string message)
        {
            foreach (var prefix in k_NoisePrefixes)
            {
                if (message.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        static string FirstLines(string text, int count)
        {
            var end = -1;
            for (var i = 0; i < count; i++)
            {
                var next = text.IndexOf('\n', end + 1);
                if (next < 0)
                    return text.Replace('\n', ' ').Trim();
                end = next;
            }
            return text.Substring(0, end).Replace('\n', ' ').Trim();
        }

        // Keeps lines that start with tag, such as "[Portal]", from now on.
        public static void KeepTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return;
            lock (s_Lock)
            {
                var tags = s_Tags;
                if (Array.IndexOf(tags, tag) >= 0)
                    return;
                var more = new string[tags.Length + 1];
                tags.CopyTo(more, 0);
                more[tags.Length] = tag;
                s_Tags = more;
            }
        }

        // Whether a line that isn't a warning or error is kept.
        public static bool IsTagged(string message)
        {
            foreach (var tag in s_Tags)
            {
                if (message.StartsWith(tag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
