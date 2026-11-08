using System.Linq;
using UnityEngine;
using UnityEngine.XR.Management;

namespace CardboardXR.Stereo
{
    // Says out loud whether XR Plug-in Management started and which loader it started, because
    // nothing else does. When XR fails to start, AR Foundation only reports the consequences, one
    // warning per missing subsystem, and XR Management itself says nothing at all: if its settings
    // didn't make it into the build it returns without a word, which looks exactly like success.
    static class XrStartupLog
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Report()
        {
            var settings = XRGeneralSettings.Instance;
            if (settings == null)
            {
                Debug.Log("[Stereo] XR settings are not in the build, so no AR provider can start. " +
                          "XR Plug-in Management adds them as a preloaded asset at build time.");
                return;
            }

            var manager = settings.Manager;
            if (manager == null)
            {
                Debug.Log("[Stereo] XR settings have no manager, so no loader can start.");
                return;
            }

            var loaders = manager.activeLoaders?.Select(loader => loader == null ? "null" : loader.name).ToArray();
            var available = loaders == null || loaders.Length == 0 ? "none" : string.Join(", ", loaders);
            var active = manager.activeLoader == null ? "none" : manager.activeLoader.name;
            Debug.Log($"[Stereo] XR init on start {settings.InitManagerOnStart}, active loader {active}, " +
                      $"loaders in the build: {available}");
        }
    }
}
