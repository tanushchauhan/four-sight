using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.ARCore;
using UnityEditor.XR.ARKit;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;

namespace ProjectBootstrap
{
    // Applies the CardboardXR player settings for Android and iOS, and gives each the only XR loader
    // it can use: ARCore on Android, ARKit on iOS. Safe to re-run.
    public static class ProjectSetup
    {
        const string ArCoreLoader = "UnityEngine.XR.ARCore.ARCoreLoader";
        const string ArKitLoader = "UnityEngine.XR.ARKit.ARKitLoader";
        const string XrFolder = "Assets/XR";
        const string XrSettingsFolder = "Assets/XR/Settings";

        // Invoke with: -executeMethod ProjectBootstrap.ProjectSetup.Apply
        public static void Apply()
        {
            var android = NamedBuildTarget.Android;

            PlayerSettings.companyName = "brenpoly";
            PlayerSettings.productName = "CardboardXR";
            PlayerSettings.SetApplicationIdentifier(android, "com.brenpoly.cardboardxr");

            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;

            // Vulkan first; the ARCore plugin supports it from 6.1. GLES3 as fallback.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });

            // Cardboard viewers hold the phone landscape-left. Auto-rotation stays off in every other
            // direction: with it allowed, Unity switched Screen.orientation to Portrait when the
            // phone tilted in a viewer.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            // Viewer QR codes often hold short links that have to be followed over the network.
            PlayerSettings.Android.forceInternetPermission = true;

            // Per-thread CPU and GPU frame times for FrameTimingLog's hitch reports.
            PlayerSettings.enableFrameTimingStats = true;

            var ios = NamedBuildTarget.iOS;
            PlayerSettings.SetApplicationIdentifier(ios, "com.brenpoly.cardboardxr");
            PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
            // AR Foundation 6 needs iOS 12; 14 is where the LiDAR depth on the iPhone Pro arrived.
            PlayerSettings.iOS.targetOSVersionString = "14.0";
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneOnly;
            // iOS kills the app the moment ARKit opens the camera unless it can say why.
            PlayerSettings.iOS.cameraUsageDescription =
                "CardboardXR uses the camera to follow your head and hands, and to show the room around you.";
            // Xcode signs with whichever Apple ID is signed into it.
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;

            // Frames driven by CAMetalDisplayLink rather than CADisplayLink. With CADisplayLink, each
            // frame waited for the one before to reach the screen, finished just after a refresh
            // and missed it, so every frame took two: 60 fps on the iPhone 15 Pro's 120 Hz display,
            // with the GPU at 1.5 ms and the main thread idle 13.7 ms of every frame. A display link
            // of the app's own was granted 8.3 ms, so the display ran at 120 Hz; the frames didn't.
            // CAMetalDisplayLink hands out each frame's drawable ahead of time (Unity asks for 2
            // refreshes, 16.7 ms at 120 Hz, as long as a 60 fps frame), so the next frame needn't
            // wait for the last. There's no scripting API for it, only the serialized setting.
            SetPlayerSetting("metalUseMetalDisplayLink", true);

            // What Unity's iOS player drops to by itself when the phone gets hot. Its defaults, 30 fps
            // when serious and 15 when critical, are too low for a headset, where a slow frame rate
            // is sickening. At 120 fps with the hand models running, the phone reached serious in
            // about 70 s and fell to exactly 30. Serious now falls back to the 60 the app had before
            // 120, and critical keeps Unity's ratio of half that.
            SetPlayerSetting("adjustIOSFPSUsingThermalState", true);
            SetPlayerSetting("thermalStateSeriousIOSFPS", 60);
            SetPlayerSetting("thermalStateCriticalIOSFPS", 30);

            // The same stripping on both platforms, set here rather than left to each platform's
            // default. iOS defaults to stripping harder than Android, and it took XR Plug-in
            // Management's startup hooks with it: Unity.XR.Management shipped in the build, but
            // XRGeneralSettings.AttemptInitializeXRSDKOnLoad was gone from RuntimeInitializeOnLoads,
            // so XR never started, ARKit never loaded, and every AR subsystem reported itself
            // missing. Nothing logged an error, because nothing ran.
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Minimal);
            PlayerSettings.SetManagedStrippingLevel(ios, ManagedStrippingLevel.Minimal);

            // The ARKit package gates its own build processor on this define, and that processor is
            // what puts libUnityARKit.a into the Xcode project. The package adds the define from an
            // editor update loop, which never ticks in batch mode: -executeMethod runs and exits
            // before it fires, so a project set up entirely from the command line never gets it.
            // Without it the build succeeds, ships no ARKit provider, and says nothing: the loader
            // fails to initialize, and AR Foundation only reports each missing subsystem in turn.
            EnsureDefine(ios, "UNITY_XR_ARKIT_LOADER_ENABLED");

            if (!ConfigureLoader(BuildTargetGroup.Android, ArCoreLoader, "Android") ||
                !ConfigureLoader(BuildTargetGroup.iOS, ArKitLoader, "iOS") ||
                !ConfigureArCore() || !ConfigureArKit())
            {
                EditorApplication.Exit(1);
                return;
            }

            ConfigureUrpRenderers();

            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectSetup] Android and iOS player and XR settings applied.");
            EditorApplication.Exit(0);
        }

        // Sets a player setting that has no scripting API, as the Project Settings window does.
        static void SetPlayerSetting(string name, bool value) =>
            EditPlayerSetting(name, property => property.boolValue = value);

        static void SetPlayerSetting(string name, int value) =>
            EditPlayerSetting(name, property => property.intValue = value);

        static void EditPlayerSetting(string name, System.Action<SerializedProperty> edit)
        {
            var settings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            var property = settings.FindProperty(name);
            if (property == null)
            {
                Debug.LogError($"[ProjectSetup] No player setting {name}; this Unity version may have renamed it.");
                return;
            }
            edit(property);
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        // Adds a scripting define for one platform, keeping the ones already there.
        static void EnsureDefine(NamedBuildTarget target, string define)
        {
            var defines = PlayerSettings.GetScriptingDefineSymbols(target)
                .Split(';').Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (defines.Contains(define))
                return;

            defines.Add(define);
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
            Debug.Log($"[ProjectSetup] Added the {define} define for {target}.");
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}"))
                AssetDatabase.CreateFolder(parent, name);
        }

        static bool ConfigureLoader(BuildTargetGroup group, string loader, string label)
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey,
                    out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                EnsureFolder("Assets", "XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, $"{XrFolder}/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }

            var settings = perTarget.SettingsForBuildTarget(group);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = $"{label} Settings";
                AssetDatabase.AddObjectToAsset(settings, perTarget);
                perTarget.SetSettingsForBuildTarget(group, settings);
            }

            if (settings.Manager == null)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = $"{label} Providers";
                AssetDatabase.AddObjectToAsset(manager, perTarget);
                settings.Manager = manager;
            }

            // AR Foundation needs XR running from the first frame.
            settings.InitManagerOnStart = true;

            if (!XRPackageMetadataStore.AssignLoader(settings.Manager, loader, group))
            {
                Debug.LogError($"[ProjectSetup] Could not assign {loader} for {label}.");
                return false;
            }

            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(settings.Manager);
            EditorUtility.SetDirty(perTarget);
            return true;
        }

        // URP needs ARBackgroundRendererFeature to draw the camera image, and ARCore on Vulkan needs
        // ARCommandBufferSupportRendererFeature. Mirrors the fix in ARCore's project validation rules.
        static void ConfigureUrpRenderers()
        {
            var pipelines = new HashSet<UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset defaultAsset)
                pipelines.Add(defaultAsset);
            for (var i = 0; i < QualitySettings.count; i++)
            {
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset qualityAsset)
                    pipelines.Add(qualityAsset);
            }

            foreach (var pipeline in pipelines)
            {
                foreach (var rendererData in pipeline.rendererDataList)
                {
                    if (rendererData == null)
                        continue;

                    AddRendererFeature<ARBackgroundRendererFeature>(rendererData);
                    AddRendererFeature<ARCommandBufferSupportRendererFeature>(rendererData);
                }
            }
        }

        static void AddRendererFeature<T>(ScriptableRendererData rendererData) where T : ScriptableRendererFeature
        {
            if (rendererData.rendererFeatures.Any(feature => feature is T))
                return;

            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = typeof(T).Name;
            feature.hideFlags |= HideFlags.HideInHierarchy;

            AssetDatabase.AddObjectToAsset(feature, rendererData);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var serializedRendererData = new SerializedObject(rendererData);
            var featuresProperty = serializedRendererData.FindProperty("m_RendererFeatures");
            featuresProperty.arraySize++;
            featuresProperty.GetArrayElementAtIndex(featuresProperty.arraySize - 1).objectReferenceValue = feature;

            var featureMapProperty = serializedRendererData.FindProperty("m_RendererFeatureMap");
            featureMapProperty.arraySize++;
            featureMapProperty.GetArrayElementAtIndex(featureMapProperty.arraySize - 1).longValue = localId;

            serializedRendererData.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererData);
            Debug.Log($"[ProjectSetup] Added {typeof(T).Name} to {rendererData.name}.");
        }

        static bool ConfigureArCore()
        {
            var arcore = ARCoreSettings.currentSettings;
            if (arcore == null || !AssetDatabase.Contains(arcore))
            {
                EnsureFolder("Assets", "XR");
                EnsureFolder(XrFolder, "Settings");
                arcore = ScriptableObject.CreateInstance<ARCoreSettings>();
                AssetDatabase.CreateAsset(arcore, $"{XrSettingsFolder}/ARCoreSettings.asset");
                ARCoreSettings.currentSettings = arcore;
            }

            arcore.requirement = ARCoreSettings.Requirement.Required;
            // Depth helps place hands in 3D but not every ARCore phone has it.
            arcore.depth = ARCoreSettings.Requirement.Optional;
            EditorUtility.SetDirty(arcore);
            return true;
        }

        static bool ConfigureArKit()
        {
            var arkit = ARKitSettings.currentSettings;
            if (arkit == null || !AssetDatabase.Contains(arkit))
            {
                EnsureFolder("Assets", "XR");
                EnsureFolder(XrFolder, "Settings");
                arkit = ScriptableObject.CreateInstance<ARKitSettings>();
                AssetDatabase.CreateAsset(arkit, $"{XrSettingsFolder}/ARKitSettings.asset");
                ARKitSettings.currentSettings = arkit;
            }

            // The whole app is built on tracking, so a device without ARKit can't run it.
            arkit.requirement = ARKitSettings.Requirement.Required;
            // Nothing here tracks faces, and asking for it would narrow which devices can install it.
            arkit.faceTracking = false;
            EditorUtility.SetDirty(arkit);
            return true;
        }
    }
}
