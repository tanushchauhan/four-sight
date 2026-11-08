using Unity.Collections;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Fills the models' NHWC float inputs (RGB, 0 to 1) from an RGBA32 camera image whose first row
    // is the top of the image, with bilinear filtering. Pixels outside the image are black.
    public static class ImageSampler
    {
        // The whole image, fitted into the square detector input.
        public static void SampleLetterbox(NativeArray<byte> rgba, int width, int height, Letterbox letterbox, float[] output)
        {
            var size = letterbox.inputSize;
            var i = 0;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var image = (new Vector2(x + 0.5f, y + 0.5f) - letterbox.offset) / letterbox.scale;
                    Sample(rgba, width, height, image, output, ref i);
                }
            }
        }

        // The region, upright, into a size x size input.
        public static void SampleRegion(NativeArray<byte> rgba, int width, int height, HandRegion region, int size, float[] output)
        {
            var first = region.CropToImage(new Vector2(0.5f / size, 0.5f / size));
            var stepX = region.CropToImage(new Vector2(1.5f / size, 0.5f / size)) - first;
            var stepY = region.CropToImage(new Vector2(0.5f / size, 1.5f / size)) - first;
            var i = 0;
            for (var y = 0; y < size; y++)
            {
                var rowStart = first + stepY * y;
                for (var x = 0; x < size; x++)
                    Sample(rgba, width, height, rowStart + stepX * x, output, ref i);
            }
        }

        // At a point in image pixels, where pixel centers are at +0.5.
        static void Sample(NativeArray<byte> rgba, int width, int height, Vector2 point, float[] output, ref int i)
        {
            var fx = point.x - 0.5f;
            var fy = point.y - 0.5f;
            var x0 = Mathf.FloorToInt(fx);
            var y0 = Mathf.FloorToInt(fy);
            var tx = fx - x0;
            var ty = fy - y0;
            var color = Vector3.zero;
            Add(rgba, width, height, x0, y0, (1f - tx) * (1f - ty), ref color);
            Add(rgba, width, height, x0 + 1, y0, tx * (1f - ty), ref color);
            Add(rgba, width, height, x0, y0 + 1, (1f - tx) * ty, ref color);
            Add(rgba, width, height, x0 + 1, y0 + 1, tx * ty, ref color);
            output[i++] = color.x / 255f;
            output[i++] = color.y / 255f;
            output[i++] = color.z / 255f;
        }

        static void Add(NativeArray<byte> rgba, int width, int height, int x, int y, float weight, ref Vector3 color)
        {
            if (weight <= 0f || x < 0 || y < 0 || x >= width || y >= height)
                return;
            var p = (y * width + x) * 4;
            color += new Vector3(rgba[p], rgba[p + 1], rgba[p + 2]) * weight;
        }
    }
}
