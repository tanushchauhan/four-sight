using System.Collections;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardboardXR.Demos
{
    // Moves between the lobby and the demos. The Core scene (rig, AR, hands, gaze) stays loaded the
    // whole time, so tracking, placed anchors and the viewer's calibration carry across; the lobby
    // and each demo are scenes loaded on top of it and unloaded on leaving. Entering a demo switches
    // Core to what the demo needs (DemoRequirements) and leaving puts back what was on before, so the
    // lobby is as the user left it. Going straight from one demo to another goes straight from one's
    // needs to the next's, from what was on before the first: putting the lobby's 3DoF back in between
    // turned AR off and on again, which restarted tracking. The "demo" launch option (an index into the catalog) starts in a
    // demo instead of the lobby. Demos are picked in the app library (AppLibrary), from the lobby or
    // from another demo. Nothing is placed on surfaces in the lobby, and entering a demo starts it with
    // no objects placed. Lives in the Core scene.
    public class DemoManager : MonoBehaviour
    {
        [SerializeField] DemoCatalog m_Catalog;
        [SerializeField] CoreServices m_Core;
        [Tooltip("The view fades through black between scenes, rather than cutting.")]
        [SerializeField] ScreenFade m_Fade;

        string m_LoadedScene;
        DemoCatalog.Entry m_Current;
        CoreState m_BeforeDemo;
        GameObject m_DefaultPrefab;
        bool m_Busy;
        Coroutine m_Fading;

        // Fading out of a scene, quickly, then into the next more slowly, once it has started.
        const float k_FadeOutSeconds = 0.3f;
        const float k_FadeInSeconds = 0.5f;

        public DemoCatalog Catalog => m_Catalog;
        // The demo running now, or null in the lobby.
        public DemoCatalog.Entry Current => m_Current;

        // A demo (or the lobby, null) finished loading.
        public event System.Action<DemoCatalog.Entry> Loaded;

        // Each demo's own log lines ([Video] and so on: its scene's name) go to the session log too,
        // so a new demo's do without listing its tag anywhere.
        void Awake()
        {
            if (m_Catalog == null)
                return;
            foreach (var entry in m_Catalog.Demos)
                SessionLog.KeepTag(entry.LogTag);
        }

        // A frame after Core's own Start, so the launch options for 6DoF and passthrough have applied
        // before a demo overrides them.
        IEnumerator Start()
        {
            yield return null;
            if (m_Catalog == null)
            {
                Debug.LogError("[Demos] No demo catalog, so no lobby or demos");
                yield break;
            }
            m_DefaultPrefab = m_Core.Placer.ObjectPrefab;
            m_Core.Placer.ObjectPrefab = null;
            var index = LaunchOptions.GetInt("demo", -1);
            if (m_Catalog.TryGet(index, out var entry))
            {
                Debug.Log($"[Demos] Starting in demo {index} ({entry.Id}), from the launch option");
                yield return Switch(entry);
            }
            else
            {
                if (index >= 0)
                    Debug.LogWarning($"[Demos] No demo {index}: the catalog has {m_Catalog.Demos.Count}");
                // Visor launch: 3DoF, with the camera and hand models off. "dof 6" restores the lobby's
                // room scan (6DoF, passthrough, hands). "passthrough 1" still opens the camera, which
                // needs 6DoF. "arView 1" has already forced 6DoF, so leave that alone.
                if (LaunchOptions.GetInt("dof", 3) == 6)
                {
                    var state = CoreState.Resolve(m_Core.State, m_Catalog.LobbyRequirements);
                    Debug.Log($"[Demos] Starting the lobby in {state}");
                    m_Core.Apply(state);
                }
                else if (LaunchOptions.GetInt("arView", 0) == 0)
                {
                    var passthrough = LaunchOptions.GetInt("passthrough", 0) != 0;
                    var state = new CoreState(passthrough, passthrough, false);
                    Debug.Log($"[Demos] Starting the lobby in {state}");
                    m_Core.Apply(state);
                }
                yield return Switch(null);
            }
        }

        public void Enter(int index)
        {
            if (m_Catalog.TryGet(index, out var entry))
                Enter(entry);
        }

        public void Enter(DemoCatalog.Entry entry)
        {
            if (m_Busy || entry == m_Current)
                return;
            StartCoroutine(Switch(entry));
        }

        // The app library's Lobby, and a demo's own way out.
        public void ExitToLobby()
        {
            if (m_Busy || m_Current == null)
                return;
            StartCoroutine(Switch(null));
        }

        // Unloads what's loaded and loads the demo, or the lobby for null.
        IEnumerator Switch(DemoCatalog.Entry next)
        {
            m_Busy = true;
            var started = Time.realtimeSinceStartup;
            // The first scene comes up out of black; any later one fades the last out first.
            if (m_Fade != null)
            {
                StopFade();
                if (m_LoadedScene == null)
                    m_Fade.Alpha = 1f;
                else
                    yield return m_Fade.FadeTo(1f, k_FadeOutSeconds);
            }
            var fromDemo = m_Current != null;
            yield return Unload(restore: next == null);

            if (next != null)
            {
                // From the lobby, what to put back on leaving; from another demo, that's kept.
                if (!fromDemo)
                    m_BeforeDemo = m_Core.State;
                var state = CoreState.Resolve(m_BeforeDemo, next.Requirements);
                Debug.Log($"[Demos] Entering {next.Id}: {state} (was {m_BeforeDemo})");
                // Anything a demo kept on leaving doesn't carry into the next one.
                m_Core.Placer.RemoveAll();
                // Cubes, unless the demo chooses something else in OnDemoEnter.
                m_Core.Placer.ObjectPrefab = m_DefaultPrefab;
                m_Core.Apply(state);
                // Unless the lobby's scan already covered the room.
                if (next.Requirements.ScanSurfaces && state.SixDof && !m_Core.Boundary.HasScannedRoom)
                    m_Core.Boundary.ScanRoom();
            }

            var path = next != null ? next.ScenePath : m_Catalog.LobbyScenePath;
            var loading = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
            if (loading == null)
            {
                Debug.LogError($"[Demos] Couldn't load {path}: is it in the build?");
                m_Busy = false;
                if (m_Fade != null)
                    m_Fade.Alpha = 0f;
                yield break;
            }
            yield return loading;

            var scene = SceneManager.GetSceneByPath(path);
            // What the demo creates goes into its own scene, so unloading it cleans up.
            SceneManager.SetActiveScene(scene);
            m_LoadedScene = path;
            m_Current = next;
            foreach (var demo in Demos(scene))
                demo.OnDemoEnter(m_Core);
            Debug.Log($"[Demos] {(next != null ? next.Title : "Lobby")} loaded in {Time.realtimeSinceStartup - started:F1} s");
            m_Busy = false;
            Loaded?.Invoke(next);
            if (m_Fade != null)
                m_Fading = StartCoroutine(FadeIn());
        }

        // A couple of frames late, so the scene and any mode change it made have drawn.
        IEnumerator FadeIn()
        {
            yield return null;
            yield return null;
            yield return m_Fade.FadeTo(0f, k_FadeInSeconds);
            m_Fading = null;
        }

        void StopFade()
        {
            if (m_Fading != null)
                StopCoroutine(m_Fading);
            m_Fading = null;
        }

        // restore: put back what was on before the demo being left; not when another demo follows.
        IEnumerator Unload(bool restore)
        {
            // The settings panel was opened for the scene being left.
            if (m_Core.Settings != null)
                m_Core.Settings.Close();
            if (m_LoadedScene == null)
                yield break;

            var scene = SceneManager.GetSceneByPath(m_LoadedScene);
            foreach (var demo in Demos(scene))
                demo.OnDemoExit(m_Core);

            var leaving = m_Current;
            m_Current = null;
            m_LoadedScene = null;
            if (leaving != null)
            {
                if (!leaving.Requirements.KeepPlacedObjects)
                    m_Core.Placer.RemoveAll();
                m_Core.Placer.ObjectPrefab = null;
                PinchRouter.Clear();
                if (restore)
                {
                    Debug.Log($"[Demos] Leaving {leaving.Id}: back to {m_BeforeDemo}");
                    m_Core.Apply(m_BeforeDemo);
                }
                else
                {
                    Debug.Log($"[Demos] Leaving {leaving.Id} for another app");
                }
            }

            SceneManager.SetActiveScene(gameObject.scene);
            yield return SceneManager.UnloadSceneAsync(scene);
            // Demo assets, such as a video's textures, are only unloaded once nothing refers to them.
            yield return Resources.UnloadUnusedAssets();
        }

        static Demo[] Demos(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return System.Array.Empty<Demo>();
            var found = new System.Collections.Generic.List<Demo>();
            foreach (var root in scene.GetRootGameObjects())
                found.AddRange(root.GetComponentsInChildren<Demo>());
            return found.ToArray();
        }
    }
}
