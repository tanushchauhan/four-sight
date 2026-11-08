namespace CardboardXR.Tracking
{
    // Estimates the gyroscope's bias by averaging it while the phone is still, with stillness judged
    // from both the gyroscope and the accelerometer. Feed it every sample, at 10 Hz or more.
    // Ported from the Cardboard SDK (sdk/sensors/gyroscope_bias_estimator.cc, Apache License 2.0).
    public sealed class GyroscopeBiasEstimator
    {
        const float k_AccelerometerLowPassCutOffFrequencyHz = 1.0f;
        const float k_RotationVelocityBasedAccelerometerLowPassCutOffFrequencyHz = 0.15f;
        const float k_GyroscopeLowPassCutOffFrequencyHz = 1.0f;
        const float k_GyroscopeBiasLowPassCutOffFrequencyHz = 0.15f;
        const float k_Epsilon = 1e-8f;
        // Window of the mean and median filters; larger windows add delay.
        const int k_FilterWindowSize = 5;
        const float k_RatioBetweenGyroBiasAndAccel = 1.5f;
        // Minimum sum of sample weights before the estimate counts.
        const float k_MinSumOfWeightsGyroBiasThreshold = 25f;
        // Change allowed in the smoothed accelerometer (m/s^3) and gyroscope (rad/s^2) while static.
        const float k_AccelerometerDeltaStaticThreshold = 0.5f;
        const float k_GyroscopeDeltaStaticThreshold = 0.03f;
        // Gyroscope readings above this (rad/s) could be slow head turns, so they never update the bias.
        const float k_GyroscopeForBiasThreshold = 0.30f;
        const int k_StaticFrameDetectionThreshold = 50;
        const double k_MinTimestep = 1;

        // Counts consecutive static frames of one signal.
        sealed class IsStaticCounter
        {
            readonly int m_Threshold;
            int m_ConsecutiveStaticFrames;

            public IsStaticCounter(int threshold) => m_Threshold = threshold;

            public bool IsRecentlyStatic => m_ConsecutiveStaticFrames >= m_Threshold;

            public void AppendFrame(bool isStatic) => m_ConsecutiveStaticFrames = isStatic ? m_ConsecutiveStaticFrames + 1 : 0;

            public void Reset() => m_ConsecutiveStaticFrames = 0;
        }

        readonly LowpassFilter m_AccelerometerLowpass = new(k_AccelerometerLowPassCutOffFrequencyHz);
        readonly LowpassFilter m_SimulatedGyroscopeFromAccelerometerLowpass = new(k_RotationVelocityBasedAccelerometerLowPassCutOffFrequencyHz);
        readonly LowpassFilter m_GyroscopeLowpass = new(k_GyroscopeLowPassCutOffFrequencyHz);
        readonly LowpassFilter m_GyroscopeBiasLowpass = new(k_GyroscopeBiasLowPassCutOffFrequencyHz);
        readonly IsStaticCounter m_AccelerometerStaticCounter = new(k_StaticFrameDetectionThreshold);
        readonly IsStaticCounter m_GyroscopeStaticCounter = new(k_StaticFrameDetectionThreshold);
        readonly MeanFilter m_MeanFilter = new(k_FilterWindowSize);
        readonly MedianFilter m_MedianFilter = new(k_FilterWindowSize);
        float m_AccumulatedGyroscopeBiasWeights;
        Vector3d m_LastMeanFilteredAccelerometer;

        public GyroscopeBiasEstimator() => Reset();

        public void Reset()
        {
            m_AccelerometerLowpass.Reset();
            m_GyroscopeLowpass.Reset();
            m_GyroscopeBiasLowpass.Reset();
            m_AccelerometerStaticCounter.Reset();
            m_GyroscopeStaticCounter.Reset();
        }

        public Vector3d GyroscopeBias => m_GyroscopeBiasLowpass.FilteredData;

        public void ProcessGyroscope(Vector3d gyroscope, long timestampNs)
        {
            m_GyroscopeLowpass.AddSample(gyroscope, timestampNs);
            var smoothedDelta = gyroscope - m_GyroscopeLowpass.FilteredData;
            m_GyroscopeStaticCounter.AppendFrame(smoothedDelta.Length < k_GyroscopeDeltaStaticThreshold);

            // Only update the bias while both signals have been fairly static.
            if (m_GyroscopeStaticCounter.IsRecentlyStatic && m_AccelerometerStaticCounter.IsRecentlyStatic)
            {
                // A failed update means large motion.
                if (!UpdateGyroscopeBias(gyroscope, timestampNs))
                    m_GyroscopeStaticCounter.AppendFrame(false);
            }
            else
            {
                m_AccumulatedGyroscopeBiasWeights = 0f;
            }
        }

        // Low-pass, median and mean filters the accelerometer, then turns the rotation between
        // filtered samples into a simulated gyroscope reading.
        public void ProcessAccelerometer(Vector3d accelerometer, long timestampNs)
        {
            var previousTimestampNs = m_AccelerometerLowpass.MostRecentTimestampNs;
            var wasInitialized = m_AccelerometerLowpass.IsInitialized;

            m_AccelerometerLowpass.AddSample(accelerometer, timestampNs);
            var smoothedDelta = accelerometer - m_AccelerometerLowpass.FilteredData;
            m_AccelerometerStaticCounter.AppendFrame(smoothedDelta.Length < k_AccelerometerDeltaStaticThreshold);

            // One sample can't be differentiated.
            if (!wasInitialized)
            {
                m_SimulatedGyroscopeFromAccelerometerLowpass.AddSample(Vector3d.Zero, timestampNs);
                return;
            }

            // Too much motion to update the simulated gyroscope.
            if (!m_AccelerometerStaticCounter.IsRecentlyStatic)
                return;

            m_MedianFilter.AddSample(m_AccelerometerLowpass.FilteredData);
            if (!m_MedianFilter.IsValid)
            {
                m_MeanFilter.AddSample(m_AccelerometerLowpass.FilteredData);
                m_LastMeanFilteredAccelerometer = m_AccelerometerLowpass.FilteredData;
                return;
            }

            m_MeanFilter.AddSample(m_MedianFilter.FilteredData);

            // The SDK divides by the timestep in nanoseconds here (not seconds); kept as is.
            var timestep = (double)(timestampNs - previousTimestampNs);
            m_SimulatedGyroscopeFromAccelerometerLowpass.AddSample(ComputeAngularVelocityFromLatestAccelerometer(timestep), timestampNs);
            m_LastMeanFilteredAccelerometer = m_MeanFilter.FilteredData;
        }

        Vector3d ComputeAngularVelocityFromLatestAccelerometer(double timestep)
        {
            if (timestep < k_MinTimestep)
                return Vector3d.Zero;

            var incremental = Rotation.RotateInto(m_LastMeanFilteredAccelerometer, m_MeanFilter.FilteredData);
            incremental.GetAxisAndAngle(out var axis, out var angle);
            return axis * (angle / timestep);
        }

        // Weights samples closer to zero more, so the estimate converges faster on a table than in
        // hand. Returns false for readings too large to be bias.
        bool UpdateGyroscopeBias(Vector3d gyroscope, long timestampNs)
        {
            var norm = (float)gyroscope.Length;
            if (norm >= k_GyroscopeForBiasThreshold)
                return false;

            var weight = 1.0f - norm / k_GyroscopeForBiasThreshold;
            weight *= weight;
            m_GyroscopeBiasLowpass.AddWeightedSample(m_GyroscopeLowpass.FilteredData, timestampNs, weight);
            m_AccumulatedGyroscopeBiasWeights += weight;
            return true;
        }

        public bool IsCurrentEstimateValid
        {
            get
            {
                // Bias along gravity can't be checked against the accelerometer, so ignore it.
                var gravityDirection = m_LastMeanFilteredAccelerometer.Normalized;
                var bias = m_GyroscopeBiasLowpass.FilteredData;
                var offGravityBias = bias - gravityDirection * Vector3d.Dot(bias, gravityDirection);

                // The estimate shouldn't correlate with the rotation seen by the accelerometer.
                var gyroscopeFromAccelerometer = m_SimulatedGyroscopeFromAccelerometerLowpass.FilteredData;
                var correlated = gyroscopeFromAccelerometer.Length * k_RatioBetweenGyroBiasAndAccel >
                                 offGravityBias.Length + k_Epsilon;
                var hasEnoughSamples = m_AccumulatedGyroscopeBiasWeights > k_MinSumOfWeightsGyroBiasThreshold;
                var countersStatic = m_GyroscopeStaticCounter.IsRecentlyStatic && m_AccelerometerStaticCounter.IsRecentlyStatic;
                return hasEnoughSamples && countersStatic && !correlated;
            }
        }
    }
}
