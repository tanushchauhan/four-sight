using System;

namespace CardboardXR.Tracking
{
    // One gyroscope or accelerometer reading. sensorTimestampNs orders and spaces samples;
    // systemTimestampNs is on the clock prediction requests use.
    public readonly struct SensorSample
    {
        public readonly long systemTimestampNs;
        public readonly long sensorTimestampNs;
        public readonly Vector3d data;

        public SensorSample(long systemTimestampNs, long sensorTimestampNs, Vector3d data)
        {
            this.systemTimestampNs = systemTimestampNs;
            this.sensorTimestampNs = sensorTimestampNs;
            this.data = data;
        }
    }

    // Extended Kalman filter that estimates the phone's rotation from the gyroscope (integrated every
    // sample, with bias removed) and the accelerometer (gravity gently corrects tilt, trusted less
    // when the phone accelerates). Its only state is the rotation.
    // Rotations are sensor-from-start: x_sensor = rotation * x_start, with start space's z along
    // gravity's reaction (up). Samples are right-handed: rad/s about the sensor axes, and m/s^2 that
    // point up when the phone is still.
    // Ported from the Cardboard SDK (sdk/sensors/sensor_fusion_ekf.cc, Apache License 2.0), without
    // its mutex since samples arrive on Unity's main thread.
    public sealed class SensorFusionEkf
    {
        const double k_FiniteDifferencingEpsilon = 1e-7;
        const double k_Epsilon = 1e-15;
        // 100 Hz, used when gyroscope samples arrive too far apart.
        const double k_DefaultGyroscopeTimestepS = 0.01f;
        const double k_MaximumGyroscopeSampleDelayS = 0.04f;
        // Moving average of accelerometer norm changes.
        const double k_SmoothingFactor = 0.5;
        // Accelerometer noise sigma range; smaller trusts the accelerometer more.
        const double k_MinAccelNoiseSigma = 0.75;
        const double k_MaxAccelNoiseSigma = 7.0;
        const double k_InitialStateCovarianceValue = 25.0;
        const double k_InitialProcessCovarianceValue = 1.0;
        // Accelerometer norm change at which its noise is capped at the maximum.
        const double k_MaxAccelNormChange = 0.15;
        const double k_TimestepFilterCoeff = 0.95;
        const int k_TimestepFilterMinSamples = 10;

        static readonly Vector3d k_CanonicalZDirection = new(0, 0, 1);

        readonly GyroscopeBiasEstimator m_BiasEstimator = new();
        LowpassFilter m_VelocityFilter;

        long m_StateTimestampNs;
        Rotation m_SensorFromStart;
        Vector3d m_Velocity;

        bool m_TimestepFilterInitialized;
        bool m_GyroscopeTimestepValid;
        bool m_ResetWithNextAccelerometerSample;

        Matrix3x3d m_StateCovariance;
        Matrix3x3d m_ProcessCovariance;
        Matrix3x3d m_AccelerometerMeasurementCovariance;
        Matrix3x3d m_AccelerometerMeasurementJacobian;
        Vector3d m_Innovation;
        Vector3d m_AccelerometerMeasurement;

        long m_GyroscopeTimestampNs;
        long m_AccelerometerTimestampNs;
        double m_FilteredGyroscopeTimestepS;
        int m_GyroscopeTimestepSamples;
        double m_PreviousAccelerometerNorm;
        double m_MovingAverageAccelerometerNormChange;
        Vector3d m_GyroscopeBias;

        public SensorFusionEkf() => ResetState();

        // False until the first accelerometer sample sets the initial orientation.
        public bool IsAlignedWithGravity { get; private set; }

        public Rotation SensorFromStart => m_SensorFromStart;
        public Vector3d GyroscopeBias => m_GyroscopeBias;

        // Starts over with the next accelerometer sample; gyroscope samples are dropped until then.
        public void Reset() => m_ResetWithNextAccelerometerSample = true;

        // The rotation at a later time, extrapolated linearly from the latest angular velocity.
        // Zero returns the current rotation.
        public Rotation PredictRotation(long requestedTimestampNs)
        {
            if (requestedTimestampNs == 0)
                return m_SensorFromStart;

            var timestepS = (requestedTimestampNs - m_StateTimestampNs) * 1e-9;
            return RotationFromGyroscope(m_Velocity, timestepS) * m_SensorFromStart;
        }

        public void ProcessGyroscopeSample(SensorSample sample)
        {
            if (m_ResetWithNextAccelerometerSample)
                return;
            // Drop samples that aren't newer than the last one.
            if (m_GyroscopeTimestampNs >= sample.sensorTimestampNs)
                return;

            if (m_GyroscopeTimestampNs != 0)
            {
                var timestepS = (sample.sensorTimestampNs - m_GyroscopeTimestampNs) * 1e-9;
                if (timestepS > k_MaximumGyroscopeSampleDelayS)
                    timestepS = m_GyroscopeTimestepValid ? m_FilteredGyroscopeTimestepS : k_DefaultGyroscopeTimestepS;
                else
                    FilterGyroscopeTimestep(timestepS);

                m_BiasEstimator.ProcessGyroscope(sample.data, sample.sensorTimestampNs);
                // Once the phone has been still, the estimator's bias is accurate.
                if (m_BiasEstimator.IsCurrentEstimateValid)
                    m_GyroscopeBias = m_BiasEstimator.GyroscopeBias;

                // Integrate only once gravity has set the initial orientation.
                if (IsAlignedWithGravity)
                {
                    var rotationFromGyroscope = RotationFromGyroscope(sample.data - m_GyroscopeBias, timestepS);
                    m_SensorFromStart = rotationFromGyroscope * m_SensorFromStart;
                    UpdateStateCovariance(Matrix3x3d.FromRotation(rotationFromGyroscope));
                    m_StateCovariance = m_StateCovariance + timestepS * timestepS * m_ProcessCovariance;
                }
            }

            // Kept for prediction.
            m_StateTimestampNs = sample.systemTimestampNs;
            m_GyroscopeTimestampNs = sample.sensorTimestampNs;

            if (m_VelocityFilter != null)
            {
                m_VelocityFilter.AddSample(sample.data - m_GyroscopeBias, m_GyroscopeTimestampNs);
                if (m_VelocityFilter.IsInitialized)
                    m_Velocity = m_VelocityFilter.FilteredData;
            }
            else
            {
                m_Velocity = sample.data - m_GyroscopeBias;
            }
        }

        public void ProcessAccelerometerSample(SensorSample sample)
        {
            if (m_AccelerometerTimestampNs >= sample.sensorTimestampNs)
                return;

            if (m_ResetWithNextAccelerometerSample)
            {
                m_ResetWithNextAccelerometerSample = false;
                ResetState();
            }

            m_AccelerometerMeasurement = sample.data;
            m_AccelerometerTimestampNs = sample.sensorTimestampNs;
            m_BiasEstimator.ProcessAccelerometer(sample.data, sample.sensorTimestampNs);

            if (!IsAlignedWithGravity)
            {
                // The first measurement sets the orientation.
                m_SensorFromStart = Rotation.RotateInto(k_CanonicalZDirection, m_AccelerometerMeasurement);
                IsAlignedWithGravity = true;
                m_PreviousAccelerometerNorm = m_AccelerometerMeasurement.Length;
                return;
            }

            UpdateMeasurementCovariance();

            m_Innovation = ComputeInnovation(m_SensorFromStart);
            ComputeMeasurementJacobian();

            var jacobianTranspose = m_AccelerometerMeasurementJacobian.Transpose();
            // S = H * P * H' + R
            var innovationCovariance = m_AccelerometerMeasurementJacobian * m_StateCovariance * jacobianTranspose +
                                       m_AccelerometerMeasurementCovariance;
            // K = P * H' * S^-1
            var kalmanGain = m_StateCovariance * jacobianTranspose * innovationCovariance.Inverse();
            // x_update = K * nu
            var stateUpdate = kalmanGain * m_Innovation;
            // P = (I - K * H) * P
            m_StateCovariance = (Matrix3x3d.Identity - kalmanGain * m_AccelerometerMeasurementJacobian) * m_StateCovariance;

            var rotationFromStateUpdate = RotationFromVector(stateUpdate);
            m_SensorFromStart = rotationFromStateUpdate * m_SensorFromStart;
            UpdateStateCovariance(Matrix3x3d.FromRotation(rotationFromStateUpdate));
        }

        void ResetState()
        {
            m_SensorFromStart = Rotation.Identity;
            m_Velocity = Vector3d.Zero;
            m_StateTimestampNs = 0;
            m_GyroscopeTimestampNs = 0;
            m_AccelerometerTimestampNs = 0;

            m_StateCovariance = Matrix3x3d.Identity * k_InitialStateCovarianceValue;
            m_ProcessCovariance = Matrix3x3d.Identity * k_InitialProcessCovarianceValue;
            m_AccelerometerMeasurementCovariance = Matrix3x3d.Identity * (k_MinAccelNoiseSigma * k_MinAccelNoiseSigma);
            m_AccelerometerMeasurementJacobian = Matrix3x3d.Zero;
            m_Innovation = Vector3d.Zero;
            m_AccelerometerMeasurement = Vector3d.Zero;
            m_MovingAverageAccelerometerNormChange = 0.0;

            m_TimestepFilterInitialized = false;
            m_GyroscopeTimestepValid = false;
            IsAlignedWithGravity = false;

            m_BiasEstimator.Reset();
            m_GyroscopeBias = Vector3d.Zero;
            m_VelocityFilter?.Reset();
        }

        // Axis-angle rotation from a vector: angle = |a|, axis = a / |a|.
        static Rotation RotationFromVector(Vector3d a)
        {
            var norm = a.Length;
            return norm < k_Epsilon ? Rotation.Identity : Rotation.FromAxisAndAngle(a / norm, norm);
        }

        // Integrates an angular velocity over a timestep. The gyroscope measures start-from-sensor
        // rotation, so it is inverted (the minus sign) for sensor-from-start.
        static Rotation RotationFromGyroscope(Vector3d gyroscope, double timestepS)
        {
            var velocity = gyroscope.Length;
            return velocity < k_Epsilon
                ? Rotation.Identity
                : Rotation.FromAxisAndAngle(gyroscope / velocity, -timestepS * velocity);
        }

        Vector3d ComputeInnovation(Rotation rotation)
        {
            var predictedDownDirection = rotation * k_CanonicalZDirection;
            Rotation.RotateInto(predictedDownDirection, m_AccelerometerMeasurement).GetAxisAndAngle(out var axis, out var angle);
            return axis * angle;
        }

        // Measurement Jacobian by numerical differentiation around the current rotation.
        void ComputeMeasurementJacobian()
        {
            for (var dof = 0; dof < 3; dof++)
            {
                var delta = Vector3d.Zero;
                delta[dof] = k_FiniteDifferencingEpsilon;
                var deltaRotation = ComputeInnovation(RotationFromVector(delta) * m_SensorFromStart);
                var column = (m_Innovation - deltaRotation) / k_FiniteDifferencingEpsilon;
                m_AccelerometerMeasurementJacobian[0, dof] = column.x;
                m_AccelerometerMeasurementJacobian[1, dof] = column.y;
                m_AccelerometerMeasurementJacobian[2, dof] = column.z;
            }
        }

        void UpdateStateCovariance(Matrix3x3d motionUpdate) =>
            m_StateCovariance = motionUpdate * m_StateCovariance * motionUpdate.Transpose();

        void FilterGyroscopeTimestep(double timestepS)
        {
            if (!m_TimestepFilterInitialized)
            {
                m_FilteredGyroscopeTimestepS = timestepS;
                m_GyroscopeTimestepSamples = 1;
                m_TimestepFilterInitialized = true;
                return;
            }

            m_FilteredGyroscopeTimestepS = k_TimestepFilterCoeff * m_FilteredGyroscopeTimestepS + (1 - k_TimestepFilterCoeff) * timestepS;
            if (++m_GyroscopeTimestepSamples > k_TimestepFilterMinSamples)
                m_GyroscopeTimestepValid = true;
        }

        // A changing accelerometer norm means acceleration beyond gravity, so trust it less.
        void UpdateMeasurementCovariance()
        {
            var norm = m_AccelerometerMeasurement.Length;
            var normChange = Math.Abs(norm - m_PreviousAccelerometerNorm);
            m_PreviousAccelerometerNorm = norm;

            m_MovingAverageAccelerometerNormChange = k_SmoothingFactor * normChange +
                                                     (1.0 - k_SmoothingFactor) * m_MovingAverageAccelerometerNormChange;

            var normChangeRatio = m_MovingAverageAccelerometerNormChange / k_MaxAccelNormChange;
            var sigma = Math.Min(k_MaxAccelNoiseSigma, k_MinAccelNoiseSigma + normChangeRatio * (k_MaxAccelNoiseSigma - k_MinAccelNoiseSigma));
            m_AccelerometerMeasurementCovariance = Matrix3x3d.Identity * (sigma * sigma);
        }
    }
}
