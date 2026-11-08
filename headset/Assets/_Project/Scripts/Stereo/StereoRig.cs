using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Stereo
{
    // Renders the scene once per eye and warps both images onto the screen through the viewer's lens
    // distortion, like the Cardboard SDK. Put it on the head transform that head tracking moves. The
    // eye cameras, their textures and the compositor are created at runtime.
    // Mono draws the scene once instead, straight to the whole screen with an ordinary perspective and
    // no distortion, for using the phone without a viewer: head tracking, passthrough, the hands and
    // the gaze reticle all work the same, only the presentation changes. The "mono" launch option (1)
    // starts in it, and SetMono switches at runtime.
    public class StereoRig : MonoBehaviour
    {
        // Only the compositor camera draws this layer, and the eye cameras never see it.
        public const int CompositorLayer = 31;
        // Seen by only one eye, for stereo images such as 3D video, where each eye has its own
        // picture. The mono view shows the left eye's.
        public const int LeftEyeOnlyLayer = 27;
        public const int RightEyeOnlyLayer = 28;

        [SerializeField] ViewerParams m_Viewer = ViewerParams.CardboardV1;
        [Tooltip("Physical landscape screen size in meters. Zero uses the size the device reports.")]
        [SerializeField] Vector2 m_ScreenSizeOverride;
        [SerializeField] Shader m_DistortionShader;
        [SerializeField] float m_NearClip = 0.05f;
        [SerializeField] float m_FarClip = 1000f;
        [Tooltip("Eye texture resolution relative to half the screen each. The \"renderScale\" launch " +
                 "option overrides it, as a percentage: 70 means 0.7. The visor default is 75: cardboard " +
                 "lenses do not resolve a full-resolution eye texture.")]
        [SerializeField, Range(0.25f, 2f)] float m_RenderScale = 0.75f;
        [Tooltip("Anti-aliasing samples per eye texture: 1, 2, 4 or 8. The \"msaa\" launch option overrides it.")]
        [SerializeField, Range(1, 8)] int m_Msaa = 1;
        [SerializeField] Color m_BackgroundColor = new(0.1f, 0.1f, 0.12f);
        [Tooltip("Android runs the display at the nearest refresh rate it supports, up to this. " +
                 "The \"fps\" launch option overrides it. 60 keeps head motion smooth through the lenses " +
                 "without redrawing a camera frame at 120 Hz.")]
        [SerializeField] int m_TargetFrameRate = 60;
        [Tooltip("Keep the CPU at most one frame ahead of the GPU: lower latency, a little less throughput.")]
        [SerializeField] bool m_LowLatency = true;
        [Tooltip("Cardboard's alignment marker: a line up from the bottom center that lines up with the middle of the viewer.")]
        [SerializeField] bool m_AlignmentMarker = true;
        [Tooltip("Draw one full-screen view without the viewer's lens distortion. The \"mono\" launch option sets it.")]
        [SerializeField] bool m_Mono;
        [Tooltip("The vertical field of view in mono, in degrees. The viewer's own is far wider, which " +
                 "only makes sense seen through its lenses.")]
        [SerializeField, Range(30f, 120f)] float m_MonoFieldOfView = 60f;

        readonly Camera[] m_EyeCameras = new Camera[2];
        readonly Vector3[] m_EyeOffsets = new Vector3[2];
        bool m_MonoEyes;
        readonly RenderTexture[] m_EyeTextures = new RenderTexture[2];
        readonly Mesh[] m_Meshes = new Mesh[2];
        readonly Material[] m_Materials = new Material[2];
        Mesh m_MarkerMesh;
        Material m_MarkerMaterial;
        Texture2D m_MarkerTexture;
        GameObject m_Compositor;
        LensDistortion m_Lens;

        // Whether the scene is drawn once to the whole screen, instead of once per eye through the
        // viewer's lenses.
        public bool IsMono => m_Mono;

        // Puts both eyes at one point, so nothing the eye cameras draw has any disparity. Passthrough
        // turns it on: its picture is one flat image shown to both eyes at a single depth, and virtual
        // objects drawn with real depth fight it, which is seen as doubling. With the eyes together the
        // whole view agrees with itself, as a flat window. It costs the stereo depth of virtual
        // objects, so VR leaves it off. Each eye keeps its own lens frustum, which its distortion
        // expects; only the separation goes.
        public bool MonoEyes
        {
            get => m_MonoEyes;
            set
            {
                if (m_MonoEyes == value)
                    return;
                m_MonoEyes = value;
                PlaceEyes();
            }
        }

        public ViewerParams Viewer
        {
            get => m_Viewer;
            set
            {
                m_Viewer = value;
                if (isActiveAndEnabled)
                    Rebuild();
            }
        }

        void OnEnable()
        {
            var frameRate = LaunchOptions.GetInt("fps", m_TargetFrameRate);
            Application.targetFrameRate = frameRate;
            // Both cost fill rate and bandwidth, which is heat: they're launch options so a run can be
            // compared against another without a rebuild, while the phone is unplugged.
            var renderScale = LaunchOptions.GetInt("renderScale", 0);
            if (renderScale > 0)
                m_RenderScale = Mathf.Clamp(renderScale / 100f, 0.25f, 2f);
            var samples = LaunchOptions.GetInt("msaa", m_Msaa);
            // A render texture takes 1, 2, 4 or 8 samples and throws on anything else.
            m_Msaa = samples >= 8 ? 8 : samples >= 4 ? 4 : samples >= 2 ? 2 : 1;
            m_Mono = LaunchOptions.GetInt("mono", m_Mono ? 1 : 0) != 0;
            // The frame-rate target doesn't switch the panel's refresh rate by itself.
            DisplayRefreshRate.Request(frameRate);
            // Measured on an iPhone 15 Pro: allowing a second queued frame changed nothing, so keep the
            // lowest-latency setting on both platforms. (What held it at 60 fps was Unity's
            // CADisplayLink, each frame waiting for the last to reach the screen; the Metal display
            // link setting in ProjectSetup fixed that.)
            if (m_LowLatency)
                QualitySettings.maxQueuedFrames = 1;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            // Viewers hold the phone landscape-left. With auto-rotation allowed, Unity switched
            // Screen.orientation to Portrait when the phone tilted in a viewer.
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeRight = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.orientation = ScreenOrientation.LandscapeLeft;
            Debug.Log($"[Stereo] Target {frameRate} fps, max queued frames {QualitySettings.maxQueuedFrames}, " +
                      $"render scale {m_RenderScale:F2}, MSAA {m_Msaa}x, {(m_Mono ? "mono" : "stereo")}");
            Rebuild();
        }

        void OnDisable() => Teardown();

        // Switches between the two eyes through the viewer's lenses and one view on the whole screen.
        public void ToggleMono() => SetMono(!m_Mono);

        public void SetMono(bool mono)
        {
            if (mono == m_Mono)
                return;
            m_Mono = mono;
            Debug.Log($"[Stereo] {(mono ? "Mono: one view on the whole screen, no viewer needed" : "Stereo: one view per eye through the viewer")}");
            if (isActiveAndEnabled)
                Rebuild();
        }

        void Rebuild()
        {
            Teardown();

            string source;
            Vector2 screenSize;
            if (m_ScreenSizeOverride.x > 0f && m_ScreenSizeOverride.y > 0f)
            {
                screenSize = m_ScreenSizeOverride;
                source = "override";
            }
            else
            {
                screenSize = ScreenMetrics.GetScreenSizeMeters(out source);
            }

            m_Lens = new LensDistortion(m_Viewer, screenSize.x, screenSize.y);
            var fov = m_Lens.GetFieldOfView(Eye.Left) * Mathf.Rad2Deg;
            Debug.Log($"[Stereo] {m_Viewer.model}: screen {screenSize.x:F4} x {screenSize.y:F4} m ({source}), " +
                      $"left eye fov L{fov.x:F1} R{fov.y:F1} B{fov.z:F1} T{fov.w:F1}");
            // In full, for Tools/record_eye.py, which undoes the lens distortion in screen recordings.
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Debug.Log("[Stereo] Viewer lens: " + string.Format(invariant,
                "screenToLens {0:F4} interLens {1:F4} trayToLens {2:F4} alignment {3} fov {4} k {5}",
                m_Viewer.screenToLensDistance, m_Viewer.interLensDistance, m_Viewer.trayToLensDistance, (int)m_Viewer.verticalAlignment,
                string.Join(",", System.Array.ConvertAll(m_Viewer.leftEyeFieldOfViewAngles, a => a.ToString("F2", invariant))),
                string.Join(",", System.Array.ConvertAll(m_Viewer.distortionCoefficients, c => c.ToString("F5", invariant)))));

            if (m_Mono)
            {
                // One camera on the whole screen: no eye textures to warp, so no compositor, no
                // distortion meshes and no alignment marker, and the scene is drawn once instead of
                // twice. It sits midway between where the eyes would be.
                CreateMonoEye();
                return;
            }

            CreateEye(Eye.Left);
            CreateEye(Eye.Right);
            PlaceEyes();
            CreateCompositor(screenSize);
        }

        // The mono view: an ordinary perspective camera drawing straight to the screen, kept in
        // m_EyeCameras[0] so Teardown clears it like any other eye.
        void CreateMonoEye()
        {
            var eyeObject = new GameObject("Mono Eye");
            eyeObject.transform.SetParent(transform, false);
            eyeObject.transform.localPosition = (m_Lens.GetEyeOffset(Eye.Left) + m_Lens.GetEyeOffset(Eye.Right)) / 2f;

            var eyeCamera = eyeObject.AddComponent<Camera>();
            eyeCamera.stereoTargetEye = StereoTargetEyeMask.None;
            eyeCamera.clearFlags = CameraClearFlags.SolidColor;
            eyeCamera.backgroundColor = m_BackgroundColor;
            eyeCamera.nearClipPlane = m_NearClip;
            eyeCamera.farClipPlane = m_FarClip;
            eyeCamera.cullingMask &= ~(1 << CompositorLayer | 1 << RightEyeOnlyLayer);
            eyeCamera.fieldOfView = m_MonoFieldOfView;
            eyeCamera.depth = 0;
            m_EyeCameras[0] = eyeCamera;
        }

        void CreateEye(Eye eye)
        {
            var index = (int)eye;
            var width = Mathf.Max(16, Mathf.RoundToInt(Screen.width * 0.5f * m_RenderScale));
            var height = Mathf.Max(16, Mathf.RoundToInt(Screen.height * m_RenderScale));
            m_EyeTextures[index] = new RenderTexture(width, height, 24)
            {
                name = $"{eye} Eye",
                antiAliasing = m_Msaa,
            };

            var eyeObject = new GameObject($"{eye} Eye");
            eyeObject.transform.SetParent(transform, false);
            m_EyeOffsets[index] = m_Lens.GetEyeOffset(eye);
            eyeObject.transform.localPosition = m_EyeOffsets[index];

            var eyeCamera = eyeObject.AddComponent<Camera>();
            eyeCamera.stereoTargetEye = StereoTargetEyeMask.None;
            eyeCamera.clearFlags = CameraClearFlags.SolidColor;
            eyeCamera.backgroundColor = m_BackgroundColor;
            eyeCamera.nearClipPlane = m_NearClip;
            eyeCamera.farClipPlane = m_FarClip;
            eyeCamera.cullingMask &= ~(1 << CompositorLayer | 1 << (eye == Eye.Left ? RightEyeOnlyLayer : LeftEyeOnlyLayer));
            eyeCamera.depth = -1;
            eyeCamera.targetTexture = m_EyeTextures[index];
            // Asymmetric frustum from the lens field of view; set after the clip planes.
            eyeCamera.projectionMatrix = m_Lens.GetProjectionMatrix(eye, m_NearClip, m_FarClip);
            m_EyeCameras[index] = eyeCamera;
        }

        // Each eye at its lens offset, or both at the point between them while MonoEyes.
        void PlaceEyes()
        {
            var together = (m_EyeOffsets[0] + m_EyeOffsets[1]) / 2f;
            for (var i = 0; i < 2; i++)
            {
                if (m_EyeCameras[i] != null)
                    m_EyeCameras[i].transform.localPosition = m_MonoEyes ? together : m_EyeOffsets[i];
            }
        }

        void CreateCompositor(Vector2 screenSize)
        {
            m_Compositor = new GameObject("Distortion Compositor") { layer = CompositorLayer };
            var compositorCamera = m_Compositor.AddComponent<Camera>();
            compositorCamera.stereoTargetEye = StereoTargetEyeMask.None;
            compositorCamera.clearFlags = CameraClearFlags.SolidColor;
            compositorCamera.backgroundColor = Color.black;
            compositorCamera.cullingMask = 1 << CompositorLayer;
            compositorCamera.orthographic = true;
            compositorCamera.nearClipPlane = 0.01f;
            compositorCamera.farClipPlane = 10f;
            compositorCamera.allowHDR = false;
            compositorCamera.allowMSAA = false;
            compositorCamera.depth = 0;

            for (var i = 0; i < 2; i++)
            {
                var eye = (Eye)i;
                m_Meshes[i] = m_Lens.CreateDistortionMesh(eye);
                m_Materials[i] = new Material(m_DistortionShader)
                {
                    name = $"{eye} Distortion",
                    mainTexture = m_EyeTextures[i],
                };
                AddCompositorMesh($"{eye} Distortion Mesh", m_Meshes[i], m_Materials[i]);
            }

            if (m_AlignmentMarker)
                CreateAlignmentMarker(screenSize);
        }

        // A solid line through the same shader, drawn after the eye images so it covers their edges.
        void CreateAlignmentMarker(Vector2 screenSize)
        {
            m_MarkerMesh = AlignmentMarker.CreateMesh(new Vector2Int(Screen.width, Screen.height), screenSize);
            m_MarkerTexture = new Texture2D(1, 1) { name = "Alignment Marker" };
            m_MarkerTexture.SetPixel(0, 0, AlignmentMarker.DefaultColor);
            m_MarkerTexture.Apply();
            m_MarkerMaterial = new Material(m_DistortionShader)
            {
                name = "Alignment Marker",
                mainTexture = m_MarkerTexture,
                renderQueue = (int)RenderQueue.Overlay + 1,
            };
            AddCompositorMesh("Alignment Marker", m_MarkerMesh, m_MarkerMaterial);
        }

        void AddCompositorMesh(string name, Mesh mesh, Material material)
        {
            // The shader places vertices on the screen itself, so the transform is irrelevant;
            // huge bounds keep the renderer from being culled.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            var meshObject = new GameObject(name) { layer = CompositorLayer };
            meshObject.transform.SetParent(m_Compositor.transform, false);
            meshObject.transform.localPosition = Vector3.forward;
            meshObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = meshObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        void Teardown()
        {
            for (var i = 0; i < 2; i++)
            {
                if (m_EyeCameras[i] != null)
                    Destroy(m_EyeCameras[i].gameObject);
                if (m_EyeTextures[i] != null)
                {
                    m_EyeTextures[i].Release();
                    Destroy(m_EyeTextures[i]);
                }
                if (m_Meshes[i] != null)
                    Destroy(m_Meshes[i]);
                if (m_Materials[i] != null)
                    Destroy(m_Materials[i]);
                m_EyeCameras[i] = null;
                m_EyeTextures[i] = null;
                m_Meshes[i] = null;
                m_Materials[i] = null;
            }

            if (m_MarkerMesh != null)
                Destroy(m_MarkerMesh);
            if (m_MarkerMaterial != null)
                Destroy(m_MarkerMaterial);
            if (m_MarkerTexture != null)
                Destroy(m_MarkerTexture);
            m_MarkerMesh = null;
            m_MarkerMaterial = null;
            m_MarkerTexture = null;

            if (m_Compositor != null)
                Destroy(m_Compositor);
            m_Compositor = null;
        }
    }
}
