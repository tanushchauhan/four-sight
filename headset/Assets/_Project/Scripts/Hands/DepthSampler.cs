using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Hands
{
    // Measured depth for the hand, from the AR provider's environment depth where there's a depth
    // sensor: the iPhone Pro's LiDAR gives a 256x192 depth map for each camera image, covering the
    // same view with the same rows first, in meters along the camera's axis. A point in the camera
    // image is at the same fraction of the way across and down the depth map.
    //
    // The depth is copied out as its camera image is taken, since the hand models finish with that
    // image after the provider has moved on.
    public sealed class DepthSampler
    {
        // A depth image and a camera image belong together if their timestamps are this close.
        const long k_MatchNs = 1_000_000;

        float[] m_Depth = Array.Empty<float>();
        byte[] m_Confidence = Array.Empty<byte>();
        float[] m_Scratch = Array.Empty<float>();

        public int Width { get; private set; }
        public int Height { get; private set; }
        // The camera image this depth belongs to; 0 while there is none.
        public long TimestampNs { get; private set; }
        public bool HasConfidence { get; private set; }

        // Why the latest TryTake failed, for the log.
        public int NoImage { get; private set; }
        public int Unmatched { get; private set; }

        public void ClearCounts() => NoImage = Unmatched = 0;

        // Copies the provider's latest depth and confidence, if they belong to the camera image taken
        // at cameraNs.
        public bool TryTake(AROcclusionManager occlusion, long cameraNs)
        {
            TimestampNs = 0;
            if (!occlusion.TryAcquireEnvironmentDepthCpuImage(out var depth))
            {
                NoImage++;
                return false;
            }

            using (depth)
            {
                if (Math.Abs((long)Math.Round(depth.timestamp * 1e9) - cameraNs) > k_MatchNs ||
                    depth.format != XRCpuImage.Format.DepthFloat32)
                {
                    Unmatched++;
                    return false;
                }
                Resize(depth.width, depth.height);
                var plane = depth.GetPlane(0);
                var floats = plane.data.Reinterpret<float>(1);
                var rowFloats = plane.rowStride / sizeof(float);
                for (var y = 0; y < Height; y++)
                    NativeArray<float>.Copy(floats, y * rowFloats, m_Depth, y * Width, Width);
            }

            HasConfidence = TryTakeConfidence(occlusion, cameraNs);
            TimestampNs = cameraNs;
            return true;
        }

        bool TryTakeConfidence(AROcclusionManager occlusion, long cameraNs)
        {
            if (!occlusion.TryAcquireEnvironmentDepthConfidenceCpuImage(out var confidence))
                return false;
            using (confidence)
            {
                if (Math.Abs((long)Math.Round(confidence.timestamp * 1e9) - cameraNs) > k_MatchNs ||
                    confidence.width != Width || confidence.height != Height ||
                    confidence.format != XRCpuImage.Format.OneComponent8)
                    return false;
                var plane = confidence.GetPlane(0);
                for (var y = 0; y < Height; y++)
                    NativeArray<byte>.Copy(plane.data, y * plane.rowStride, m_Confidence, y * Width, Width);
            }
            return true;
        }

        // Loads a depth map directly; for tests.
        public void Set(float[] depth, byte[] confidence, int width, int height, long timestampNs)
        {
            Resize(width, height);
            Array.Copy(depth, m_Depth, width * height);
            HasConfidence = confidence != null;
            if (HasConfidence)
                Array.Copy(confidence, m_Confidence, width * height);
            TimestampNs = timestampNs;
        }

        // The median depth within radius depth pixels of a point, given as a fraction of the way across
        // and down the camera image. Only positive, finite depths count, and where there's a confidence
        // map, only those at least minConfidence (ARKit: 0 low, 1 medium, 2 high). The median keeps
        // pixels past the hand's edge, which see the room behind, from pulling it far.
        public bool TrySample(Vector2 uv, int radius, int minConfidence, out float meters, out int used)
        {
            meters = 0f;
            used = 0;
            if (TimestampNs == 0 || !(uv.x >= 0f && uv.x < 1f && uv.y >= 0f && uv.y < 1f))
                return false;

            var cx = (int)(uv.x * Width);
            var cy = (int)(uv.y * Height);
            var side = 2 * radius + 1;
            if (m_Scratch.Length < side * side)
                m_Scratch = new float[side * side];
            for (var y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
            {
                for (var x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
                {
                    var i = y * Width + x;
                    var d = m_Depth[i];
                    if (!(d > 0f) || float.IsInfinity(d) || HasConfidence && m_Confidence[i] < minConfidence)
                        continue;
                    m_Scratch[used++] = d;
                }
            }
            if (used == 0)
                return false;

            Array.Sort(m_Scratch, 0, used);
            meters = used % 2 == 1
                ? m_Scratch[used / 2]
                : 0.5f * (m_Scratch[used / 2 - 1] + m_Scratch[used / 2]);
            return true;
        }

        void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            if (m_Depth.Length != width * height)
            {
                m_Depth = new float[width * height];
                m_Confidence = new byte[width * height];
            }
        }
    }
}
