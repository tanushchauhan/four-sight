using System;
using System.Collections.Generic;

namespace CardboardXR.Tracking
{
    // Estimates a position from linear acceleration, integrated at the IMU rate, and position fixes
    // that arrive less often and late, such as ARCore camera poses (30 Hz, 45-100 ms old). Each axis
    // is a Kalman filter over position, velocity and accelerometer bias; the axes share one
    // covariance because they see the same sample timing and noise. A late fix rewinds to the state
    // saved at its capture time, corrects it, and replays the accelerations that came after.
    public sealed class PositionFilter
    {
        // The state at a sample time, and the acceleration measured then, which holds until the next.
        struct Entry
        {
            public long timeNs;
            public Vector3d acceleration;
            public Vector3d position;
            public Vector3d velocity;
            public Vector3d bias;
            // Over (position, velocity, bias), shared by the three axes.
            public Matrix3x3d covariance;
        }

        // How far back a fix can land.
        const long k_HistoryNs = 500_000_000;
        // Pruning runs in batches once the history holds this many entries.
        const int k_PruneCount = 512;
        const double k_MaxExtrapolationSeconds = 0.1;
        const double k_InitialVelocityVariance = 0.25;
        const double k_InitialBiasVariance = 0.09;
        // Phone accelerometer biases, plus gravity leaking in from a few degrees of tilt error, stay
        // well under this, in m/s^2. A larger estimate means the filter is explaining something else.
        const double k_MaxBias = 0.5;
        // A fix further than max(this, 5 sigma) from the predicted position is rejected. ARCore's
        // good fixes land within ~4 cm even in quick moves; when it fails it can slide away meters in
        // a second while still claiming to track.
        const double k_MinGateMeters = 0.08;
        const double k_GateSigmas = 5;

        readonly double m_AccelerationVariance;
        readonly double m_BiasVariancePerSecond;
        readonly double m_PositionVariance;
        readonly List<Entry> m_History = new();
        // Accelerations before the first fix, which replay once it arrives.
        readonly List<(long timeNs, Vector3d acceleration)> m_Pending = new();
        long m_LastFixNs;

        // accelerationNoise: per-sample acceleration error in m/s^2, including gravity leaking in
        // through orientation error. biasDrift: how fast the accelerometer bias wanders, in
        // m/s^2 per root second. positionNoise: fix error in meters.
        public PositionFilter(double accelerationNoise = 0.5, double biasDrift = 0.05, double positionNoise = 0.005)
        {
            m_AccelerationVariance = accelerationNoise * accelerationNoise;
            m_BiasVariancePerSecond = biasDrift * biasDrift;
            m_PositionVariance = positionNoise * positionNoise;
        }

        // False until the first fix.
        public bool IsInitialized => m_History.Count > 0;

        public Vector3d Velocity => IsInitialized ? Latest.velocity : Vector3d.Zero;

        public Vector3d AccelerometerBias => IsInitialized ? Latest.bias : Vector3d.Zero;

        // Fixes that came out of capture order or older than the history, and were ignored.
        public int DroppedPositions { get; private set; }

        // Fixes too far from the prediction, which were ignored.
        public int RejectedPositions { get; private set; }

        // The latest rejected fix minus the position predicted for its time.
        public Vector3d LastRejectedInnovation { get; private set; }

        // How far accepted fixes landed from the predicted position, since the last call to
        // TakeInnovationStats. Small values mean the accelerometer integration agrees with the fixes.
        int m_InnovationCount;
        double m_InnovationSumSquares;
        double m_InnovationMax;

        public void TakeInnovationStats(out int count, out double rmsMeters, out double maxMeters)
        {
            count = m_InnovationCount;
            rmsMeters = count > 0 ? Math.Sqrt(m_InnovationSumSquares / count) : 0;
            maxMeters = m_InnovationMax;
            m_InnovationCount = 0;
            m_InnovationSumSquares = 0;
            m_InnovationMax = 0;
        }

        Entry Latest => m_History[m_History.Count - 1];

        public void Reset()
        {
            m_History.Clear();
            m_Pending.Clear();
            m_LastFixNs = 0;
            DroppedPositions = 0;
            RejectedPositions = 0;
        }

        // Linear acceleration (gravity removed) in the same frame as the fixes. Samples older than the
        // latest state are ignored.
        public void AddAcceleration(long timeNs, Vector3d acceleration)
        {
            if (!IsInitialized)
            {
                if (m_Pending.Count > 0 && timeNs <= m_Pending[m_Pending.Count - 1].timeNs)
                    return;
                m_Pending.Add((timeNs, acceleration));
                var stale = 0;
                while (stale < m_Pending.Count - 1 && m_Pending[stale].timeNs < timeNs - k_HistoryNs)
                    stale++;
                m_Pending.RemoveRange(0, stale);
                return;
            }

            var latest = Latest;
            if (timeNs < latest.timeNs)
                return;
            if (timeNs == latest.timeNs)
            {
                // Measured at the latest state's time, so it holds from there.
                latest.acceleration = acceleration;
                m_History[m_History.Count - 1] = latest;
                return;
            }

            var next = Propagate(latest, timeNs);
            next.acceleration = acceleration;
            m_History.Add(next);
            if (m_History.Count >= k_PruneCount)
                Prune(timeNs - k_HistoryNs);
        }

        // A position measured at timeNs, which may be earlier than the latest acceleration. Returns
        // false if it was dropped or rejected as a jump.
        public bool AddPosition(long timeNs, Vector3d position)
        {
            if (!IsInitialized)
            {
                Initialize(timeNs, position);
                return true;
            }

            if (timeNs <= m_LastFixNs || timeNs < m_History[0].timeNs)
            {
                DroppedPositions++;
                return false;
            }
            m_LastFixNs = timeNs;

            // Correct the state at the fix's time, keep it, and replay the later samples from it.
            var index = m_History.Count - 1;
            while (m_History[index].timeNs > timeNs)
                index--;
            var predicted = Propagate(m_History[index], timeNs);
            var distance = (position - predicted.position).Length;
            var gate = Math.Max(k_MinGateMeters, k_GateSigmas * Math.Sqrt(predicted.covariance[0, 0] + m_PositionVariance));
            if (distance > gate)
            {
                RejectedPositions++;
                LastRejectedInnovation = position - predicted.position;
                return false;
            }

            m_InnovationCount++;
            m_InnovationSumSquares += distance * distance;
            m_InnovationMax = Math.Max(m_InnovationMax, distance);
            m_History.Insert(index + 1, Correct(predicted, position));
            for (var i = index + 2; i < m_History.Count; i++)
            {
                var replayed = Propagate(m_History[i - 1], m_History[i].timeNs);
                replayed.acceleration = m_History[i].acceleration;
                m_History[i] = replayed;
            }
            return true;
        }

        // Puts the estimate at a position measured at timeNs, at rest; for picking up again after the
        // position was held or the estimate went wrong. keepBias keeps the accelerometer bias learned
        // so far, unless it may be part of what went wrong.
        public void Relocate(long timeNs, Vector3d position, bool keepBias = true)
        {
            if (!IsInitialized)
            {
                Initialize(timeNs, position);
                return;
            }

            var latest = Latest;
            var bias = keepBias ? latest.bias : Vector3d.Zero;
            var covariance = new Matrix3x3d();
            covariance[0, 0] = m_PositionVariance;
            covariance[1, 1] = k_InitialVelocityVariance;
            covariance[2, 2] = keepBias ? latest.covariance[2, 2] : k_InitialBiasVariance;
            m_History.Clear();
            m_Pending.Clear();
            // Holding an acceleration equal to the bias means no net acceleration until the next sample.
            m_History.Add(new Entry
            {
                timeNs = timeNs,
                acceleration = bias,
                position = position,
                bias = bias,
                covariance = covariance,
            });
            m_LastFixNs = timeNs;
        }

        // Extrapolates from the latest sample, at most 100 ms ahead.
        public Vector3d PredictPosition(long timeNs)
        {
            if (!IsInitialized)
                return Vector3d.Zero;
            var latest = Latest;
            var seconds = Math.Clamp((timeNs - latest.timeNs) * 1e-9, 0, k_MaxExtrapolationSeconds);
            var acceleration = latest.acceleration - latest.bias;
            return latest.position + latest.velocity * seconds + acceleration * (0.5 * seconds * seconds);
        }

        // Starts at rest at the first fix, holding the acceleration measured at or before it, then
        // replays the accelerations measured since.
        void Initialize(long timeNs, Vector3d position)
        {
            var covariance = new Matrix3x3d();
            covariance[0, 0] = m_PositionVariance;
            covariance[1, 1] = k_InitialVelocityVariance;
            covariance[2, 2] = k_InitialBiasVariance;
            var held = Vector3d.Zero;
            foreach (var (sampleNs, acceleration) in m_Pending)
            {
                if (sampleNs <= timeNs)
                    held = acceleration;
            }

            m_History.Add(new Entry { timeNs = timeNs, acceleration = held, position = position, covariance = covariance });
            m_LastFixNs = timeNs;
            var pending = m_Pending.ToArray();
            m_Pending.Clear();
            foreach (var (sampleNs, acceleration) in pending)
            {
                if (sampleNs > timeNs)
                    AddAcceleration(sampleNs, acceleration);
            }
        }

        // Moves a state forward to toNs, holding its acceleration.
        Entry Propagate(Entry from, long toNs)
        {
            var to = from;
            to.timeNs = toNs;
            var dt = (toNs - from.timeNs) * 1e-9;
            if (dt <= 0)
                return to;

            var halfDt2 = 0.5 * dt * dt;
            var acceleration = from.acceleration - from.bias;
            to.position = from.position + from.velocity * dt + acceleration * halfDt2;
            to.velocity = from.velocity + acceleration * dt;

            var transition = Matrix3x3d.Identity;
            transition[0, 1] = dt;
            transition[0, 2] = -halfDt2;
            transition[1, 2] = -dt;
            // Acceleration noise enters position and velocity together; the bias wanders on its own.
            var noise = new Matrix3x3d();
            noise[0, 0] = m_AccelerationVariance * halfDt2 * halfDt2;
            noise[0, 1] = noise[1, 0] = m_AccelerationVariance * halfDt2 * dt;
            noise[1, 1] = m_AccelerationVariance * dt * dt;
            noise[2, 2] = m_BiasVariancePerSecond * dt;
            to.covariance = transition * from.covariance * transition.Transpose() + noise;
            return to;
        }

        Entry Correct(Entry state, Vector3d measured)
        {
            var p = state.covariance;
            var innovationVariance = p[0, 0] + m_PositionVariance;
            var gain = new Vector3d(p[0, 0], p[1, 0], p[2, 0]) / innovationVariance;
            var innovation = measured - state.position;
            state.position += innovation * gain.x;
            state.velocity += innovation * gain.y;
            state.bias += innovation * gain.z;
            if (state.bias.Length > k_MaxBias)
                state.bias = state.bias.Normalized * k_MaxBias;

            var corrected = new Matrix3x3d();
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 3; col++)
                    corrected[row, col] = p[row, col] - gain[row] * p[0, col];
            }
            // Keep it symmetric against rounding.
            state.covariance = (corrected + corrected.Transpose()) * 0.5;
            return state;
        }

        void Prune(long cutoffNs)
        {
            var remove = 0;
            while (remove < m_History.Count - 1 && m_History[remove + 1].timeNs <= cutoffNs)
                remove++;
            if (remove > 0)
                m_History.RemoveRange(0, remove);
        }
    }
}
