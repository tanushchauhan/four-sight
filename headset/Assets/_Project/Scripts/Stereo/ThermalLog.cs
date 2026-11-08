using System.Globalization;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Times the phone's heating: each change of iOS's thermal state, checked every second, logged with
    // the time since launch and how long the last state lasted, so "how long until fair" is read
    // straight off the log rather than from the 5 s [Frames] lines. A run only measures heating from
    // cold if it started nominal and off the charger (charging heats the battery), so the state and
    // battery at launch are logged first, and each change says whether that still holds. Only in iOS
    // players (DeviceState); Android's thermal status is in [Perf]. Logs [Thermal].
    //   iOS says nominal well before a phone has cooled, and apps can't read its temperature, so a run
    //   launched within k_CoolMinutes of the last one ending (or of being seen charging) doesn't count
    //   either: a run 3 min after a build on the charger reached fair in 1:22. The last time the app ran
    //   is saved (k_LastRunKey) every k_SaveSeconds and on pausing, since a killed app gets no warning.
    public class ThermalLog : MonoBehaviour
    {
        const float k_CheckSeconds = 1f;
        const float k_CoolMinutes = 15f;
        const float k_SaveSeconds = 15f;
        // Unix seconds, when the app last ran, and when it was last seen charging.
        const string k_LastRunKey = "ThermalLastRun";
        const string k_LastChargingKey = "ThermalLastCharging";

        string m_State;
        float m_Since;
        float m_NextCheck;
        float m_NextSave;
        bool m_FromCold;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (!DeviceState.Supported)
                return;
            var holder = new GameObject("Thermal Log");
            DontDestroyOnLoad(holder);
            holder.AddComponent<ThermalLog>();
        }

        void Start()
        {
            m_State = DeviceState.ThermalState;
            m_Since = Time.realtimeSinceStartup;
            var why = m_State != "nominal" ? "not nominal" : Charging ? "charging" : WarmFrom();
            m_FromCold = why == null;
            Debug.Log($"[Thermal] {m_State} at launch; {Battery()}" +
                      (m_FromCold ? "; timing the heating from cold" : $"; not a run from cold ({why}), so its times aren't comparable"));
            Save();
        }

        static double Now => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Why the phone may still be warm from before this run, or null.
        static string WarmFrom()
        {
            string Ago(string key, string what)
            {
                if (!double.TryParse(PlayerPrefs.GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var then))
                    return null;
                var minutes = (Now - then) / 60.0;
                return minutes >= 0 && minutes < k_CoolMinutes ? $"{what} {minutes:F0} min ago" : null;
            }
            return Ago(k_LastRunKey, "last ran") ?? Ago(k_LastChargingKey, "charging");
        }

        // When it last ran, and was last charging; as strings, since PlayerPrefs floats lose the seconds.
        static void Save()
        {
            var now = Now.ToString("F0", CultureInfo.InvariantCulture);
            PlayerPrefs.SetString(k_LastRunKey, now);
            if (Charging)
                PlayerPrefs.SetString(k_LastChargingKey, now);
            PlayerPrefs.Save();
        }

        static bool Charging => SystemInfo.batteryStatus is BatteryStatus.Charging or BatteryStatus.Full;

        static string Battery() =>
            $"battery {SystemInfo.batteryLevel * 100f:F0}%, {SystemInfo.batteryStatus.ToString().ToLowerInvariant()}";

        void Update()
        {
            var now = Time.realtimeSinceStartup;
            if (now >= m_NextSave)
            {
                m_NextSave = now + k_SaveSeconds;
                Save();
            }
            if (now < m_NextCheck)
                return;
            m_NextCheck = now + k_CheckSeconds;
            var state = DeviceState.ThermalState;
            if (state == m_State)
                return;
            if (Charging)
                m_FromCold = false;
            Debug.Log($"[Thermal] {m_State} -> {state} at {Clock(now)} after launch, {Clock(now - m_Since)} in {m_State}; " +
                      $"{Battery()}{(m_FromCold ? "" : "; not from cold")}");
            m_State = state;
            m_Since = now;
        }

        void OnApplicationQuit() => Save();

        // Paused (in the background) the phone cools, so the clock no longer measures heating.
        void OnApplicationPause(bool paused)
        {
            if (paused)
                Save();
            if (paused && m_FromCold)
            {
                m_FromCold = false;
                Debug.Log($"[Thermal] Paused at {Clock(Time.realtimeSinceStartup)}, {m_State}: later times aren't from cold");
            }
        }

        static string Clock(float seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
    }
}
