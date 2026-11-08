using UnityEngine;

namespace CardboardXR.Demos.Rush
{
    // One incoming orb, a child of the track, whose +z points away from the player and whose origin
    // is where their eyes were at the start. It flies in along -z toward its arrival spot, marked by
    // a faint ring that brightens as it nears. Hit, it bursts; missed, it greys and flies on past.
    public class RushOrb : MonoBehaviour
    {
        const float k_BurstSeconds = 0.18f;
        const float k_FadeSeconds = 0.4f;
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");
        static readonly Color k_Grey = new(0.35f, 0.35f, 0.4f);

        Vector3 m_Arrival;
        float m_ArrivalTime;
        float m_Speed;
        Color m_Color;
        Renderer m_Core;
        Renderer m_Halo;
        Renderer m_Ring;
        MaterialPropertyBlock m_Block;
        float m_EndedAt = -1f;
        // The game's clock, which stops while it's paused.
        System.Func<float> m_Clock;

        // Still coming, neither hit nor missed.
        public bool IsActive => m_EndedAt < 0f;
        public bool IsLive { get; private set; } = true;
        public bool IsDone { get; private set; }

        // Its position along the track now, in the track's space.
        public Vector3 LocalPosition => transform.localPosition;

        public void Launch(System.Func<float> clock, Vector3 arrival, float arrivalTime, float speed, Color color, Renderer core,
            Renderer halo, Renderer ring)
        {
            m_Clock = clock;
            m_Arrival = arrival;
            m_ArrivalTime = arrivalTime;
            m_Speed = speed;
            m_Color = color;
            m_Core = core;
            m_Halo = halo;
            m_Ring = ring;
            m_Block = new MaterialPropertyBlock();
            // The ring sits at the arrival spot, facing the player, and stays there.
            m_Ring.transform.SetParent(transform.parent, false);
            m_Ring.transform.SetLocalPositionAndRotation(arrival, Quaternion.identity);
            m_Ring.transform.localScale = Vector3.one * (RushRules.k_OrbRadius * 2.6f);
            // Where it starts, now: it's created at the track's origin, at the player's eyes, and Update
            // only runs from the next frame, so it was drawn there for a frame, a flash of colour.
            Fly();
            UpdateVisuals(0f);
        }

        // Along the track toward its arrival spot, by the game's clock.
        void Fly() => transform.localPosition = m_Arrival + Vector3.forward * (m_Speed * (m_ArrivalTime - m_Clock()));

        void Update()
        {
            // It keeps coming whether hit or not, so a burst happens where it was touched.
            if (m_EndedAt < 0f || !IsLive)
                Fly();

            if (m_EndedAt < 0f)
            {
                var toGo = Mathf.Clamp01((transform.localPosition.z - m_Arrival.z) / RushRules.k_SpawnDistance);
                UpdateVisuals(1f - toGo);
                return;
            }

            var since = m_Clock() - m_EndedAt;
            if (IsLive)
            {
                // Hit: grows and fades quickly.
                var t = Mathf.Clamp01(since / k_BurstSeconds);
                transform.localScale = Vector3.one * Mathf.Lerp(1f, 2.4f, t);
                SetColor(m_Core, k_BaseColor, m_Color * (1f - t));
                SetColor(m_Halo, k_BaseColor, Color.white * (1f - t));
                if (t >= 1f)
                    Finish();
            }
            else
            {
                var t = Mathf.Clamp01(since / k_FadeSeconds);
                SetColor(m_Core, k_BaseColor, Color.Lerp(k_Grey, Color.black, t));
                SetColor(m_Halo, k_BaseColor, Color.black);
                if (t >= 1f)
                    Finish();
            }
        }

        // Nearing: the ring brightens and tightens, the halo glows in the orb's color.
        void UpdateVisuals(float nearness)
        {
            SetColor(m_Core, k_BaseColor, m_Color);
            SetColor(m_Halo, k_BaseColor, m_Color * 0.5f);
            var ring = m_Color;
            ring.a = Mathf.Lerp(0.15f, 0.9f, nearness * nearness);
            SetColor(m_Ring, k_Color, ring);
        }

        public void Hit()
        {
            IsLive = true;
            m_EndedAt = m_Clock();
            HideRing();
        }

        public void Miss()
        {
            IsLive = false;
            m_EndedAt = m_Clock();
            HideRing();
        }

        void HideRing()
        {
            if (m_Ring != null)
                Destroy(m_Ring.gameObject);
        }

        void Finish()
        {
            IsDone = true;
            Destroy(gameObject);
        }

        void OnDestroy() => HideRing();

        void SetColor(Renderer renderer, int id, Color color)
        {
            if (renderer == null)
                return;
            renderer.GetPropertyBlock(m_Block);
            m_Block.SetColor(id, color);
            renderer.SetPropertyBlock(m_Block);
        }
    }
}
