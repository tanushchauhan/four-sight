using System;
using System.Collections.Generic;

namespace CardboardXR.Tracking
{
    // First-order low-pass filter for timestamped 3D samples, with a cutoff frequency.
    // Ported from the Cardboard SDK (sdk/sensors/lowpass_filter.cc, Apache License 2.0).
    public sealed class LowpassFilter
    {
        const double k_SecondsFromNanoseconds = 1e-9;
        // Updates closer than 1 ms or further apart than 1 s are skipped (the SDK's float constants).
        const double k_MinTimestepS = 0.001f;
        const double k_MaxTimestepS = 1.00f;

        readonly double m_CutoffTimeConstant;
        Vector3d m_FilteredData;

        public LowpassFilter(double cutoffFrequencyHz)
        {
            m_CutoffTimeConstant = 1.0 / (2.0 * Math.PI * cutoffFrequencyHz);
            Reset();
        }

        public Vector3d FilteredData => m_FilteredData;
        public long MostRecentTimestampNs { get; private set; }
        public bool IsInitialized { get; private set; }

        public void AddSample(Vector3d sample, long timestampNs) => AddWeightedSample(sample, timestampNs, 1.0);

        public void AddWeightedSample(Vector3d sample, long timestampNs, double weight)
        {
            if (!IsInitialized)
            {
                m_FilteredData = sample;
                MostRecentTimestampNs = timestampNs;
                IsInitialized = true;
                return;
            }

            if (timestampNs < MostRecentTimestampNs)
            {
                MostRecentTimestampNs = timestampNs;
                return;
            }

            var deltaS = (timestampNs - MostRecentTimestampNs) * k_SecondsFromNanoseconds;
            if (deltaS <= k_MinTimestepS || deltaS > k_MaxTimestepS)
            {
                MostRecentTimestampNs = timestampNs;
                return;
            }

            var weightedDeltaS = weight * deltaS;
            var alpha = weightedDeltaS / (m_CutoffTimeConstant + weightedDeltaS);
            m_FilteredData = (1.0 - alpha) * m_FilteredData + alpha * sample;
            MostRecentTimestampNs = timestampNs;
        }

        public void Reset()
        {
            IsInitialized = false;
            m_FilteredData = Vector3d.Zero;
        }
    }

    // Mean of the last few samples, divided by the full window size like the SDK.
    // Ported from the Cardboard SDK (sdk/sensors/mean_filter.cc, Apache License 2.0).
    public sealed class MeanFilter
    {
        readonly int m_Size;
        readonly Queue<Vector3d> m_Buffer = new();

        public MeanFilter(int size) => m_Size = size;

        public bool IsValid => m_Buffer.Count == m_Size;

        public void AddSample(Vector3d sample)
        {
            m_Buffer.Enqueue(sample);
            if (m_Buffer.Count > m_Size)
                m_Buffer.Dequeue();
        }

        public Vector3d FilteredData
        {
            get
            {
                var mean = Vector3d.Zero;
                foreach (var sample in m_Buffer)
                    mean += sample;
                return mean / m_Size;
            }
        }
    }

    // The sample with the median length among the last few samples.
    // Ported from the Cardboard SDK (sdk/sensors/median_filter.cc, Apache License 2.0).
    public sealed class MedianFilter
    {
        readonly int m_Size;
        readonly List<Vector3d> m_Buffer = new();
        readonly List<float> m_Norms = new();
        readonly List<float> m_SortedNorms = new();

        public MedianFilter(int size) => m_Size = size;

        public bool IsValid => m_Buffer.Count == m_Size;

        public void AddSample(Vector3d sample)
        {
            m_Buffer.Add(sample);
            m_Norms.Add((float)sample.Length);
            if (m_Buffer.Count > m_Size)
            {
                m_Buffer.RemoveAt(0);
                m_Norms.RemoveAt(0);
            }
        }

        public Vector3d FilteredData
        {
            get
            {
                m_SortedNorms.Clear();
                m_SortedNorms.AddRange(m_Norms);
                m_SortedNorms.Sort();
                var medianNorm = m_SortedNorms[m_Size / 2];
                var index = m_Norms.IndexOf(medianNorm);
                return m_Buffer[index];
            }
        }

        public void Reset()
        {
            m_Buffer.Clear();
            m_Norms.Clear();
        }
    }
}
