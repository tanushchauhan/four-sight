using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // Material's state layer: a wash of the content color over an interactive element, 8% while
    // hovered and 10% while pressed, eased in and out. Gaze is how this app hovers, so it's what says
    // "this can be clicked". Works with the gaze pointer's events as with any pointer's, and stays off
    // while the element can't be used. Goes on the element, with its overlay image as a child.
    public class M3StateLayer : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        // Most of the way to a new opacity in this long, in seconds.
        const float k_EaseSeconds = 0.06f;

        [SerializeField] Image m_Layer;
        [SerializeField] Selectable m_Selectable;
        [SerializeField] Color m_Color = Color.white;

        bool m_Hovered;
        bool m_Pressed;
        float m_Opacity;

        public void Initialize(Image layer, Selectable selectable, Color color)
        {
            m_Layer = layer;
            m_Selectable = selectable;
            SetColor(color);
        }

        public void SetColor(Color color)
        {
            m_Color = color;
            Draw();
        }

        public void OnPointerEnter(PointerEventData eventData) => m_Hovered = true;

        public void OnPointerExit(PointerEventData eventData)
        {
            m_Hovered = false;
            m_Pressed = false;
        }

        public void OnPointerDown(PointerEventData eventData) => m_Pressed = true;

        public void OnPointerUp(PointerEventData eventData) => m_Pressed = false;

        void OnDisable()
        {
            m_Hovered = m_Pressed = false;
            m_Opacity = 0f;
            Draw();
        }

        // The opacity wanted now, by state.
        public static float Target(bool hovered, bool pressed, bool interactable) =>
            !interactable ? 0f : pressed ? M3Theme.k_PressedOpacity : hovered ? M3Theme.k_HoverOpacity : 0f;

        void Update()
        {
            var interactable = m_Selectable == null || m_Selectable.IsInteractable();
            var target = Target(m_Hovered, m_Pressed, interactable);
            if (Mathf.Approximately(m_Opacity, target))
                return;
            m_Opacity = Mathf.Lerp(m_Opacity, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 3f / k_EaseSeconds));
            if (Mathf.Abs(m_Opacity - target) < 0.002f)
                m_Opacity = target;
            Draw();
        }

        void Draw()
        {
            if (m_Layer != null)
                m_Layer.color = M3Theme.WithAlpha(m_Color, m_Opacity);
        }
    }
}
