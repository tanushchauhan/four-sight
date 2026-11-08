using UnityEngine;

namespace CardboardXR.Hands
{
    // How the camera image fits the square detector input: scaled to fit, centered, and padded.
    public struct Letterbox
    {
        public int inputSize;
        // Input pixels per image pixel.
        public float scale;
        // Where the image's top-left corner sits in the input, in input pixels.
        public Vector2 offset;

        public static Letterbox Fit(int imageWidth, int imageHeight, int inputSize)
        {
            var scale = (float)inputSize / Mathf.Max(imageWidth, imageHeight);
            return new Letterbox
            {
                inputSize = inputSize,
                scale = scale,
                offset = new Vector2((inputSize - imageWidth * scale) / 2f, (inputSize - imageHeight * scale) / 2f),
            };
        }

        // From the input's normalized coordinates to image pixels.
        public Vector2 ToImage(Vector2 normalized) => (normalized * inputSize - offset) / scale;

        public Vector2 ToImageSize(Vector2 normalizedSize) => normalizedSize * inputSize / scale;
    }

    // A square, turned region of the camera image holding a hand, in image pixels with y down. Crop
    // coordinates run 0 to 1 across and down the region as the landmarker sees it, turned so the
    // fingers point up: image = center + Rotate((crop - 0.5) * size, rotation). The rotation is in
    // radians, clockwise in the image: π/2 means the fingers point right.
    public struct HandRegion
    {
        // The wrist, the thumb's base joints and the other fingers' two lowest joints, which
        // MediaPipe bounds to track the hand.
        static readonly int[] k_TrackingLandmarks = { 0, 1, 2, 3, 5, 6, 9, 10, 13, 14, 17, 18 };

        public Vector2 center;
        public float size;
        public float rotation;

        public Vector2 CropToImage(Vector2 crop) => center + Rotate((crop - new Vector2(0.5f, 0.5f)) * size, rotation);

        public Vector2 ImageToCrop(Vector2 image) => Rotate(image - center, -rotation) / size + new Vector2(0.5f, 0.5f);

        public static Vector2 Rotate(Vector2 v, float angle)
        {
            var cos = Mathf.Cos(angle);
            var sin = Mathf.Sin(angle);
            return new Vector2(cos * v.x - sin * v.y, sin * v.x + cos * v.y);
        }

        // The rotation that turns the wrist-to-knuckle direction to point up, as MediaPipe
        // computes it.
        public static float RotationFrom(Vector2 wrist, Vector2 knuckle)
        {
            var angle = Mathf.PI / 2f - Mathf.Atan2(-(knuckle.y - wrist.y), knuckle.x - wrist.x);
            return angle - 2f * Mathf.PI * Mathf.Floor((angle + Mathf.PI) / (2f * Mathf.PI));
        }

        // The landmarker's region for a palm the detector found: the palm box turned upright, moved
        // half its height toward the fingers and grown 2.6 times to take in the whole hand.
        public static HandRegion FromPalm(Palm palm, Letterbox letterbox)
        {
            var rotation = RotationFrom(letterbox.ToImage(palm.wrist), letterbox.ToImage(palm.middleKnuckle));
            return Transform(letterbox.ToImage(palm.center), letterbox.ToImageSize(palm.size), rotation, -0.5f, 2.6f);
        }

        // The next image's region from this image's landmarks (in image pixels), as MediaPipe tracks
        // a hand without the detector: the bounds of the palm and finger bases, turned upright,
        // moved a tenth toward the fingers and doubled.
        public static HandRegion FromLandmarks(Vector2[] landmarks)
        {
            var rotation = RotationFrom(landmarks[0], landmarks[9]);
            Vector2 min = landmarks[k_TrackingLandmarks[0]], max = min;
            foreach (var id in k_TrackingLandmarks)
            {
                min = Vector2.Min(min, landmarks[id]);
                max = Vector2.Max(max, landmarks[id]);
            }
            var axisAlignedCenter = (min + max) / 2f;

            Vector2 uprightMin = Vector2.positiveInfinity, uprightMax = Vector2.negativeInfinity;
            foreach (var id in k_TrackingLandmarks)
            {
                var upright = Rotate(landmarks[id] - axisAlignedCenter, -rotation);
                uprightMin = Vector2.Min(uprightMin, upright);
                uprightMax = Vector2.Max(uprightMax, upright);
            }
            var center = axisAlignedCenter + Rotate((uprightMin + uprightMax) / 2f, rotation);
            return Transform(center, uprightMax - uprightMin, rotation, -0.1f, 2f);
        }

        // MediaPipe's RectTransformation: moves the region along its own up axis by shiftY of its
        // height, then makes it a square on its longer side, scaled.
        static HandRegion Transform(Vector2 center, Vector2 size, float rotation, float shiftY, float scale) => new()
        {
            center = center + Rotate(new Vector2(0f, size.y * shiftY), rotation),
            size = Mathf.Max(size.x, size.y) * scale,
            rotation = rotation,
        };
    }
}
