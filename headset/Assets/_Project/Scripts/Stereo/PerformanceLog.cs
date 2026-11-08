using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Logs where the CPU time goes and how hot the phone is getting, every m_Interval seconds with
    // the [Perf] tag, to find what makes it throttle. CPU time comes from Linux's per-thread counters
    // for this process (/proc/self/task), grouped by thread name and given in cores (1.0 is one core
    // kept busy): Unity's main and render threads, the job workers running the hand models, ARCore's
    // threads and so on. Heat comes from Android's thermal status and headroom (1.0 means throttling,
    // by its forecast 10 s ahead) and the battery temperature; power from the battery's current and
    // voltage. Android only; any part that fails is left out.
    public class PerformanceLog : MonoBehaviour
    {
        // Linux counts thread CPU time in ticks of 1/100 s.
        const double k_TicksPerSecond = 100.0;
        // Android's BatteryManager.BATTERY_PROPERTY_CURRENT_NOW, in microamps.
        const int k_CurrentNow = 2;

        [SerializeField] float m_Interval = 5f;
        [Tooltip("How many of the busiest thread groups to list.")]
        [SerializeField] int m_TopGroups = 8;

        readonly Dictionary<int, (string group, long ticks)> m_Threads = new();
        readonly Dictionary<string, long> m_GroupTicks = new();
        double m_LastTime;
        float m_NextLog;
        bool m_Android;
        AndroidJavaObject m_Activity;
        AndroidJavaObject m_PowerManager;
        AndroidJavaObject m_BatteryManager;

        void Start()
        {
            m_Android = Application.platform == RuntimePlatform.Android;
            if (!m_Android)
                return;
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                m_Activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                m_PowerManager = m_Activity.Call<AndroidJavaObject>("getSystemService", "power");
                m_BatteryManager = m_Activity.Call<AndroidJavaObject>("getSystemService", "batterymanager");
            }
            catch (Exception e)
            {
                Debug.Log($"[Perf] No thermal or battery readings: {e.Message}");
            }
            SampleThreads(out _);
            m_LastTime = Time.realtimeSinceStartupAsDouble;
            m_NextLog = Time.unscaledTime + m_Interval;
        }

        void OnDestroy()
        {
            m_PowerManager?.Dispose();
            m_BatteryManager?.Dispose();
            m_Activity?.Dispose();
        }

        void Update()
        {
            if (!m_Android || Time.unscaledTime < m_NextLog)
                return;
            m_NextLog = Time.unscaledTime + m_Interval;

            var now = Time.realtimeSinceStartupAsDouble;
            var seconds = now - m_LastTime;
            m_LastTime = now;
            m_GroupTicks.Clear();
            if (!SampleThreads(out var totalTicks) || seconds <= 0)
                return;

            var cores = new StringBuilder();
            foreach (var (group, ticks) in m_GroupTicks.OrderByDescending(pair => pair.Value).Take(m_TopGroups))
                cores.Append($", {group} {ticks / k_TicksPerSecond / seconds:F2}");
            Debug.Log($"[Perf] CPU {totalTicks / k_TicksPerSecond / seconds:F2} cores{cores}; {Heat()}; {Battery()}");
        }

        // Adds each thread's CPU ticks since the last sample to its group's; false if /proc can't be
        // read.
        bool SampleThreads(out long totalTicks)
        {
            totalTicks = 0;
            string[] tasks;
            try
            {
                tasks = Directory.GetDirectories("/proc/self/task");
            }
            catch (Exception)
            {
                return false;
            }

            foreach (var task in tasks)
            {
                if (!int.TryParse(Path.GetFileName(task), out var id) || !TryReadThread(task, out var name, out var ticks))
                    continue;
                // Numbered threads of a kind, like "Job.Worker 3", are one group.
                var group = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '-', '_', '#');
                if (group.Length == 0)
                    group = name;
                var since = m_Threads.TryGetValue(id, out var last) ? ticks - last.ticks : 0;
                m_Threads[id] = (group, ticks);
                if (since <= 0)
                    continue;
                totalTicks += since;
                m_GroupTicks[group] = m_GroupTicks.TryGetValue(group, out var sum) ? sum + since : since;
            }
            return true;
        }

        // A thread's name and its user plus system CPU ticks, from /proc/self/task/<id>/stat:
        // "<id> (<name>) <state> ..." with user and system time the 12th and 13th fields after the
        // name. The name can hold spaces and parentheses, so it ends at the last ')'.
        static bool TryReadThread(string task, out string name, out long ticks)
        {
            name = null;
            ticks = 0;
            string stat;
            try
            {
                stat = File.ReadAllText(Path.Combine(task, "stat"));
            }
            catch (Exception)
            {
                return false;
            }
            var open = stat.IndexOf('(');
            var close = stat.LastIndexOf(')');
            if (open < 0 || close <= open)
                return false;
            name = stat.Substring(open + 1, close - open - 1);
            var fields = stat.Substring(close + 2).Split(' ');
            return fields.Length > 12 && long.TryParse(fields[11], out var user) && long.TryParse(fields[12], out var system) &&
                   (ticks = user + system) >= 0;
        }

        string Heat()
        {
            if (m_PowerManager == null)
                return "no thermal readings";
            try
            {
                var status = m_PowerManager.Call<int>("getCurrentThermalStatus");
                var headroom = m_PowerManager.Call<float>("getThermalHeadroom", 10);
                return $"thermal status {status}, headroom {headroom:F2}";
            }
            catch (Exception e)
            {
                return $"no thermal readings ({e.Message})";
            }
        }

        string Battery()
        {
            if (m_Activity == null)
                return "no battery readings";
            try
            {
                using var filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED");
                using var intent = m_Activity.Call<AndroidJavaObject>("registerReceiver", null, filter);
                var celsius = intent.Call<int>("getIntExtra", "temperature", 0) / 10f;
                var volts = intent.Call<int>("getIntExtra", "voltage", 0) / 1000f;
                var level = intent.Call<int>("getIntExtra", "level", -1);
                var amps = m_BatteryManager != null ? Mathf.Abs(m_BatteryManager.Call<int>("getIntProperty", k_CurrentNow)) / 1e6f : 0f;
                return $"battery {celsius:F1} C, {amps * volts:F2} W ({amps * 1000f:F0} mA at {volts:F2} V), {level}%";
            }
            catch (Exception e)
            {
                return $"no battery readings ({e.Message})";
            }
        }
    }
}
