using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // Rounded rectangles of any corner radius from one generated sprite: an anti-aliased white circle,
    // nine-sliced at its middle, so the corners are its quarters and the rest stretches. An Image's
    // pixelsPerUnitMultiplier sets how big the corners are drawn (Apply). The editor saves one as an
    // asset (M3Assets.Round), which UI saved in a scene needs; otherwise one is made at runtime.
    public static class M3Shapes
    {
        public const int k_Size = 128;
        public const float k_Radius = k_Size / 2f;

        static Sprite s_Round;

        public static Sprite Round
        {
            get
            {
                if (s_Round != null)
                    return s_Round;
                var assets = M3Assets.Instance;
                if (assets != null && assets.Round != null)
                    return s_Round = assets.Round;
                s_Round = CreateRound();
                s_Round.hideFlags = s_Round.texture.hideFlags = HideFlags.DontSave;
                return s_Round;
            }
        }

        public static Sprite CreateRound()
        {
            var texture = new Texture2D(k_Size, k_Size, TextureFormat.RGBA32, true)
            {
                name = "M3 Round",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            var pixels = new Color32[k_Size * k_Size];
            for (var y = 0; y < k_Size; y++)
            {
                for (var x = 0; x < k_Size; x++)
                {
                    var dx = x + 0.5f - k_Radius;
                    var dy = y + 0.5f - k_Radius;
                    var coverage = Mathf.Clamp01(k_Radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    pixels[y * k_Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(coverage * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, k_Size, k_Size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(k_Radius, k_Radius, k_Radius, k_Radius));
            sprite.name = "M3 Round";
            return sprite;
        }

        // Gives the image rounded corners of this radius in canvas units (dp), never more than half its
        // shorter side, so M3Theme.k_ShapeFull makes a pill or a circle.
        public static void Apply(Image image, float radius)
        {
            var size = image.rectTransform.rect.size;
            if (size.x <= 0f || size.y <= 0f)
            size = image.rectTransform.sizeDelta;
            var clamped = Mathf.Max(0.01f, Mathf.Min(radius, Mathf.Min(size.x, size.y) / 2f));
            image.sprite = Round;
            image.type = Image.Type.Sliced;
            image.fillCenter = true;
            // The canvas's reference pixels per unit (100) and the sprite's (100) cancel, so the corner,
            // k_Radius pixels of the sprite, is drawn k_Radius / multiplier units across.
            image.pixelsPerUnitMultiplier = PixelsPerUnitMultiplier(clamped);
        }

        public static float PixelsPerUnitMultiplier(float radius) => k_Radius / radius;
    }
}
