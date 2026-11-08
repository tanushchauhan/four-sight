using CardboardXR.UI;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class MaterialUiTests
    {
        [Test]
        public void DpFollowsAndroidXrTimesTheCardboardScale()
        {
            var scale = M3Theme.CardboardScale;
            try
            {
                M3Theme.CardboardScale = 1f;
                // Android XR: 1024 dp at 1.75 m is perceived as 1556 mm.
                Assert.AreEqual(1.556f, M3Theme.DpToMeters(1024f, 1.75f), 0.002f);
                M3Theme.CardboardScale = 1.6f;
                Assert.AreEqual(1.556f * 1.6f, M3Theme.DpToMeters(1024f, 1.75f), 0.004f);
                // Angular: twice as far, twice as big.
                Assert.AreEqual(2f * M3Theme.DpToMeters(56f, 1f), M3Theme.DpToMeters(56f, 2f), 1e-6f);
            }
            finally
            {
                M3Theme.CardboardScale = scale;
            }
        }

        [Test]
        public void TextColorsAreLegibleOnTheirSurfaces()
        {
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnSurface, M3Theme.SurfaceContainerHigh), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnSurfaceVariant, M3Theme.SurfaceContainerHigh), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnPrimary, M3Theme.Primary), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnSecondaryContainer, M3Theme.SecondaryContainer), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.InverseOnSurface, M3Theme.InverseSurface), 4.5f);
        }

        [Test]
        public void NoTextIsSmallerThanXrsMinimum()
        {
            foreach (M3Theme.Type type in System.Enum.GetValues(typeof(M3Theme.Type)))
                Assert.GreaterOrEqual(M3Theme.Style(type).size, 14f, type.ToString());
        }

        [Test]
        public void CornersAreDrawnAtTheirRadius()
        {
            // The sprite's corner is k_Radius pixels; at the multiplier it spans the radius in units.
            Assert.AreEqual(M3Shapes.k_Radius, M3Shapes.PixelsPerUnitMultiplier(32f) * 32f, 1e-4f);
            var sprite = M3Shapes.CreateRound();
            try
            {
                Assert.AreEqual(new Vector4(M3Shapes.k_Radius, M3Shapes.k_Radius, M3Shapes.k_Radius, M3Shapes.k_Radius), sprite.border);
                var texture = sprite.texture;
                Assert.AreEqual(0f, texture.GetPixel(0, 0).a, 0.01f, "outside the circle");
                Assert.AreEqual(1f, texture.GetPixel(M3Shapes.k_Size / 2, M3Shapes.k_Size / 2).a, 0.01f, "the middle");
            }
            finally
            {
                Object.DestroyImmediate(sprite.texture);
                Object.DestroyImmediate(sprite);
            }
        }

        [Test]
        public void StateLayerFollowsHoverAndPress()
        {
            Assert.AreEqual(0f, M3StateLayer.Target(false, false, true));
            Assert.AreEqual(M3Theme.k_HoverOpacity, M3StateLayer.Target(true, false, true));
            Assert.AreEqual(M3Theme.k_PressedOpacity, M3StateLayer.Target(true, true, true));
            Assert.AreEqual(0f, M3StateLayer.Target(true, true, false), "nothing shows on a disabled element");
        }

        [Test]
        public void ButtonsTakeTheirVariantsColors()
        {
            var filled = M3Button.Colors(M3Button.Variant.Filled, false, true, Color.white);
            Assert.AreEqual(M3Theme.Primary, filled.container);
            Assert.AreEqual(M3Theme.OnPrimary, filled.content);
            var tonal = M3Button.Colors(M3Button.Variant.Tonal, false, true, Color.white);
            Assert.AreEqual(M3Theme.SecondaryContainer, tonal.container);
            // A selected toggle reads as on.
            Assert.AreEqual(M3Theme.Primary, M3Button.Colors(M3Button.Variant.Tonal, true, true, Color.white).container);
            var toolbarOn = M3Button.Colors(M3Button.Variant.Toolbar, true, true, Color.white);
            var toolbarOff = M3Button.Colors(M3Button.Variant.Toolbar, false, true, Color.white);
            Assert.AreEqual(M3Theme.SecondaryContainer, toolbarOn.container);
            Assert.AreEqual(0f, toolbarOff.container.a);
            var disabled = M3Button.Colors(M3Button.Variant.Filled, false, false, Color.white);
            Assert.AreEqual(M3Theme.k_DisabledContent, disabled.content.a, 1e-5f);
            Assert.AreEqual(M3Theme.k_DisabledContainer, disabled.container.a, 1e-5f);
            var swatch = M3Button.Colors(M3Button.Variant.Swatch, true, true, Color.red);
            Assert.AreEqual(Color.red, swatch.container);
            Assert.AreEqual(M3Theme.Primary, swatch.outline);
        }

        [Test]
        public void IconsResolveToPrivateUseCharacters()
        {
            foreach (var name in M3Icons.Codepoints.Keys)
            {
                var glyph = M3Icons.Glyph(name);
                Assert.IsNotEmpty(glyph, name);
                Assert.That(char.ConvertToUtf32(glyph, 0), Is.InRange(0xE000, 0xF8FF), name);
            }
            Assert.IsEmpty(M3Icons.Glyph("no_such_icon"));
        }

        [Test]
        public void TheIconFontHasEveryIcon()
        {
            var assets = M3Assets.Instance;
            if (assets == null || assets.Icons == null)
                Assert.Ignore("The Material UI's assets haven't been made yet (they are with the first build)");
            foreach (var codepoint in M3Icons.Codepoints.Values)
                Assert.IsTrue(assets.Icons.characterLookupTable.ContainsKey((uint)codepoint), $"U+{codepoint:X4}");
        }

        [Test]
        public void ToolbarClosesUpAroundHiddenItems()
        {
            // Lobby | Recenter, Scan | 6DoF, Passthrough, Pinch, items 92 wide, dividers 16.
            var groups = new[] { 0, 1, 1, 2, 2, 2 };
            var itemX = new float[6];
            var dividerX = new float[3];
            var width = M3Toolbar.Arrange(new[] { true, true, true, true, true, true }, groups, 3, 92f, 16f, itemX, dividerX);
            Assert.AreEqual(6 * 92f + 2 * 16f, width);
            Assert.AreEqual(46f, itemX[0]);
            Assert.AreEqual(92f + 8f, dividerX[1]);
            Assert.AreEqual(92f + 16f + 46f, itemX[1]);

            // Lobby hidden: its group and the divider after it go, and the rest start at the left.
            width = M3Toolbar.Arrange(new[] { false, true, true, true, true, true }, groups, 3, 92f, 16f, itemX, dividerX);
            Assert.AreEqual(5 * 92f + 16f, width);
            Assert.IsNaN(itemX[0]);
            Assert.IsNaN(dividerX[1]);
            Assert.AreEqual(46f, itemX[1]);
            Assert.AreEqual(2 * 92f + 8f, dividerX[2]);
        }
    }
}
