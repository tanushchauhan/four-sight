using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // A Material 3 button: pill-shaped, colored by its variant, with a state layer for hover and press
    // and Material's disabled look. A toggle shows "selected". The variants:
    //   Filled, Tonal, Outlined, Text: Material's common buttons, label and optional leading icon.
    //   Toolbar: a navigation-bar item for an orbiter toolbar, an icon in a pill indicator over a
    //     label; the indicator fills when selected.
    //   Swatch: a circle of a chosen color, ringed when selected.
    //   Segment: one choice of a button group (Material 3 Expressive's connected buttons), filled
    //     with the highest surface container, and with the primary color when it's the one chosen.
    //   (A swatch shows a check when selected, dark or light for contrast with its color.)
    //   ListItem: a row of a list, clear until hovered, the secondary container when selected.
    // Built by M3.Button and friends. Clicks go through the Button, which the gaze pointer presses.
    public class M3Button : MonoBehaviour
    {
        public enum Variant
        {
            Filled,
            Tonal,
            Outlined,
            Text,
            Toolbar,
            Swatch,
            Segment,
            ListItem,
        }

        [SerializeField] Variant m_Variant;
        [SerializeField] bool m_Selected;
        [SerializeField] Button m_Button;
        [Tooltip("The shape behind the content: the whole button, or a toolbar item's indicator.")]
        [SerializeField] Image m_Container;
        [Tooltip("Drawn behind the container, a dp larger: an outlined button's line, a swatch's ring.")]
        [SerializeField] Image m_Outline;
        [SerializeField] TMP_Text m_Label;
        [SerializeField] TMP_Text m_Icon;
        [SerializeField] M3StateLayer m_StateLayer;
        [SerializeField] Color m_SwatchColor = Color.white;

        bool m_Applied;
        bool m_WasInteractable;

        public Button Button => m_Button;
        public TMP_Text Label => m_Label;

        public bool Selected
        {
            get => m_Selected;
            set
            {
                m_Selected = value;
                Apply();
            }
        }

        public bool Interactable
        {
            get => m_Button.interactable;
            set
            {
                m_Button.interactable = value;
                Apply();
            }
        }

        public string Text
        {
            get => m_Label != null ? m_Label.text : "";
            set
            {
                if (m_Label != null)
                    m_Label.text = value;
            }
        }

        // An icon by name (see M3Icons).
        public string Icon
        {
            set
            {
                if (m_Icon != null)
                    m_Icon.text = M3Icons.Glyph(value);
            }
        }

        public void Initialize(Variant variant, Button button, Image container, Image outline, TMP_Text label, TMP_Text icon,
            M3StateLayer stateLayer, Color swatchColor)
        {
            m_Variant = variant;
            m_Button = button;
            m_Container = container;
            m_Outline = outline;
            m_Label = label;
            m_Icon = icon;
            m_StateLayer = stateLayer;
            m_SwatchColor = swatchColor;
            Apply();
        }

        void OnEnable() => Apply();

        void Update()
        {
            if (m_Button != null && m_Button.interactable != m_WasInteractable)
                Apply();
        }

        // The colors for the variant and state: container, outline (clear for none), and content.
        public static (Color container, Color outline, Color content) Colors(Variant variant, bool selected, bool interactable,
            Color swatch)
        {
            if (!interactable && variant != Variant.Swatch)
            {
                var content = M3Theme.WithAlpha(M3Theme.OnSurface, M3Theme.k_DisabledContent);
                var container = M3Theme.WithAlpha(M3Theme.OnSurface, M3Theme.k_DisabledContainer);
                return variant switch
                {
                    Variant.Filled or Variant.Tonal => (container, Color.clear, content),
                    Variant.Outlined => (Color.clear, container, content),
                    Variant.Segment => (container, Color.clear, content),
                    _ => (Color.clear, Color.clear, content),
                };
            }
            return variant switch
            {
                Variant.Filled => (M3Theme.Primary, Color.clear, M3Theme.OnPrimary),
                // A selected tonal toggle turns filled.
                Variant.Tonal => selected
                    ? (M3Theme.Primary, Color.clear, M3Theme.OnPrimary)
                    : (M3Theme.SecondaryContainer, Color.clear, M3Theme.OnSecondaryContainer),
                Variant.Outlined => selected
                    ? (M3Theme.InverseSurface, Color.clear, M3Theme.InverseOnSurface)
                    : (Color.clear, M3Theme.Outline, M3Theme.Primary),
                Variant.Text => (Color.clear, Color.clear, M3Theme.Primary),
                Variant.Toolbar => selected
                    ? (M3Theme.SecondaryContainer, Color.clear, M3Theme.OnSecondaryContainer)
                    : (Color.clear, Color.clear, M3Theme.OnSurfaceVariant),
                Variant.Segment => selected
                    ? (M3Theme.Primary, Color.clear, M3Theme.OnPrimary)
                    : (M3Theme.SurfaceContainerHighest, Color.clear, M3Theme.OnSurfaceVariant),
                Variant.ListItem => selected
                    ? (M3Theme.SecondaryContainer, Color.clear, M3Theme.OnSecondaryContainer)
                    : (Color.clear, Color.clear, M3Theme.OnSurface),
                _ => (swatch, selected ? M3Theme.Primary : Color.clear, M3Theme.OnSurface),
            };
        }

        public void Apply()
        {
            if (m_Button == null)
                return;
            m_WasInteractable = m_Button.interactable;
            var (container, outline, content) = Colors(m_Variant, m_Selected, m_WasInteractable, m_SwatchColor);
            if (m_Container != null)
                m_Container.color = container;
            if (m_Outline != null)
                m_Outline.color = outline;
            if (m_Icon != null)
                m_Icon.color = m_Variant != Variant.Swatch ? content
                    : !m_Selected ? Color.clear
                    : M3Theme.Contrast(m_SwatchColor, Color.black) > M3Theme.Contrast(m_SwatchColor, Color.white) ? M3Theme.Surface : Color.white;
            if (m_Label != null)
                // A toolbar item's label sits under its indicator, on the toolbar itself.
                m_Label.color = m_Variant == Variant.Toolbar && m_WasInteractable
                    ? (m_Selected ? M3Theme.OnSurface : M3Theme.OnSurfaceVariant)
                    : content;
            if (m_StateLayer != null)
                m_StateLayer.SetColor(m_Variant == Variant.Swatch ? M3Theme.OnSurface : content);
            m_Applied = true;
        }

        void OnValidate()
        {
            if (m_Applied)
                Apply();
        }
    }
}
