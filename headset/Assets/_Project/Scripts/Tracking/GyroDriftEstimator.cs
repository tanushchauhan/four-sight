using System;
using System.Collections.Generic;

namespace CardboardXR.Tracking
{
    // Learns a gyroscope bias from how a tracker's heading drifts against a reference whose heading
    // doesn't, such as ARCore. Over each window it fits the trend of the heading difference; that
    // drift rate is the bias along the vertical, measured in the gyroscope's frame. A share of it is
    // added to Bias, which the tracker subtracts from its samples, and the next window measures what
    // is left. Only the vertical part shows up as heading drift; the accelerometer already corrects
    // tilt. The bias moves with temperature, so it keeps learning.
    public sealed class GyroDriftEstimator
    {
        // Magnitude limit, in rad/s; phone gyroscopes stay far below it.
        const double k_MaxBias = 0.05;
        // The most one window's drift can move the bias, in rad/s (~0.34 deg/s). Real drift on a
        // Pixel 9 Pro was ~0.4 deg/s in all; a fit right after ARCore relocalized read 1.07 deg/s
        // while its heading settled.
        const double k_MaxDriftPerWindow = 0.006;

        readonly double m_WindowSeconds;
        readonly double m_Gain;
        readonly List<(double seconds, double heading)> m_Window = new();
        long m_WindowStartNs;
        Vector3d m_UpSum;
        bool m_SkipWindow;

        // windowSeconds: how long each drift fit runs. gain: the share of each measured drift that is
        // corrected, which trades speed for noise.
        public GyroDriftEstimator(double windowSeconds = 5, double gain = 0.5)
        {
            m_WindowSeconds = windowSeconds;
            m_Gain = gain;
        }

        // To subtract from gyroscope samples, in rad/s in the gyroscope's frame.
        public Vector3d Bias { get; private set; }

        // The heading drift measured over the last complete window, in rad/s.
        public double LastDriftRate { get; private set; }

        // Windows that moved the bias.
        public int Updates { get; private set; }

        // Starts over, keeping the bias; for when the reference jumps, as when ARCore relocalizes.
        // The next window only measures, since the reference may still be settling.
        public void Restart()
        {
            ClearWindow();
            m_SkipWindow = true;
        }

        void ClearWindow()
        {
            m_Window.Clear();
            m_UpSum = Vector3d.Zero;
        }

        // headingDifference: the reference's heading minus the tracker's, in radians, positive when
        // the tracker turns the other way from positive gyroscope rotation about the vertical.
        // upInSensor: the vertical in the gyroscope's frame at timeNs.
        public void AddSample(long timeNs, double headingDifference, Vector3d upInSensor)
        {
            if (m_Window.Count == 0)
            {
                m_WindowStartNs = timeNs;
                m_Window.Add((0, headingDifference));
                m_UpSum = upInSensor;
                return;
            }

            // Unwrapped against the window's first sample.
            var first = m_Window[0].heading;
            var seconds = (timeNs - m_WindowStartNs) * 1e-9;
            m_Window.Add((seconds, first + Wrap(headingDifference - first)));
            m_UpSum += upInSensor;
            if (seconds < m_WindowSeconds)
                return;

            LastDriftRate = Slope();
            if (m_SkipWindow)
            {
                m_SkipWindow = false;
            }
            else
            {
                var drift = Math.Clamp(LastDriftRate, -k_MaxDriftPerWindow, k_MaxDriftPerWindow);
                var bias = Bias + m_UpSum.Normalized * (drift * m_Gain);
                Bias = bias.Length > k_MaxBias ? bias.Normalized * k_MaxBias : bias;
                Updates++;
            }
            ClearWindow();
        }

        // Least-squares slope of heading against time.
        double Slope()
        {
            double meanT = 0, meanH = 0;
            foreach (var (t, h) in m_Window)
            {
                meanT += t;
                meanH += h;
            }
            meanT /= m_Window.Count;
            meanH /= m_Window.Count;

            double covariance = 0, variance = 0;
            foreach (var (t, h) in m_Window)
            {
                covariance += (t - meanT) * (h - meanH);
                variance += (t - meanT) * (t - meanT);
            }
            return variance > 0 ? covariance / variance : 0;
        }

        static double Wrap(double radians)
        {
            while (radians > Math.PI)
                radians -= 2 * Math.PI;
            while (radians <= -Math.PI)
                radians += 2 * Math.PI;
            return radians;
        }
    }
}
