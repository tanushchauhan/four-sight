using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Recent pointing rays, so a pinch can aim with the ray from just before it: closing the fingers
    // moves the point between them, and with it the ray, by a median 5.6 degrees on the phone.
    public class AimHistory
    {
        readonly float m_KeepSeconds;
        // Oldest first.
        readonly List<(float time, Ray ray)> m_Rays = new();

        // Keeps rays for this long after the newest.
        public AimHistory(float keepSeconds) => m_KeepSeconds = keepSeconds;

        public int Count => m_Rays.Count;

        public void Add(float time, Ray ray)
        {
            m_Rays.Add((time, ray));
            while (m_Rays.Count > 0 && time - m_Rays[0].time > m_KeepSeconds)
                m_Rays.RemoveAt(0);
        }

        public void Clear() => m_Rays.Clear();

        // The newest ray from at or before the time, or the oldest kept if they're all newer; false if
        // there are none.
        public bool TryGetAt(float time, out Ray ray)
        {
            for (var i = m_Rays.Count - 1; i >= 0; i--)
            {
                if (m_Rays[i].time <= time)
                {
                    ray = m_Rays[i].ray;
                    return true;
                }
            }
            ray = m_Rays.Count > 0 ? m_Rays[0].ray : default;
            return m_Rays.Count > 0;
        }
    }
}
