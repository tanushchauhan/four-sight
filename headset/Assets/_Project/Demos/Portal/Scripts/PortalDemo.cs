using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos.Portal
{
    // Open a doorway in your room and walk through it into another world. Look at the floor and
    // pinch: a portal stands there, facing you, anchored to the floor, with the world visible only
    // through it (see PortalWorld). Walk through the frame and you're in the world, with your room
    // showing through the door behind you; walk back through to return. Runs in passthrough, which
    // shows the room: so everything, the world included, is seen flat, the same picture in both eyes.
    // Walking through needs the phone off its cable. Move portal takes it down to put it somewhere
    // else. Logs [Portal].
    public class PortalDemo : Demo
    {
        [SerializeField] GameObject m_PortalPrefab;

        CoreServices m_Core;
        PortalWorld m_Portal;
        RectTransform m_Panel;
        TMP_Text m_Hint;
        AudioSource m_Audio;
        AudioClip m_Whoosh;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            core.Placer.ObjectPrefab = m_PortalPrefab;
            core.Placer.Placed += OnPlaced;
            m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Whoosh = Whoosh();
            BuildPanel();
            UpdateHint();
            Debug.Log("[Portal] Ready: look at the floor and pinch to open a portal");
        }

        public override void OnDemoExit(CoreServices core)
        {
            core.Placer.Placed -= OnPlaced;
            if (m_Portal != null)
                m_Portal.Crossed -= OnCrossed;
        }

        void OnPlaced(GameObject placed)
        {
            if (!placed.TryGetComponent<PortalWorld>(out var portal))
                return;
            // Standing on the floor: its origin is the bottom of the doorway.
            placed.transform.localPosition = Vector3.zero;
            m_Portal = portal;
            m_Portal.Crossed += OnCrossed;
            m_Core.Placer.ObjectPrefab = null;
            var distance = Vector3.Distance(m_Core.Head.position, placed.transform.position);
            Debug.Log($"[Portal] Opened {distance:F1} m away");
            // Softly, as it rises out of the floor.
            m_Audio.PlayOneShot(m_Whoosh, 0.4f);
            UpdateHint();
        }

        void OnCrossed(bool inside)
        {
            m_Audio.PlayOneShot(m_Whoosh, 0.7f);
            UpdateHint();
        }

        // Takes the portal down so it can be put somewhere else: it sinks into the floor first.
        void Move()
        {
            if (m_Portal == null)
                return;
            var portal = m_Portal;
            portal.Crossed -= OnCrossed;
            m_Portal = null;
            m_Audio.PlayOneShot(m_Whoosh, 0.3f);
            Debug.Log("[Portal] Closing it to move it");
            portal.Close(() =>
            {
                if (m_Core == null)
                    return;
                m_Core.Placer.RemoveAll();
                m_Core.Placer.ObjectPrefab = m_PortalPrefab;
                Debug.Log("[Portal] Taken down to move it");
                UpdateHint();
            });
            UpdateHint();
        }

        void UpdateHint()
        {
            if (m_Portal == null)
                m_Hint.text = "Look at the floor and pinch to open a portal there.\nNo highlight on the floor? Look down at it for a moment.";
            else if (!m_Portal.IsInside)
                m_Hint.text = "Walk through the doorway.\nUnplug the phone first if it's on a cable.";
            else
                m_Hint.text = "You're through. Look back: your room is behind the door.\nWalk back through to return.";
        }

        // A small Material panel ahead and a little above, clear of the floor where the portal goes:
        // what to do next, and Move portal. The lobby is on the settings panel.
        void BuildPanel()
        {
            const float distance = 1.5f;
            m_Panel = WorldUi.Panel("Portal Panel", transform, new Vector2(440f, 192f), distance);
            m_Hint = M3.Text("Hint", m_Panel, "", M3Theme.Type.BodyLarge, new Vector2(0f, 36f), new Vector2(392f, 72f));
            M3.Button("Move portal", m_Panel, "Move portal", M3Button.Variant.Tonal, new Vector2(0f, -44f), Move, "open_with");
            WorldUi.PlaceInFront(m_Panel, m_Core.Head, distance, 8f, -25f);
        }

        // A soft rush of noise that swells and fades, made in code.
        static AudioClip Whoosh()
        {
            const int rate = 44100;
            const float seconds = 0.7f;
            var samples = new float[(int)(rate * seconds)];
            var random = new System.Random(3);
            var low = 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (float)samples.Length;
                var noise = (float)(random.NextDouble() * 2.0 - 1.0);
                // A low-pass whose cutoff sweeps up then down, for the sense of something passing.
                var cutoff = Mathf.Lerp(0.02f, 0.25f, Mathf.Sin(t * Mathf.PI));
                low += (noise - low) * cutoff;
                samples[i] = low * Mathf.Sin(t * Mathf.PI) * 1.6f;
            }
            var clip = AudioClip.Create("Whoosh", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
