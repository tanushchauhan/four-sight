using UnityEngine;

namespace CardboardXR.Stereo
{
    public enum Eye
    {
        Left = 0,
        Right = 1,
    }

    // Per-eye field of view, projection, eye offset and distortion mesh for a viewer on a screen of
    // known physical size (landscape, meters).
    // Ported from the Cardboard SDK (sdk/lens_distortion.cc and sdk/distortion_mesh.cc, Apache
    // License 2.0). Screen UVs follow the SDK: 0-1 across the whole landscape screen, origin at the
    // bottom left, left eye on the left half.
    public class LensDistortion
    {
        public const int MeshResolution = 40;

        // Assumed bezel between the tray and the screen's lit area.
        const float k_DefaultBorderSizeMeters = 0.003f;

        struct Viewport
        {
            public float width;
            public float height;
            public float xEyeOffset;
            public float yEyeOffset;
        }

        readonly ViewerParams m_Viewer;
        readonly PolynomialRadialDistortion m_Distortion;
        readonly float m_ScreenWidthMeters;
        readonly float m_ScreenHeightMeters;
        // Radians as (left, right, bottom, top), indexed by Eye.
        readonly Vector4[] m_FieldOfView = new Vector4[2];

        public LensDistortion(ViewerParams viewer, float screenWidthMeters, float screenHeightMeters)
        {
            m_Viewer = viewer;
            m_Distortion = new PolynomialRadialDistortion(viewer.distortionCoefficients);
            m_ScreenWidthMeters = screenWidthMeters;
            m_ScreenHeightMeters = screenHeightMeters;

            var left = CalculateFieldOfView();
            m_FieldOfView[(int)Eye.Left] = left;
            m_FieldOfView[(int)Eye.Right] = new Vector4(left.y, left.x, left.z, left.w);
        }

        public ViewerParams Viewer => m_Viewer;
        public PolynomialRadialDistortion Distortion => m_Distortion;

        // Angles from the eye's optical axis to the edges of its view, in radians:
        // (left, right, bottom, top).
        public Vector4 GetFieldOfView(Eye eye) => m_FieldOfView[(int)eye];

        // Eye position in head space, in meters: half the lens separation to either side.
        public Vector3 GetEyeOffset(Eye eye) =>
            new((eye == Eye.Left ? -0.5f : 0.5f) * m_Viewer.interLensDistance, 0f, 0f);

        public Matrix4x4 GetProjectionMatrix(Eye eye, float near, float far)
        {
            var fov = m_FieldOfView[(int)eye];
            return Matrix4x4.Frustum(
                -Mathf.Tan(fov.x) * near, Mathf.Tan(fov.y) * near,
                -Mathf.Tan(fov.z) * near, Mathf.Tan(fov.w) * near,
                near, far);
        }

        // The eye texture UV seen through the lens at a screen UV (the SDK's
        // DistortedUvForUndistortedUv).
        public Vector2 ScreenUvToTextureUv(Vector2 screenUv, Eye eye)
        {
            CalculateViewports(eye, out var screen, out var texture);
            var tanAngle = new Vector2(
                screenUv.x * screen.width - screen.xEyeOffset,
                screenUv.y * screen.height - screen.yEyeOffset);
            var distorted = m_Distortion.Distort(tanAngle);
            return new Vector2(
                (distorted.x + texture.xEyeOffset) / texture.width,
                (distorted.y + texture.yEyeOffset) / texture.height);
        }

        // The screen UV where an eye texture UV appears (the SDK's UndistortedUvForDistortedUv).
        public Vector2 TextureUvToScreenUv(Vector2 textureUv, Eye eye)
        {
            CalculateViewports(eye, out var screen, out var texture);
            var tanAngle = new Vector2(
                textureUv.x * texture.width - texture.xEyeOffset,
                textureUv.y * texture.height - texture.yEyeOffset);
            var undistorted = m_Distortion.DistortInverse(tanAngle);
            return new Vector2(
                (undistorted.x + screen.xEyeOffset) / screen.width,
                (undistorted.y + screen.yEyeOffset) / screen.height);
        }

        // A grid mesh that warps one eye's texture onto its part of the screen. Vertices are in
        // normalized device coordinates for the whole screen (-1..1, y up) and need no transform;
        // UVs index the eye texture.
        public Mesh CreateDistortionMesh(Eye eye)
        {
            const int n = MeshResolution;
            var vertices = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            for (var row = 0; row < n; row++)
            {
                for (var col = 0; col < n; col++)
                {
                    var textureUv = new Vector2(col / (float)(n - 1), row / (float)(n - 1));
                    var screenUv = TextureUvToScreenUv(textureUv, eye);
                    var index = row * n + col;
                    vertices[index] = new Vector3(2f * screenUv.x - 1f, 2f * screenUv.y - 1f, 0f);
                    uvs[index] = textureUv;
                }
            }

            // Two triangles per grid cell; Unity meshes have no triangle strips.
            var triangles = new int[(n - 1) * (n - 1) * 6];
            var t = 0;
            for (var row = 0; row < n - 1; row++)
            {
                for (var col = 0; col < n - 1; col++)
                {
                    var index = row * n + col;
                    triangles[t++] = index;
                    triangles[t++] = index + n;
                    triangles[t++] = index + 1;
                    triangles[t++] = index + 1;
                    triangles[t++] = index + n;
                    triangles[t++] = index + n + 1;
                }
            }

            var mesh = new Mesh { name = $"Cardboard Distortion ({eye})" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        Vector4 CalculateFieldOfView()
        {
            var deviceFov = m_Viewer.leftEyeFieldOfViewAngles;
            var toLens = m_Viewer.screenToLensDistance;
            var outerDistance = (m_ScreenWidthMeters - m_Viewer.interLensDistance) / 2f;
            var innerDistance = m_Viewer.interLensDistance / 2f;
            var bottomDistance = GetYEyeOffsetMeters();
            var topDistance = m_ScreenHeightMeters - bottomDistance;

            // The lens widens what the screen shows, so the visible angle to each screen edge is
            // the distorted tan-angle; the viewer's own limits clamp it.
            var outerAngle = Mathf.Atan(m_Distortion.Distort(new Vector2(outerDistance / toLens, 0f)).x);
            var innerAngle = Mathf.Atan(m_Distortion.Distort(new Vector2(innerDistance / toLens, 0f)).x);
            var bottomAngle = Mathf.Atan(m_Distortion.Distort(new Vector2(0f, bottomDistance / toLens)).y);
            var topAngle = Mathf.Atan(m_Distortion.Distort(new Vector2(0f, topDistance / toLens)).y);

            return new Vector4(
                Mathf.Min(outerAngle, deviceFov[0] * Mathf.Deg2Rad),
                Mathf.Min(innerAngle, deviceFov[1] * Mathf.Deg2Rad),
                Mathf.Min(bottomAngle, deviceFov[2] * Mathf.Deg2Rad),
                Mathf.Min(topAngle, deviceFov[3] * Mathf.Deg2Rad));
        }

        // Height of the lenses' optical center above the bottom of the screen.
        float GetYEyeOffsetMeters()
        {
            switch (m_Viewer.verticalAlignment)
            {
                case ViewerParams.VerticalAlignment.Bottom:
                    return m_Viewer.trayToLensDistance - k_DefaultBorderSizeMeters;
                case ViewerParams.VerticalAlignment.Top:
                    return m_ScreenHeightMeters - m_Viewer.trayToLensDistance - k_DefaultBorderSizeMeters;
                default:
                    return m_ScreenHeightMeters / 2f;
            }
        }

        // Screen and eye-texture extents in tan-angle units, with the optical center's position.
        void CalculateViewports(Eye eye, out Viewport screen, out Viewport texture)
        {
            var toLens = m_Viewer.screenToLensDistance;
            var eyeCenterMeters = eye == Eye.Left
                ? (m_ScreenWidthMeters - m_Viewer.interLensDistance) / 2f
                : (m_ScreenWidthMeters + m_Viewer.interLensDistance) / 2f;
            screen = new Viewport
            {
                width = m_ScreenWidthMeters / toLens,
                height = m_ScreenHeightMeters / toLens,
                xEyeOffset = eyeCenterMeters / toLens,
                yEyeOffset = GetYEyeOffsetMeters() / toLens,
            };

            var fov = m_FieldOfView[(int)eye];
            texture = new Viewport
            {
                width = Mathf.Tan(fov.x) + Mathf.Tan(fov.y),
                height = Mathf.Tan(fov.z) + Mathf.Tan(fov.w),
                xEyeOffset = Mathf.Tan(fov.x),
                yEyeOffset = Mathf.Tan(fov.z),
            };
        }
    }
}
