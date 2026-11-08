using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Lights virtual objects like the room the camera sees, while passthrough is on: Core's one
    // directional light, and the ambient light, follow ARCore's or ARKit's light estimate, and the
    // light casts soft shadows, which detected surfaces show on the camera image (the
    // CardboardXR/ShadowCatcher pass on the plane prefab, turned up through _ShadowCatcherStrength).
    //   What the estimate gives differs by platform. ARCore's Environmental HDR mode gives the main
    //   light's direction, color and brightness and the ambient light as spherical harmonics, so on
    //   Android shadows fall away from the room's brightest light. ARKit's world tracking only gives
    //   an overall brightness and color temperature, so on iPhone the light keeps k_DefaultPitch
    //   overhead, which is how most rooms are lit, and only its strength and warmth follow the room.
    // The estimate updates with each camera image and jumps a little; the light eases toward it
    // (k_EaseSeconds). Out of passthrough (VR, or 3DoF) the light is a fixed overhead key light with no
    // shadows, and estimation is off, since it costs camera-frame time. Put it on the XR Origin, whose
    // pose is session space, which the estimate's direction is in. Logs [Light].
    public class PassthroughLighting : MonoBehaviour
    {
        const float k_DefaultPitch = 65f;
        const float k_DefaultYaw = -30f;
        const float k_DefaultIntensity = 1f;
        static readonly Color k_DefaultAmbient = new(0.42f, 0.42f, 0.45f);
        // How dark a full shadow makes the camera image, 0-1.
        const float k_ShadowStrength = 0.55f;
        const float k_EaseSeconds = 0.5f;
        const float k_LogSeconds = 5f;
        // Ambient light as a share of the main light's, when the estimate gives no ambient of its own.
        const float k_AmbientShare = 0.45f;
        static readonly int k_ShadowStrengthId = Shader.PropertyToID("_ShadowCatcherStrength");

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] PassthroughController m_Passthrough;
        [SerializeField] ARCameraManager m_CameraManager;
        [SerializeField] Light m_Light;

        bool m_Active;
        float m_NextLog;
        // The latest estimate, which Update eases toward.
        Quaternion m_TargetRotation;
        Color m_TargetColor = Color.white;
        float m_TargetIntensity = k_DefaultIntensity;
        SphericalHarmonicsL2? m_TargetAmbient;
        string m_Source = "none yet";
        // The main light's strength as the estimate gave it, before clamping, for tuning.
        float m_RawStrength;

        // What's asked of the platform: Android's Environmental HDR mode needs direction and
        // harmonics asked for; ARKit's world tracking can only give the ambient terms.
        static LightEstimation Requested =>
            Application.platform == RuntimePlatform.IPhonePlayer
                ? LightEstimation.AmbientIntensity | LightEstimation.AmbientColor
                : LightEstimation.MainLightDirection | LightEstimation.MainLightIntensity | LightEstimation.AmbientSphericalHarmonics |
                  LightEstimation.AmbientIntensity | LightEstimation.AmbientColor;

        void OnEnable()
        {
            m_CameraManager.frameReceived += OnFrame;
            ApplyDefaults();
        }

        void OnDisable()
        {
            m_CameraManager.frameReceived -= OnFrame;
            Shader.SetGlobalFloat(k_ShadowStrengthId, 0f);
        }

        Quaternion DefaultRotation => transform.rotation * Quaternion.Euler(k_DefaultPitch, k_DefaultYaw, 0f);

        void ApplyDefaults()
        {
            m_TargetRotation = DefaultRotation;
            m_TargetColor = Color.white;
            m_TargetIntensity = k_DefaultIntensity;
            m_TargetAmbient = null;
            m_Light.transform.rotation = m_TargetRotation;
            m_Light.color = m_TargetColor;
            m_Light.intensity = m_TargetIntensity;
            m_Light.shadows = LightShadows.None;
            SetFlatAmbient(k_DefaultAmbient);
            Shader.SetGlobalFloat(k_ShadowStrengthId, 0f);
        }

        void Update()
        {
            var active = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof && m_Passthrough.IsOn;
            if (active != m_Active)
            {
                m_Active = active;
                m_CameraManager.requestedLightEstimation = active ? Requested : LightEstimation.None;
                if (active)
                {
                    m_Light.shadows = LightShadows.Soft;
                    Shader.SetGlobalFloat(k_ShadowStrengthId, k_ShadowStrength);
                    m_TargetRotation = DefaultRotation;
                    m_Source = "none yet";
                    m_NextLog = Time.unscaledTime + k_LogSeconds;
                    Debug.Log($"[Light] Following the room's light, asking for {Requested}");
                }
                else
                {
                    ApplyDefaults();
                    Debug.Log("[Light] Back to the fixed light");
                }
            }
            if (!m_Active)
                return;

            var ease = 1f - Mathf.Exp(-Time.unscaledDeltaTime / k_EaseSeconds);
            m_Light.transform.rotation = Quaternion.Slerp(m_Light.transform.rotation, m_TargetRotation, ease);
            m_Light.color = Color.Lerp(m_Light.color, m_TargetColor, ease);
            m_Light.intensity = Mathf.Lerp(m_Light.intensity, m_TargetIntensity, ease);
            if (m_TargetAmbient.HasValue)
                RenderSettings.ambientProbe = m_TargetAmbient.Value;
            else
                SetFlatAmbient(m_Light.color * (m_Light.intensity * k_AmbientShare));

            if (Time.unscaledTime >= m_NextLog)
            {
                m_NextLog = Time.unscaledTime + k_LogSeconds;
                var euler = (Quaternion.Inverse(transform.rotation) * m_Light.transform.rotation).eulerAngles;
                Debug.Log($"[Light] From {m_Source}: intensity {m_Light.intensity:F2} (raw {m_RawStrength:F2}), color {Hex(m_Light.color)}, " +
                          $"pitch {euler.x:F0} yaw {euler.y:F0} in session space, " +
                          $"ambient {(m_TargetAmbient.HasValue ? "harmonics" : "flat")}");
            }
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            if (!m_Active)
                return;
            var estimate = args.lightEstimation;
            var source = "";

            if (estimate.mainLightDirection is { } direction && direction.sqrMagnitude > 1e-6f)
            {
                m_TargetRotation = transform.rotation * Quaternion.LookRotation(direction.normalized);
                source += "direction ";
            }

            // ARCore's Environmental HDR folds the main light's strength into its color, and gives no
            // brightness of its own: the color's brightest channel is it.
            var mainStrength = estimate.mainLightColor is { } hdr ? Mathf.Max(hdr.r, Mathf.Max(hdr.g, hdr.b)) : 0f;
            if (estimate.averageMainLightBrightness is { } main)
            {
                m_TargetIntensity = Mathf.Clamp(main * 2f, 0.3f, 1.6f);
                source += "main brightness ";
            }
            else if (estimate.averageIntensityInLumens is { } lumens)
            {
                // ARKit calls 1000 lumens a neutrally lit room.
                m_TargetIntensity = Mathf.Clamp(lumens / 1000f, 0.3f, 1.6f);
                source += "lumens ";
            }
            else if (mainStrength > 0f)
            {
                m_TargetIntensity = Mathf.Clamp(mainStrength, 0.3f, 1.6f);
                m_RawStrength = mainStrength;
                source += "color strength ";
            }
            else if (estimate.averageBrightness is { } brightness)
            {
                // ARCore's ambient mode: the image's average brightness, about 0.5 in a lit room.
                m_TargetIntensity = Mathf.Clamp(brightness * 2f, 0.3f, 1.6f);
                source += "brightness ";
            }

            if (estimate.mainLightColor is { } color)
            {
                m_TargetColor = Normalized(color);
                source += "color ";
            }
            else if (estimate.averageColorTemperature is { } kelvin)
            {
                m_TargetColor = Mathf.CorrelatedColorTemperatureToRGB(kelvin);
                source += "temperature ";
            }
            else if (estimate.colorCorrection is { } correction)
            {
                m_TargetColor = Normalized(correction);
                source += "color correction ";
            }

            if (estimate.ambientSphericalHarmonics is { } harmonics)
            {
                m_TargetAmbient = harmonics;
                source += "harmonics";
            }

            if (source.Length > 0)
                m_Source = source.Trim();
        }

        // A color with its brightest channel at 1: the estimate's brightness is applied separately.
        static Color Normalized(Color color)
        {
            var max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return max > 1e-4f ? new Color(color.r / max, color.g / max, color.b / max) : Color.white;
        }

        static void SetFlatAmbient(Color color)
        {
            var harmonics = new SphericalHarmonicsL2();
            harmonics.AddAmbientLight(color);
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = harmonics;
        }

        static string Hex(Color color) => ColorUtility.ToHtmlStringRGB(color);
    }
}
