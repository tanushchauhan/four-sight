using System.Diagnostics;

namespace CardboardXR.Stereo
{
    // Puts camera-image timestamps on a reference clock, and says how old an image is on its own.
    // HeadPoseFusion uses it to match an AR pose with the gyroscope rotation at the moment its image
    // was captured; HandTracker, to know how far to predict the hand past its image.
    //
    // On Android the two already share a clock: ARCore and the native sensors both stamp with the
    // boot-time clock. On iOS they don't: ARKit stamps frames with the system's monotonic clock,
    // while the tracker's Input System samples carry the Input System's own time, and matching
    // one against the other found nothing, so every AR pose was thrown away.
    //
    // Rather than hard-code which clock each platform uses, this looks at how old a frame appears
    // when it arrives. Measured on the clock it was really stamped with, a frame is tens of
    // milliseconds old; on any other clock it is off by minutes or hours. So the first plausible
    // reading settles the question, and the choice is logged.
    public class CameraClock
    {
        public enum Base
        {
            Unknown,
            // The camera stamps on the tracker's clock: nothing to convert.
            Tracker,
            // The camera stamps on the system's monotonic clock: convert by the offset between it and
            // the tracker's clock, read at the same instant.
            System,
        }

        // A frame this old or younger when it arrives, measured on the clock it was stamped with. A
        // little negative allowed, for rounding between clocks that share a base.
        const long k_MinAgeNs = -50_000_000;
        const long k_MaxAgeNs = 500_000_000;
        // Distinct frames read before giving up and saying so. Early frames after a session starts
        // can be stale. Frames, not calls: HandTracker asks about the same image every rendered frame.
        const int k_AttemptsBeforeWarning = 30;

        readonly string m_LogTag;
        readonly string m_ReferenceName;
        int m_Attempts;
        long m_LastAttemptNs = long.MinValue;
        bool m_Warned;

        // logTag and referenceName only word the log lines: the tag of the component that owns this,
        // and what its reference clock is, as "the head tracker's clock".
        public CameraClock(string logTag = "[Pose]", string referenceName = "the head tracker's clock")
        {
            m_LogTag = logTag;
            m_ReferenceName = referenceName;
        }

        public Base Source { get; private set; } = Base.Unknown;

        // How old the frame passed to the latest successful call was at that moment, on the clock it
        // was stamped with: its age on arrival, if called as it arrives.
        public long ArrivalAgeNs { get; private set; }

        public void Reset()
        {
            Source = Base.Unknown;
            m_Attempts = 0;
            m_LastAttemptNs = long.MinValue;
            m_Warned = false;
            ArrivalAgeNs = 0;
        }

        // The system's monotonic clock in nanoseconds. On Apple platforms this is the clock ARKit
        // stamps its frames with.
        public static long SystemNowNs()
        {
            var ticks = Stopwatch.GetTimestamp();
            var frequency = Stopwatch.Frequency;
            // Split so that neither part overflows or loses nanoseconds to floating point.
            return ticks / frequency * 1_000_000_000L + ticks % frequency * 1_000_000_000L / frequency;
        }

        // Converts a camera timestamp to the tracker's clock. trackerNowNs and systemNowNs must be
        // read together, just before calling. False while the camera's clock is still unknown.
        public bool TryToTrackerClock(long cameraNs, long trackerNowNs, long systemNowNs, out long trackerNs)
        {
            if (Source == Base.Unknown)
                Identify(cameraNs, trackerNowNs, systemNowNs);

            switch (Source)
            {
                case Base.Tracker:
                    trackerNs = cameraNs;
                    ArrivalAgeNs = trackerNowNs - cameraNs;
                    return true;
                case Base.System:
                    trackerNs = cameraNs + (trackerNowNs - systemNowNs);
                    ArrivalAgeNs = systemNowNs - cameraNs;
                    return true;
                default:
                    trackerNs = 0;
                    return false;
            }
        }

        void Identify(long cameraNs, long trackerNowNs, long systemNowNs)
        {
            var trackerAge = trackerNowNs - cameraNs;
            var systemAge = systemNowNs - cameraNs;
            if (Plausible(trackerAge))
            {
                Source = Base.Tracker;
                UnityEngine.Debug.Log($"{m_LogTag} Camera images are stamped on {m_ReferenceName} " +
                                      $"({trackerAge * 1e-6:F0} ms old)");
                return;
            }

            if (Plausible(systemAge))
            {
                Source = Base.System;
                UnityEngine.Debug.Log($"{m_LogTag} Camera images are stamped on the system clock, " +
                                      $"{(trackerNowNs - systemNowNs) * 1e-9:F3} s from {m_ReferenceName}; " +
                                      $"converting ({systemAge * 1e-6:F0} ms old)");
                return;
            }

            if (cameraNs != m_LastAttemptNs)
            {
                m_LastAttemptNs = cameraNs;
                m_Attempts++;
            }
            if (m_Attempts < k_AttemptsBeforeWarning || m_Warned)
                return;
            m_Warned = true;
            UnityEngine.Debug.LogWarning($"{m_LogTag} Camera images match neither {m_ReferenceName} " +
                                         $"({trackerAge * 1e-9:F3} s old) nor the system clock " +
                                         $"({systemAge * 1e-9:F3} s old), so their timing can't be used");
        }

        static bool Plausible(long ageNs) => ageNs >= k_MinAgeNs && ageNs <= k_MaxAgeNs;
    }
}
