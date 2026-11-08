using UnityEngine;
using UnityEngine.EventSystems;

namespace CardboardXR.Interaction
{
    // Lightens a renderer while the gaze is on it and shifts its hue when it's clicked. Useful as a
    // gaze target in test scenes. Needs a collider. Something that can be grabbed also turns cyan
    // while a hand could take it (GrabReady), which wins over the gaze's lightening.
    [RequireComponent(typeof(Renderer))]
    public class GazeHighlight : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        // URP's main color property.
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        // Cool against the placed cubes' warm orange, so it reads at a glance over a camera image.
        static readonly Color k_GrabReady = new(0.3f, 0.9f, 1f);
        const float k_HoverLighten = 0.45f;
        const float k_HueStep = 0.17f;

        Renderer m_Renderer;
        MaterialPropertyBlock m_Block;
        Color m_Color;
        bool m_Hovered;
        bool m_GrabReady;

        // Whether a hand could grab this now, so a pinch would take it rather than click.
        public bool GrabReady
        {
            get => m_GrabReady;
            set
            {
                if (m_GrabReady == value)
                    return;
                m_GrabReady = value;
                Apply();
            }
        }

        void Awake()
        {
            m_Renderer = GetComponent<Renderer>();
            m_Block = new MaterialPropertyBlock();
            m_Color = m_Renderer.sharedMaterial.color;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            m_Hovered = true;
            Apply();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            m_Hovered = false;
            Apply();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            Color.RGBToHSV(m_Color, out var hue, out var saturation, out var value);
            m_Color = Color.HSVToRGB((hue + k_HueStep) % 1f, Mathf.Max(saturation, 0.7f), Mathf.Max(value, 0.8f));
            Apply();
        }

        void Apply()
        {
            m_Block.SetColor(k_BaseColor, m_GrabReady ? k_GrabReady
                : m_Hovered ? Color.Lerp(m_Color, Color.white, k_HoverLighten) : m_Color);
            m_Renderer.SetPropertyBlock(m_Block);
        }
    }
}
