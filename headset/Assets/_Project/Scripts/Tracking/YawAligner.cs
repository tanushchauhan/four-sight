using System;

namespace CardboardXR.Tracking
{
    // Estimates the heading (yaw) difference between two gravity-aligned tracking spaces with y up,
    // such as the head tracker's space and ARCore's, from rotations of the same object measured at
    // the same time in each. It starts once a few samples in a row agree, then averages headings on
    // the circle with a time constant, which smooths out noise while following the head tracker's
    // slow heading drift. A sample far from the estimate is ignored as an outlier, unless they keep
    // coming, which means one of the spaces turned (as when ARCore relocalizes); then it starts over.
    public sealed class YawAligner
    {
        // Samples whose spaces disagree about down by more than ~25 degrees are ignored.
        const double k_MinHorizontalForward = 0.9;
        const int k_SamplesToStart = 3;
        const double k_StartToleranceRadians = 5 * Math.PI / 180;
        const double k_OutlierRadians = 15 * Math.PI / 180;
        const int k_OutliersToRestart = 15;

        readonly double m_TimeConstantSeconds;
        long m_LastTimeNs;
        int m_Candidates;
        double m_CandidateYaw;
        int m_ConsecutiveOutliers;

        public YawAligner(double timeConstantSeconds = 2.0) => m_TimeConstantSeconds = timeConstantSeconds;

        // False until enough samples agree, and again while starting over.
        public bool IsInitialized { get; private set; }

        // The heading that takes the tracked space into the reference space, in radians around +y.
        public double YawRadians { get; private set; }

        public int Samples { get; private set; }

        // Samples ignored because the spaces disagreed about down.
        public int Rejected { get; private set; }

        // Samples ignored because they were far from the estimate.
        public int Outliers { get; private set; }

        public Rotation Correction => Rotation.FromAxisAndAngle(new Vector3d(0, 1, 0), YawRadians);

        public void Reset()
        {
            IsInitialized = false;
            Samples = 0;
            Rejected = 0;
            Outliers = 0;
            m_Candidates = 0;
            m_ConsecutiveOutliers = 0;
        }

        // reference and tracked are the object's rotation in each space at timeNs.
        public void AddSample(long timeNs, Rotation reference, Rotation tracked)
        {
            // The rotation from tracked space to reference space, ideally a turn around y.
            var forward = reference * tracked.Inverse * new Vector3d(0, 0, 1);
            if (Math.Sqrt(forward.x * forward.x + forward.z * forward.z) < k_MinHorizontalForward)
            {
                Rejected++;
                return;
            }

            var yaw = Math.Atan2(forward.x, forward.z);
            if (!IsInitialized)
            {
                AddCandidate(timeNs, yaw);
                return;
            }

            if (Math.Abs(Wrap(yaw - YawRadians)) > k_OutlierRadians)
            {
                Outliers++;
                if (++m_ConsecutiveOutliers >= k_OutliersToRestart)
                {
                    IsInitialized = false;
                    m_ConsecutiveOutliers = 0;
                    m_Candidates = 0;
                    AddCandidate(timeNs, yaw);
                }
                return;
            }

            m_ConsecutiveOutliers = 0;
            var seconds = Math.Max(0, (timeNs - m_LastTimeNs) * 1e-9);
            m_LastTimeNs = timeNs;
            Samples++;
            // A plain average over the first samples, then an exponential one.
            var gain = Math.Max(1.0 / Samples, 1 - Math.Exp(-seconds / m_TimeConstantSeconds));
            YawRadians = Wrap(YawRadians + Wrap(yaw - YawRadians) * gain);
        }

        // Starts from the average of the first samples in a row that agree.
        void AddCandidate(long timeNs, double yaw)
        {
            if (m_Candidates > 0 && Math.Abs(Wrap(yaw - m_CandidateYaw)) > k_StartToleranceRadians)
                m_Candidates = 0;
            m_Candidates++;
            m_CandidateYaw = m_Candidates == 1 ? yaw : Wrap(m_CandidateYaw + Wrap(yaw - m_CandidateYaw) / m_Candidates);
            m_LastTimeNs = timeNs;
            if (m_Candidates < k_SamplesToStart)
                return;

            YawRadians = m_CandidateYaw;
            IsInitialized = true;
            Samples = m_Candidates;
            m_Candidates = 0;
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
