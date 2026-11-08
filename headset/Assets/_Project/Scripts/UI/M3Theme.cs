using UnityEngine;

namespace CardboardXR.UI
{
    // Material 3's design tokens as the app uses them, following Material Design for XR
    // (developer.android.com/design/ui/xr): the baseline dark color scheme, the type scale, shapes,
    // and how big a dp is in the world.
    //   Android XR sizes UI in dp, at 0.868 dmm each; a dmm is a millimeter seen from a meter away, so
    //   1 dp at distance d is 0.868 d mm. A phone in a Cardboard viewer has about half a headset's
    //   pixels per degree, so everything is CardboardScale times bigger than that: 14 dp text, XR's
    //   smallest, would otherwise be about 9 pixels tall per eye. Proportions are untouched.
    // World-space canvases here use one canvas unit per dp (see M3Canvas), so sizes in code are dp.
    public static class M3Theme
    {
        public const float k_DpToDmm = 0.868f;
        public const float k_DefaultCardboardScale = 1.6f;

        // Set at startup from the "uiScale" launch option (percent); see UiScaleOption.
        public static float CardboardScale { get; set; } = k_DefaultCardboardScale;

        // Where Android XR puts a panel: 1.75 m away, its middle 5 degrees below eye level.
        public const float k_PanelDistance = 1.75f;
        public const float k_PanelPitch = -5f;
        // Nearer than that, for the app's own overlay (the settings panel), so it's always in front of
        // a scene's panels rather than cutting through them. Sizes are angular, so it looks the same.
        public const float k_OverlayDistance = 1.25f;

        public static float DpToMeters(float dp, float distance) => dp * k_DpToDmm * 1e-3f * distance * CardboardScale;

        // Baseline dark scheme.
        public static readonly Color Primary = Hex(0xD0BCFF);
        public static readonly Color OnPrimary = Hex(0x381E72);
        public static readonly Color PrimaryContainer = Hex(0x4F378B);
        public static readonly Color OnPrimaryContainer = Hex(0xEADDFF);
        public static readonly Color SecondaryContainer = Hex(0x4A4458);
        public static readonly Color OnSecondaryContainer = Hex(0xE8DEF8);
        public static readonly Color Surface = Hex(0x141218);
        public static readonly Color SurfaceContainer = Hex(0x211F26);
        public static readonly Color SurfaceContainerHigh = Hex(0x2B2930);
        public static readonly Color SurfaceContainerHighest = Hex(0x36343B);
        public static readonly Color OnSurface = Hex(0xE6E0E9);
        public static readonly Color OnSurfaceVariant = Hex(0xCAC4D0);
        public static readonly Color Outline = Hex(0x938F99);
        public static readonly Color OutlineVariant = Hex(0x49454F);
        public static readonly Color Error = Hex(0xF2B8B5);
        public static readonly Color InverseSurface = Hex(0xE6E0E9);
        public static readonly Color InverseOnSurface = Hex(0x322F35);

        // State layers: the content color at these opacities over the container. Twice Material's 8% and
        // 10%: through a phone in a viewer, at about half a headset's pixels per degree, those were
        // too faint to tell hovered from not.
        public const float k_HoverOpacity = 0.16f;
        public const float k_PressedOpacity = 0.2f;
        // Disabled: content at 38%, container at 12% of on-surface.
        public const float k_DisabledContent = 0.38f;
        public const float k_DisabledContainer = 0.12f;

        // Corner radii, in dp. Full rounds a shape's shorter side.
        public const float k_ShapeFull = 1000f;
        public const float k_ShapePanel = 32f;
        public const float k_ShapeDialog = 28f;
        public const float k_ShapeLarge = 16f;
        public const float k_ShapeCard = 12f;
        public const float k_ShapeSmall = 8f;

        // Spatial elevation, in dp toward the viewer.
        public const float k_ElevationOrbiter = 16f;
        public const float k_ElevationPopup = 32f;
        public const float k_ElevationDialog = 56f;

        // Targets: 56 dp, 8 dp apart.
        public const float k_Target = 56f;
        public const float k_TargetGap = 8f;

        public enum Type
        {
            HeadlineSmall,
            TitleLarge,
            TitleMedium,
            BodyLarge,
            BodyMedium,
            LabelLarge,
        }

        // Size in dp, and whether it's set in the medium weight. Nothing under 14 dp, XR's minimum.
        public static (float size, bool medium) Style(Type type) => type switch
        {
            Type.HeadlineSmall => (24f, false),
            Type.TitleLarge => (22f, false),
            Type.TitleMedium => (16f, true),
            Type.BodyLarge => (16f, false),
            Type.BodyMedium => (14f, false),
            _ => (14f, true),
        };

        // WCAG contrast ratio between two colors, for checking pairs of roles.
        public static float Contrast(Color a, Color b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Mathf.Max(la, lb) + 0.05f) / (Mathf.Min(la, lb) + 0.05f);
        }

        static float Luminance(Color c)
        {
            static float Channel(float v) => v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
        }

        public static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

        static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}
