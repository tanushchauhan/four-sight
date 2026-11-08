using UnityEngine;

namespace CardboardXR.Hands
{
    // Places the landmarker's 21 landmarks in 3D in front of the camera. The model gives each
    // landmark in image pixels, and separately the hand's 3D shape in meters around its center. The
    // shape is moved to where its palm projects best onto the palm's pixels, which gives the hand's
    // distance however it's turned, even edge-on to the camera. Each landmark then sits on its own
    // pixel's ray at the depth the shape gives it, so the joints line up with the camera image.
    public static class HandGeometry
    {
        public const int JointCount = 21;
        public const int ThumbTip = 4;
        public const int IndexTip = 8;
        // The wrist and the four finger knuckles, which hold still relative to each other.
        public static readonly int[] Palm = { 0, 5, 9, 13, 17 };
        public static readonly int[] All =
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        // Where the shape's origin is in the camera image's frame (x right and y down across the
        // image, z forward, in meters) for the chosen landmarks to project best onto their pixels, by
        // linear least squares. rmsError is how far those landmarks still miss, in pixels.
        public static Vector3 FitPosition(Vector2[] imageLandmarks, Vector3[] metricLandmarks, int[] ids,
            Vector2 focalLength, Vector2 principalPoint, out float rmsError)
        {
            // A landmark m seen at normalized image position (a, b) gives two equations linear in the
            // position t: tx - a tz = a mz - mx, and ty - b tz = b mz - my.
            double sumA = 0, sumB = 0, sumSquares = 0, sumRx = 0, sumRy = 0, sumARx = 0, sumBRy = 0;
            foreach (var i in ids)
            {
                var m = metricLandmarks[i];
                double a = (imageLandmarks[i].x - principalPoint.x) / focalLength.x;
                double b = (imageLandmarks[i].y - principalPoint.y) / focalLength.y;
                var rx = a * m.z - m.x;
                var ry = b * m.z - m.y;
                sumA += a;
                sumB += b;
                sumSquares += a * a + b * b;
                sumRx += rx;
                sumRy += ry;
                sumARx += a * rx;
                sumBRy += b * ry;
            }

            // How spread out the landmarks are in the image; zero if they're all on one ray.
            var n = ids.Length;
            var spread = sumSquares - (sumA * sumA + sumB * sumB) / n;
            if (spread < 1e-12)
            {
                rmsError = float.PositiveInfinity;
                return Vector3.zero;
            }
            var tz = ((sumA * sumRx + sumB * sumRy) / n - sumARx - sumBRy) / spread;
            var tx = (sumRx + sumA * tz) / n;
            var ty = (sumRy + sumB * tz) / n;
            var position = new Vector3((float)tx, (float)ty, (float)tz);

            var squaredError = 0.0;
            foreach (var i in ids)
            {
                var q = metricLandmarks[i] + position;
                if (q.z <= 0f)
                {
                    rmsError = float.PositiveInfinity;
                    return position;
                }
                var projected = Vector2.Scale(new Vector2(q.x, q.y) / q.z, focalLength) + principalPoint;
                squaredError += (projected - imageLandmarks[i]).sqrMagnitude;
            }
            rmsError = (float)System.Math.Sqrt(squaredError / n);
            return position;
        }

        // Each landmark in the camera image's frame, in meters, on its pixel's ray at the shape's
        // depth for it, with the shape's origin at centerDepth.
        public static void PlaceJoints(Vector2[] imageLandmarks, Vector3[] metricLandmarks, float centerDepth,
            Vector2 focalLength, Vector2 principalPoint, Vector3[] joints)
        {
            for (var i = 0; i < JointCount; i++)
            {
                var z = centerDepth + metricLandmarks[i].z;
                var p = imageLandmarks[i];
                joints[i] = new Vector3((p.x - principalPoint.x) / focalLength.x * z, (p.y - principalPoint.y) / focalLength.y * z, z);
            }
        }

        // The palm size that would have placed the palm at measuredDepth instead of estimatedDepth.
        // With the hand's image fixed, the depth a fit finds scales with the size it assumes.
        public static float PalmSizeForDepth(float palmSize, float estimatedDepth, float measuredDepth) =>
            palmSize * measuredDepth / estimatedDepth;

        // How far ahead of the camera the palm is, from placed joints.
        public static float PalmDepth(Vector3[] joints)
        {
            var depth = 0f;
            foreach (var id in Palm)
                depth += joints[id].z;
            return depth / Palm.Length;
        }

        // The palm's size in 3D, the average distance between its landmarks, which doesn't change as
        // the hand turns.
        public static float PalmSize(Vector3[] shape)
        {
            var sum = 0f;
            var pairs = 0;
            for (var a = 0; a < Palm.Length; a++)
            {
                for (var b = a + 1; b < Palm.Length; b++)
                {
                    sum += Vector3.Distance(shape[Palm[a]], shape[Palm[b]]);
                    pairs++;
                }
            }
            return sum / pairs;
        }

        // The palm's average width between its landmarks in the image, in pixels.
        public static float PalmSpan(Vector2[] imageLandmarks)
        {
            var sum = 0f;
            var pairs = 0;
            for (var a = 0; a < Palm.Length; a++)
            {
                for (var b = a + 1; b < Palm.Length; b++)
                {
                    sum += Vector2.Distance(imageLandmarks[Palm[a]], imageLandmarks[Palm[b]]);
                    pairs++;
                }
            }
            return sum / pairs;
        }

        // Which screen direction (x right, y up) each camera image axis (x right, y down) points,
        // from ARCore's display matrix, which maps screen UV to texture UV as
        // tex = u * (m00, m01) + v * (m10, m11) + (m20, m21). The CPU image and the camera texture
        // share axes, with texture v running down the image's rows.
        public static void ImageAxesOnScreen(Matrix4x4 display, out Vector2 imageX, out Vector2 imageY)
        {
            // The inverse of the matrix whose columns are (m00, m01) and (m10, m11).
            var determinant = display.m00 * display.m11 - display.m10 * display.m01;
            imageX = RoundToAxis(new Vector2(display.m11, -display.m01) / determinant);
            imageY = RoundToAxis(new Vector2(-display.m10, display.m00) / determinant);
        }

        // The eight ways the image axes can lie on the screen, numbered from 1, for the "handAxes"
        // launch option.
        public static void ImageAxesVariant(int variant, out Vector2 imageX, out Vector2 imageY)
        {
            var turns = (variant - 1) / 2 % 4;
            var flip = (variant - 1) % 2 == 1;
            imageX = HandRegion.Rotate(Vector2.right, turns * Mathf.PI / 2f);
            imageY = HandRegion.Rotate(flip ? Vector2.up : Vector2.down, turns * Mathf.PI / 2f);
            imageX = RoundToAxis(imageX);
            imageY = RoundToAxis(imageY);
        }

        static Vector2 RoundToAxis(Vector2 v) =>
            Mathf.Abs(v.x) >= Mathf.Abs(v.y) ? new Vector2(Mathf.Sign(v.x), 0f) : new Vector2(0f, Mathf.Sign(v.y));
    }
}
