using UnityEngine;

namespace CardboardXR.Stereo
{
    // The Cardboard alignment marker: a thin line up from the bottom center of the screen, between
    // the two eye images. Lining it up with the middle of the viewer centers the phone between the
    // lenses. The size follows the Cardboard SDK's Unity plugin (Widget.cs), 2 dp wide and 24 mm
    // tall, and the color its Android sample.
    public static class AlignmentMarker
    {
        public const float DefaultHeightMeters = 0.024f;
        public const float DefaultWidthDp = 2f;
        public static readonly Color DefaultColor = new Color32(0xb4, 0xb4, 0xb4, 0xff);

        const float k_MetersPerInch = 0.0254f;
        // Android's dp: 1/160 inch.
        const float k_DpPerInch = 160f;

        // A quad in normalized device coordinates, as the distortion shader expects, with its edges on
        // whole pixels so the line stays sharp.
        public static Mesh CreateMesh(Vector2Int screenPixels, Vector2 screenMeters,
            float heightMeters = DefaultHeightMeters, float widthDp = DefaultWidthDp)
        {
            var dpiX = screenPixels.x / (screenMeters.x / k_MetersPerInch);
            var pixelsPerMeterY = screenPixels.y / screenMeters.y;
            var widthPixels = Mathf.Max(1, (int)(widthDp * dpiX / k_DpPerInch));
            var heightPixels = Mathf.Clamp((int)(heightMeters * pixelsPerMeterY), 1, screenPixels.y);

            var left = (screenPixels.x - widthPixels) / 2;
            var leftNdc = left * 2f / screenPixels.x - 1f;
            var rightNdc = (left + widthPixels) * 2f / screenPixels.x - 1f;
            var topNdc = heightPixels * 2f / screenPixels.y - 1f;

            var mesh = new Mesh { name = "Alignment Marker" };
            mesh.SetVertices(new[]
            {
                new Vector3(leftNdc, -1f), new Vector3(rightNdc, -1f),
                new Vector3(leftNdc, topNdc), new Vector3(rightNdc, topNdc),
            });
            mesh.SetUVs(0, new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one });
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            return mesh;
        }
    }
}
