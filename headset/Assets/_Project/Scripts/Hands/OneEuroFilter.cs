using UnityEngine;

namespace CardboardXR.Hands
{
    // The One Euro filter (Casiez, Roussel and Vogel, 2012): a low-pass filter whose cutoff rises with
    // speed, so a still hand holds steady and a moving one keeps up. The cutoff is minCutoff Hz at
    // rest and rises by beta Hz per unit per second of smoothed speed.
    public class OneEuroFilter
    {
        readonly float m_MinCutoff;
        readonly float m_Beta;
        readonly float m_DerivativeCutoff;
        Vector3 m_Value;
        Vector3 m_Derivative;
        double m_LastTime;
        bool m_HasValue;

        public OneEuroFilter(float minCutoff, float beta, float derivativeCutoff = 1f)
        {
            m_MinCutoff = minCutoff;
            m_Beta = beta;
            m_DerivativeCutoff = derivativeCutoff;
        }

        // Starts over, so the next value is taken as it is.
        public void Reset() => m_HasValue = false;

        // Time is in seconds; a sample no newer than the last returns the current value.
        public Vector3 Filter(Vector3 value, double time)
        {
            if (!m_HasValue)
            {
                m_Value = value;
                m_Derivative = Vector3.zero;
                m_LastTime = time;
                m_HasValue = true;
                return value;
            }
            if (time <= m_LastTime)
                return m_Value;

            var dt = (float)(time - m_LastTime);
            m_LastTime = time;
            m_Derivative = Vector3.Lerp(m_Derivative, (value - m_Value) / dt, Alpha(m_DerivativeCutoff, dt));
            var cutoff = m_MinCutoff + m_Beta * m_Derivative.magnitude;
            m_Value = Vector3.Lerp(m_Value, value, Alpha(cutoff, dt));
            return m_Value;
        }

        static float Alpha(float cutoff, float dt)
        {
            var tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
