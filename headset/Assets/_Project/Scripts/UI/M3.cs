using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // Builds Material 3 components, styled after Material Design for XR, on uGUI canvases. Sizes and
    // positions are in canvas units, which on world canvases made by Canvas are dp (see M3Canvas);
    // positions are of each element's middle, from its parent's middle, as the rest of the app's UI
    // is laid out. Works at runtime and in the editor, so UI saved in a scene is built the same way.
    // Making a canvas work with the gaze pointer (a GazeCanvas) is up to the caller, since that lives
    // in the interaction assembly.
    public static class M3
    {
        // Buttons are as tall as XR's recommended target.
        public const float k_ButtonHeight = M3Theme.k_Target;
        // A two-line list row, as Material's list items.
        public const float k_ListItemHeight = 64f;
        const float k_ButtonPadding = 24f;
        const float k_IconSize = 20f;
        const float k_IconGap = 8f;

        // A world-space canvas whose units are dp at the distance, with an optional rounded surface.
        public static RectTransform Canvas(string name, Transform parent, Vector2 size, float distance,
            Color? surface = null, float radius = M3Theme.k_ShapePanel)
        {
            var canvasObject = new GameObject(name, typeof(RectTransform));
            canvasObject.transform.SetParent(parent, false);
            canvasObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvasObject.transform;
            rect.sizeDelta = size;
            canvasObject.AddComponent<M3Canvas>().SetDistance(distance);
            // Fades in when shown and, hidden with M3Fade.Hide, out.
            canvasObject.AddComponent<M3Fade>();
            if (surface.HasValue)
            {
                var image = canvasObject.AddComponent<Image>();
                image.color = surface.Value;
                M3Shapes.Apply(image, radius);
            }
            return rect;
        }

        // An empty area to group elements, filling its parent unless given a size.
        public static RectTransform Group(string name, RectTransform parent, Vector2? position = null, Vector2? size = null)
        {
            var rect = NewRect(name, parent);
            if (size.HasValue)
            {
                rect.anchoredPosition = position ?? Vector2.zero;
                rect.sizeDelta = size.Value;
            }
            else
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.sizeDelta = Vector2.zero;
            }
            return rect;
        }

        // A rounded rectangle of a color: a card's or a panel's surface, a divider, an indicator.
        public static Image Surface(string name, RectTransform parent, Vector2 position, Vector2 size, Color color, float radius,
            bool raycastTarget = false)
        {
            var rect = NewRect(name, parent);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycastTarget;
            if (radius > 0f)
                M3Shapes.Apply(image, radius);
            return image;
        }

        public static TextMeshProUGUI Text(string name, RectTransform parent, string content, M3Theme.Type type, Vector2 position,
            Vector2 size, Color? color = null, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            var rect = NewRect(name, parent);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            var (fontSize, medium) = M3Theme.Style(type);
            var assets = M3Assets.Instance;
            if (assets != null)
                text.font = medium && assets.Medium != null ? assets.Medium : assets.Regular;
            text.fontSize = fontSize;
            text.color = color ?? M3Theme.OnSurface;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            text.text = content;
            return text;
        }

        // A Material icon by name, size in dp.
        public static TextMeshProUGUI Icon(string name, RectTransform parent, string icon, float size, Vector2 position, Color color)
        {
            var rect = NewRect(name, parent);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(size, size);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            var assets = M3Assets.Instance;
            if (assets != null)
                text.font = assets.Icons;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.text = M3Icons.Glyph(icon);
            return text;
        }

        // How wide a button is for its label and icon, in dp.
        public static float ButtonWidth(string label, string icon)
        {
            var assets = M3Assets.Instance;
            var labelWidth = label.Length * 8.2f;
            if (assets != null && assets.Medium != null)
            {
                var (size, _) = M3Theme.Style(M3Theme.Type.LabelLarge);
                labelWidth = PreferredWidth(assets.Medium, label, size);
            }
            return Mathf.Ceil(k_ButtonPadding * 2f + labelWidth + (string.IsNullOrEmpty(icon) ? 0f : k_IconSize + k_IconGap));
        }

        // A common button: a pill k_ButtonHeight tall, as wide as its label needs unless given a width.
        public static M3Button Button(string name, RectTransform parent, string label, M3Button.Variant variant, Vector2 position,
            UnityAction onClick, string icon = null, float width = 0f)
        {
            if (width <= 0f)
                width = ButtonWidth(label, icon);
            var size = new Vector2(width, k_ButtonHeight);
            var (root, button, container, outline) = ButtonBase(name, parent, position, size, M3Theme.k_ShapeFull,
                variant == M3Button.Variant.Outlined);
            var contentWidth = string.IsNullOrEmpty(icon) ? 0f : k_IconSize + k_IconGap;
            TMP_Text iconText = null;
            if (!string.IsNullOrEmpty(icon))
            {
                var labelWidth = width - k_ButtonPadding * 2f - contentWidth;
                iconText = Icon("Icon", root, icon, k_IconSize, new Vector2(-(labelWidth + k_IconGap) / 2f, 0f), M3Theme.OnSurface);
            }
            var labelText = Text("Label", root, label, M3Theme.Type.LabelLarge, new Vector2(contentWidth / 2f, 0f),
                new Vector2(width - k_ButtonPadding * 2f - contentWidth + 4f, k_ButtonHeight));
            return Finish(root, variant, button, container, outline, labelText, iconText, onClick, Color.white);
        }

        // A round button holding only an icon, k_ButtonHeight across.
        public static M3Button IconButton(string name, RectTransform parent, string icon, M3Button.Variant variant, Vector2 position,
            UnityAction onClick, float size = M3Theme.k_Target)
        {
            var (root, button, container, outline) = ButtonBase(name, parent, position, new Vector2(size, size), M3Theme.k_ShapeFull,
                variant == M3Button.Variant.Outlined);
            var iconText = Icon("Icon", root, icon, size * 0.45f, Vector2.zero, M3Theme.OnSurface);
            return Finish(root, variant, button, container, outline, null, iconText, onClick, Color.white);
        }

        // A navigation-bar item for an orbiter toolbar: an icon in a pill indicator, over a label.
        public static M3Button ToolbarItem(string name, RectTransform parent, string icon, string label, Vector2 position, float width,
            UnityAction onClick)
        {
            const float height = 76f;
            var root = NewRect(name, parent);
            root.anchoredPosition = position;
            root.sizeDelta = new Vector2(width, height);
            // The whole item is the target, though only the indicator shows.
            var hit = root.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            var button = root.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.transition = Selectable.Transition.None;
            var indicator = Surface("Indicator", root, new Vector2(0f, 14f), new Vector2(64f, 36f), Color.clear, M3Theme.k_ShapeFull);
            var stateImage = Surface("State Layer", indicator.rectTransform, Vector2.zero, new Vector2(64f, 36f), Color.clear,
                M3Theme.k_ShapeFull);
            var iconText = Icon("Icon", root, icon, 24f, new Vector2(0f, 14f), M3Theme.OnSurfaceVariant);
            var labelText = Text("Label", root, label, M3Theme.Type.LabelLarge, new Vector2(0f, -22f), new Vector2(width, 24f));
            labelText.textWrappingMode = TextWrappingModes.NoWrap;
            labelText.overflowMode = TextOverflowModes.Overflow;
            var state = root.gameObject.AddComponent<M3StateLayer>();
            state.Initialize(stateImage, button, M3Theme.OnSurfaceVariant);
            var m3 = root.gameObject.AddComponent<M3Button>();
            m3.Initialize(M3Button.Variant.Toolbar, button, indicator, null, labelText, iconText, state, Color.white);
            if (onClick != null)
                AddListener(button.onClick, onClick);
            return m3;
        }

        // A button group for one choice: a row of filled segments, segmentWidth dp each with a small gap,
        // the chosen one in the primary color. onPick gets the index; the caller sets Selected. Positioned by the row's middle.
        public static M3Button[] Segments(string name, RectTransform parent, string[] labels, Vector2 position, float segmentWidth,
            System.Action<int> onPick)
        {
            const float gap = 4f;
            var row = Group(name, parent, position, new Vector2(labels.Length * (segmentWidth + gap) - gap, k_ButtonHeight));
            var buttons = new M3Button[labels.Length];
            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var x = -row.sizeDelta.x / 2f + segmentWidth / 2f + i * (segmentWidth + gap);
                var size = new Vector2(segmentWidth, k_ButtonHeight);
                var (root, button, container, outline) = ButtonBase(labels[i], row, new Vector2(x, 0f), size, M3Theme.k_ShapeFull, false);
                var label = Text("Label", root, labels[i], M3Theme.Type.LabelLarge, Vector2.zero, size);
                buttons[i] = Finish(root, M3Button.Variant.Segment, button, container, outline, label, null,
                    onPick != null ? () => onPick(index) : null, Color.white);
            }
            return buttons;
        }

        // A list row k_ListItemHeight tall: a leading icon, a headline and a supporting line under it.
        // The headline is the button's Text; the supporting line is returned to set separately.
        public static (M3Button item, TMP_Text supporting) ListItem(string name, RectTransform parent, string icon, Vector2 position,
            float width, UnityAction onClick)
        {
            const float padding = 16f;
            const float iconSize = 24f;
            var size = new Vector2(width, k_ListItemHeight);
            var (root, button, container, outline) = ButtonBase(name, parent, position, size, M3Theme.k_ShapeLarge, false);
            var iconText = Icon("Icon", root, icon, iconSize, new Vector2(-width / 2f + padding + iconSize / 2f, 0f), M3Theme.OnSurfaceVariant);
            var textLeft = padding + iconSize + padding;
            var textWidth = width - textLeft - padding;
            var textX = -width / 2f + textLeft + textWidth / 2f;
            var headline = Text("Headline", root, "", M3Theme.Type.BodyLarge, new Vector2(textX, 11f), new Vector2(textWidth, 24f),
                alignment: TextAlignmentOptions.Left);
            headline.textWrappingMode = TextWrappingModes.NoWrap;
            var supporting = Text("Supporting", root, "", M3Theme.Type.BodyMedium, new Vector2(textX, -12f), new Vector2(textWidth, 20f),
                M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            supporting.textWrappingMode = TextWrappingModes.NoWrap;
            var m3 = Finish(root, M3Button.Variant.ListItem, button, container, outline, headline, iconText, onClick, Color.white,
                M3Theme.k_ShapeLarge);
            return (m3, supporting);
        }

        // A circle of a color to pick, ringed and checked when chosen.
        public static M3Button Swatch(string name, RectTransform parent, Color color, Vector2 position, float diameter, UnityAction onClick)
        {
            var (root, button, container, outline) = ButtonBase(name, parent, position, new Vector2(diameter, diameter),
                M3Theme.k_ShapeFull, true, ring: 4f);
            var check = Icon("Check", root, "check", diameter * 0.5f, Vector2.zero, Color.clear);
            return Finish(root, M3Button.Variant.Swatch, button, container, outline, null, check, onClick, color);
        }

        // A slider as wide as given, its target k_ButtonHeight tall around a track.
        public static M3Slider Slider(string name, RectTransform parent, Vector2 position, float width)
        {
            const float trackHeight = 12f;
            const float handle = 24f;
            var root = NewRect(name, parent);
            root.anchoredPosition = position;
            root.sizeDelta = new Vector2(width, k_ButtonHeight);
            var hit = root.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            var track = Surface("Track", root, Vector2.zero, new Vector2(width, trackHeight), M3Theme.SecondaryContainer, M3Theme.k_ShapeFull);
            var active = Surface("Active", track.rectTransform, Vector2.zero, new Vector2(trackHeight, trackHeight), M3Theme.Primary,
                M3Theme.k_ShapeFull);
            var activeRect = active.rectTransform;
            activeRect.anchorMin = activeRect.anchorMax = new Vector2(0f, 0.5f);
            activeRect.pivot = new Vector2(0f, 0.5f);
            activeRect.anchoredPosition = Vector2.zero;
            var knob = Surface("Handle", track.rectTransform, Vector2.zero, new Vector2(handle, handle), M3Theme.Primary, M3Theme.k_ShapeFull);
            var slider = root.gameObject.AddComponent<M3Slider>();
            slider.Initialize(track.rectTransform, activeRect, knob.rectTransform, handle);
            return slider;
        }

        static (RectTransform root, Button button, Image container, Image outline) ButtonBase(string name, RectTransform parent,
            Vector2 position, Vector2 size, float radius, bool withOutline, float ring = 1f)
        {
            var root = NewRect(name, parent);
            root.anchoredPosition = position;
            root.sizeDelta = size;
            Image outline = null;
            if (withOutline)
                outline = Surface("Outline", root, Vector2.zero, size, Color.clear, radius);
            // The container takes the gaze's ray: the root's own image would be on top of nothing.
            var container = Surface("Container", root, Vector2.zero, withOutline ? size - Vector2.one * (2f * ring) : size, Color.clear,
                radius, raycastTarget: true);
            var button = root.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = container;
            return (root, button, container, outline);
        }

        static M3Button Finish(RectTransform root, M3Button.Variant variant, Button button, Image container, Image outline, TMP_Text label,
            TMP_Text icon, UnityAction onClick, Color swatch, float radius = M3Theme.k_ShapeFull)
        {
            var stateImage = Surface("State Layer", root, Vector2.zero, root.sizeDelta, Color.clear, radius);
            // Over the container, under the content.
            stateImage.transform.SetSiblingIndex(container.transform.GetSiblingIndex() + 1);
            var state = root.gameObject.AddComponent<M3StateLayer>();
            state.Initialize(stateImage, button, Color.white);
            var m3 = root.gameObject.AddComponent<M3Button>();
            m3.Initialize(variant, button, container, outline, label, icon, state, swatch);
            if (onClick != null)
                AddListener(button.onClick, onClick);
            return m3;
        }

        // A listener that's saved with the scene when built in the editor, as a plain one isn't.
        static void AddListener(UnityEvent onClick, UnityAction action)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && action.Target is Object)
            {
                UnityEditor.Events.UnityEventTools.AddPersistentListener(onClick, action);
                return;
            }
#endif
            onClick.AddListener(action);
        }

        static float PreferredWidth(TMP_FontAsset font, string text, float size)
        {
            // Font units to dp: the font's em is size dp.
            var width = 0f;
            var faceInfo = font.faceInfo;
            var scale = size / faceInfo.pointSize * faceInfo.scale;
            foreach (var c in text)
            {
                if (font.characterLookupTable.TryGetValue(c, out var character) ||
                    font.TryAddCharacters(c.ToString()) && font.characterLookupTable.TryGetValue(c, out character))
                    width += character.glyph.metrics.horizontalAdvance * scale;
                else
                    width += size * 0.55f;
            }
            return width;
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }
    }
}
