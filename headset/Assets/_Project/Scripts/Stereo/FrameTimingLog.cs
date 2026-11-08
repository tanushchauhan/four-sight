using System;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Logs the achieved frame rate every few seconds with the [Frames] tag, and each hitch with the
    // [Hitch] tag: when it happened and where the time went (main thread, render thread, GPU, or
    // waiting to present), plus whether a garbage collection ran. Needs Frame Timing Stats enabled in
    // the Player settings for the per-thread times.
    public class FrameTimingLog : MonoBehaviour
    {
        // FrameTimingManager reports a frame a few frames after it happens.
        const int k_TimingDelayFrames = 4;

        [SerializeField] float m_Interval = 5f;
        [Tooltip("Frames slower than this are logged individually.")]
        [SerializeField] float m_HitchMilliseconds = 30f;

        readonly FrameTiming[] m_Timings = new FrameTiming[k_TimingDelayFrames];
        int m_Frames;
        int m_LateFrames;
        float m_Elapsed;
        float m_WorstFrame;
        int m_LastGcCount;

        // The hitch waiting for its timings to become available.
        int m_PendingFrames;
        string m_PendingHitch;

        void OnEnable()
        {
            // Stack traces make every log line slow and noisy on device.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            m_LastGcCount = GC.CollectionCount(0);
        }

        void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
            var deltaTime = Time.unscaledDeltaTime;
            var gcCount = GC.CollectionCount(0);
            var collections = gcCount - m_LastGcCount;
            m_LastGcCount = gcCount;

            if (deltaTime * 1000f > m_HitchMilliseconds && m_PendingHitch == null)
            {
                m_PendingHitch = $"{deltaTime * 1000f:F1} ms at {DateTime.Now:HH:mm:ss.fff}, frame {Time.frameCount}, " +
                                 $"GCs {collections}, heap {GC.GetTotalMemory(false) / 1e6:F1} MB";
                m_PendingFrames = k_TimingDelayFrames;
            }
            else if (m_PendingHitch != null && --m_PendingFrames <= 0)
            {
                LogPendingHitch();
            }

            m_Frames++;
            m_Elapsed += deltaTime;
            m_WorstFrame = Mathf.Max(m_WorstFrame, deltaTime);
            // A frame that took half again its budget missed at least one refresh.
            var budget = 1f / (Application.targetFrameRate > 0 ? Application.targetFrameRate : 60);
            if (deltaTime > budget * 1.5f)
                m_LateFrames++;

            if (m_Elapsed < m_Interval)
                return;

            Debug.Log($"[Frames] {m_Frames / m_Elapsed:F1} fps (target {Application.targetFrameRate}), " +
                      $"worst {m_WorstFrame * 1000f:F1} ms, {100f * m_LateFrames / m_Frames:F1}% late, " +
                      $"display {Screen.currentResolution.refreshRateRatio.value:F0} Hz; {TypicalFrame()}{DeviceText()}");
            m_Frames = m_LateFrames = 0;
            m_Elapsed = m_WorstFrame = 0f;
        }

        // The thermal state, since Unity's iOS player caps the frame rate itself when it's serious
        // or critical (see ProjectSetup), and the screen's maximum rate.
        static string DeviceText() => DeviceState.Supported
            ? $"; thermal state {DeviceState.ThermalState}, screen maximum {DeviceState.ScreenMaximumFramesPerSecond} fps"
            : "";

        // Where a normal frame's time goes, averaged over the last few. Without this only hitches were
        // ever broken down, which says nothing about why a steady frame rate sits where it does: a
        // budget spent on work and one spent waiting to present look identical from the frame rate.
        string TypicalFrame()
        {
            var count = (int)FrameTimingManager.GetLatestTimings((uint)m_Timings.Length, m_Timings);
            if (count <= 0)
                return "no frame timings (enable Frame Timing Stats)";

            double cpu = 0, main = 0, render = 0, present = 0, gpu = 0;
            for (var i = 0; i < count; i++)
            {
                cpu += m_Timings[i].cpuFrameTime;
                main += m_Timings[i].cpuMainThreadFrameTime;
                render += m_Timings[i].cpuRenderThreadFrameTime;
                present += m_Timings[i].cpuMainThreadPresentWaitTime;
                gpu += m_Timings[i].gpuFrameTime;
            }

            return $"typical cpu {cpu / count:F1} ms: main {main / count:F1}, render {render / count:F1}, " +
                   $"present wait {present / count:F1}, gpu {gpu / count:F1}";
        }

        // Reports the slowest recent frame's breakdown alongside the hitch.
        void LogPendingHitch()
        {
            var count = (int)FrameTimingManager.GetLatestTimings((uint)m_Timings.Length, m_Timings);
            var slowest = -1;
            for (var i = 0; i < count; i++)
            {
                if (slowest < 0 || m_Timings[i].cpuFrameTime > m_Timings[slowest].cpuFrameTime)
                    slowest = i;
            }

            var breakdown = slowest < 0
                ? "no frame timings (enable Frame Timing Stats)"
                : $"cpu {m_Timings[slowest].cpuFrameTime:F1} ms: main {m_Timings[slowest].cpuMainThreadFrameTime:F1}, " +
                  $"render {m_Timings[slowest].cpuRenderThreadFrameTime:F1}, " +
                  $"present wait {m_Timings[slowest].cpuMainThreadPresentWaitTime:F1}, " +
                  $"gpu {m_Timings[slowest].gpuFrameTime:F1}";
            Debug.Log($"[Hitch] {m_PendingHitch}; {breakdown}");
            m_PendingHitch = null;
        }
    }
}
