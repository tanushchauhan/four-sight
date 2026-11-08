using UnityEngine;

namespace CardboardXR.UI
{
    // Fades a panel in when it's shown and out when it's hidden, as Material's motion does for
    // surfaces that appear and leave: opacity with a slight grow, quick and decelerating in, quicker
    // out. Fading in is automatic, whenever the object is turned on; to fade out, hide it with
    // M3Fade.Hide rather than SetActive(false), which turns it off once it has faded. Show cancels a
    // fade out. While fading out it takes no gaze or taps (its CanvasGroup stops blocking raycasts).
    // And its buttons don't click until it has been up k_ClickGuardSeconds: a panel appears where the
    // user is looking, and a pinch already under way (or a flicker of one) landed on Scan Room 0.13 s
    // after the settings panel opened. It still stops the gaze meanwhile, so nothing behind is clicked.
    // M3.Canvas adds one to every Material canvas; any other panel root can have one added.
    [DisallowMultipleComponent]
    public class M3Fade : MonoBehaviour
    {
        public const float k_InSeconds = 0.18f;
        public const float k_OutSeconds = 0.12f;
        // The panel grows from this much of its size as it fades in.
        const float k_StartScale = 0.96f;
        public const float k_ClickGuardSeconds = 0.35f;

        CanvasGroup m_Group;
        Vector3 m_BaseScale = Vector3.one;
        float m_Value;
        bool m_Hiding;
        float m_ShownAt;

        void Awake()
        {
            m_Group = GetComponent<CanvasGroup>();
            if (m_Group == null)
                m_Group = gameObject.AddComponent<CanvasGroup>();
        }

        // From nothing, each time it's turned on. Its size is taken now, after whatever set it (such
        // as M3Canvas's dp scale) has run.
        void OnEnable()
        {
            if (m_Hiding)
                return;
            m_BaseScale = transform.localScale;
            m_Value = 0f;
            m_ShownAt = Time.unscaledTime;
            Apply();
        }

        void OnDisable()
        {
            // Left at full size, so the next fade in starts from the right one.
            if (m_Group != null)
                transform.localScale = m_BaseScale;
            m_Hiding = false;
        }

        void Update()
        {
            var clickable = !m_Hiding && Time.unscaledTime - m_ShownAt >= k_ClickGuardSeconds;
            if (m_Group.interactable != clickable)
                m_Group.interactable = clickable;
            var target = m_Hiding ? 0f : 1f;
            if (m_Value == target)
            {
                if (m_Hiding)
                {
                    m_Hiding = false;
                    transform.localScale = m_BaseScale;
                    gameObject.SetActive(false);
                }
                return;
            }
            m_Value = Mathf.MoveTowards(m_Value, target, Time.unscaledDeltaTime / (m_Hiding ? k_OutSeconds : k_InSeconds));
            Apply();
        }

        void Apply()
        {
            // Decelerating: quick at first, settling into place.
            var eased = 1f - (1f - m_Value) * (1f - m_Value);
            m_Group.alpha = eased;
            m_Group.blocksRaycasts = !m_Hiding;
            transform.localScale = m_BaseScale * Mathf.Lerp(k_StartScale, 1f, eased);
        }

        // Shows an object, fading it in if it can; cancels a fade out.
        public static void Show(GameObject target)
        {
            if (target.TryGetComponent<M3Fade>(out var fade) && fade.m_Hiding)
            {
                fade.m_Hiding = false;
                fade.Apply();
            }
            if (!target.activeSelf)
                target.SetActive(true);
        }

        // Hides an object, fading it out first if it can and it's showing.
        public static void Hide(GameObject target)
        {
            if (!target.activeSelf)
                return;
            if (!target.activeInHierarchy || !target.TryGetComponent<M3Fade>(out var fade) || !fade.enabled)
            {
                target.SetActive(false);
                return;
            }
            fade.m_Hiding = true;
            fade.Apply();
        }

        public static void SetShown(GameObject target, bool shown)
        {
            if (shown)
                Show(target);
            else
                Hide(target);
        }

        // Whether it's showing and staying: on, and not fading out.
        public static bool IsShown(GameObject target) =>
            target.activeSelf && (!target.TryGetComponent<M3Fade>(out var fade) || !fade.m_Hiding);
    }
}
