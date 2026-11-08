using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CardboardXR.Demos;
using CardboardXR.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectBootstrap
{
    // Creates the demo catalog, the lobby and each demo's starter scene, but only those that don't
    // exist yet. Unlike Core, these are made once and then edited by hand in the Editor, so a build
    // never overwrites them; delete one to have it made again. Their materials go in their own
    // folders for the same reason, since Core's generated ones are replaced on every build.
    // The demos come from their DemoSetup subclasses, one in each demo's folder, so nothing here names
    // a demo. Logs [DemoScenes].
    public static class DemoScenes
    {
        internal const string DemosFolder = "Assets/_Project/Demos";
        internal const string CatalogPath = DemosFolder + "/DemoCatalog.asset";
        const string LobbyFolder = DemosFolder + "/Lobby";
        const string LobbyScene = LobbyFolder + "/Lobby.unity";

        // A folder name, which is also a C# name and a log tag; and a short id for logs.
        static readonly Regex k_NamePattern = new("^[A-Z][A-Za-z0-9]*$");
        static readonly Regex k_IdPattern = new("^[a-z][a-z0-9-]*$");

        // Makes a new demo's scene and catalog entry (and anything else missing) without a build: from
        // the menu, or with Unity closed:
        //   Unity -batchmode -quit -projectPath . -executeMethod ProjectBootstrap.DemoScenes.MakeMissing
        // It opens scenes of its own, so in the Editor it first offers to save the open ones.
        [MenuItem("CardboardXR/Make Missing Demo Scenes")]
        public static void MakeMissing()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            if (!EnsureAll() && Application.isBatchMode)
                EditorApplication.Exit(1);
        }

        // Makes whatever is missing. False if a demo's setup is wrong, which it logs as an error. Call it
        // before opening another scene, since it opens and saves its own.
        public static bool EnsureAll()
        {
            StereoBuilder.EnsureFolder(DemosFolder);
            var setups = FindSetups();
            if (setups == null)
                return false;
            var catalog = EnsureCatalog(setups);
            // Saved now: the scenes below are opened in Single mode, which unloads assets loaded before.
            AssetDatabase.SaveAssetIfDirty(catalog);
            EnsureLobby();
            foreach (var setup in setups)
                EnsureScene(setup);
            AssetDatabase.SaveAssets();
            return true;
        }

        // Every demo's setup, in Order, or null if any is wrong.
        static List<DemoSetup> FindSetups()
        {
            var setups = new List<DemoSetup>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<DemoSetup>())
            {
                if (!type.IsAbstract)
                    setups.Add((DemoSetup)Activator.CreateInstance(type));
            }
            setups.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Name, b.Name));

            var ok = true;
            var names = new HashSet<string>();
            var ids = new HashSet<string>();
            foreach (var setup in setups)
            {
                var type = setup.GetType().Name;
                var entry = setup.Entry;
                if (setup.Name == null || !k_NamePattern.IsMatch(setup.Name))
                    ok = Problem($"{type}.Name \"{setup.Name}\" should be a folder name like \"Portal\": a capital letter, then letters and digits");
                else if (setup.Name == "Lobby" || !names.Add(setup.Name))
                    ok = Problem($"{type}.Name \"{setup.Name}\" is taken: another demo, or the lobby, has that folder");
                if (entry == null)
                {
                    ok = Problem($"{type}.Entry is null");
                    continue;
                }
                if (entry.Id == null || !k_IdPattern.IsMatch(entry.Id))
                    ok = Problem($"{type}: the Id \"{entry.Id}\" should be short and lowercase, like \"portal\"");
                else if (!ids.Add(entry.Id))
                    ok = Problem($"{type}: another demo already has the Id \"{entry.Id}\"");
                if (string.IsNullOrWhiteSpace(entry.Title))
                    ok = Problem($"{type}: the entry needs a Title, for the app library");
            }
            return ok ? setups : null;
        }

        static bool Problem(string message)
        {
            Debug.LogError($"[DemoScenes] {message}");
            return false;
        }

        static DemoCatalog EnsureCatalog(List<DemoSetup> setups)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<DemoCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<DemoCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            // Entries are added once each; after that the catalog is edited by hand, so a changed
            // title or order stays. So after a merge conflict in the catalog, either side will do:
            // the next build adds back any demo's entry that's missing.
            foreach (var setup in setups)
                EnsureEntry(catalog, setup);
            var known = new HashSet<string>();
            foreach (var setup in setups)
                known.Add(setup.Entry.Id);
            foreach (var entry in catalog.Demos)
            {
                if (!known.Contains(entry.Id))
                    Debug.LogWarning($"[DemoScenes] The catalog's {entry.Id} has no DemoSetup; remove it from {CatalogPath} if its demo is gone");
            }
            return catalog;
        }

        static void EnsureEntry(DemoCatalog catalog, DemoSetup setup)
        {
            var source = setup.Entry;
            if (catalog.IndexOf(source.Id) >= 0)
                return;
            var serialized = new SerializedObject(catalog);
            var demos = serialized.FindProperty("m_Demos");
            demos.arraySize++;
            var entry = demos.GetArrayElementAtIndex(demos.arraySize - 1);
            entry.FindPropertyRelative("Id").stringValue = source.Id;
            entry.FindPropertyRelative("Title").stringValue = source.Title;
            entry.FindPropertyRelative("Description").stringValue = source.Description ?? "";
            entry.FindPropertyRelative("Tagline").stringValue = source.Tagline ?? "";
            entry.FindPropertyRelative("Icon").stringValue = string.IsNullOrWhiteSpace(source.Icon) ? "apps" : source.Icon;
            entry.FindPropertyRelative("Tint").colorValue = source.Tint;
            entry.FindPropertyRelative("ScenePath").stringValue = setup.ScenePath;
            var needs = entry.FindPropertyRelative("Requirements");
            var requirements = source.Requirements;
            needs.FindPropertyRelative("SixDof").enumValueIndex = (int)requirements.SixDof;
            needs.FindPropertyRelative("Passthrough").enumValueIndex = (int)requirements.Passthrough;
            needs.FindPropertyRelative("Hands").enumValueIndex = (int)requirements.Hands;
            needs.FindPropertyRelative("ScanSurfaces").boolValue = requirements.ScanSurfaces;
            needs.FindPropertyRelative("KeepPlacedObjects").boolValue = requirements.KeepPlacedObjects;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[DemoScenes] Added {source.Id} to the catalog");
        }

        static void EnsureScene(DemoSetup setup)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(setup.ScenePath) != null)
                return;
            StereoBuilder.EnsureFolder(setup.Folder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            setup.BuildScene(setup.Folder);
            if (Object.FindAnyObjectByType<Demo>() == null)
                Debug.LogWarning($"[DemoScenes] {setup.ScenePath} has no Demo component, so DemoManager won't call it on entering or leaving");
            EditorSceneManager.SaveScene(scene, setup.ScenePath);
            Debug.Log($"[DemoScenes] Made {setup.ScenePath}");
        }

        // The checkerboard room with its colored markers, which passthrough hides, and the demo cards.
        // Straight checker edges should look straight through the viewer.
        static void EnsureLobby()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LobbyScene) != null)
                return;
            StereoBuilder.EnsureFolder(LobbyFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unlit = DemoAssets.Unlit;

            var environment = DemoAssets.Environment();
            // Two checks per texture repeat, so one check is one meter. The tinted floor and ceiling
            // make a sideways or upside-down view obvious.
            var checker = DemoAssets.CreateCheckerTexture(LobbyFolder);
            var tiling = DemoAssets.RoomSize / 2f;
            BuildRoom(environment.transform,
                StereoBuilder.CreateMaterial("Checker Walls", unlit, Color.white, checker, tiling, LobbyFolder),
                StereoBuilder.CreateMaterial("Checker Floor", unlit, new Color(0.55f, 0.75f, 1f), checker, tiling, LobbyFolder),
                StereoBuilder.CreateMaterial("Checker Ceiling", unlit, new Color(1f, 0.8f, 0.55f), checker, tiling, LobbyFolder));
            BuildMarkers(environment.transform, unlit);

            // Not part of the environment: the hint stays in passthrough.
            new GameObject("Lobby Hint").AddComponent<LobbyHint>();

            EditorSceneManager.SaveScene(scene, LobbyScene);
            Debug.Log($"[DemoScenes] Made {LobbyScene}");
        }

        static void BuildRoom(Transform parent, Material walls, Material floor, Material ceiling)
        {
            var room = new GameObject("Checker Room");
            room.transform.SetParent(parent, false);
            const float size = DemoAssets.RoomSize;
            const float eyeHeight = DemoAssets.EyeHeight;
            var half = size / 2f;
            var middle = half - eyeHeight;
            (string name, Vector3 position, Vector3 rotation, Material material)[] surfaces =
            {
                ("Front", new Vector3(0f, middle, half), Vector3.zero, walls),
                ("Back", new Vector3(0f, middle, -half), new Vector3(0f, 180f, 0f), walls),
                ("Right", new Vector3(half, middle, 0f), new Vector3(0f, 90f, 0f), walls),
                ("Left", new Vector3(-half, middle, 0f), new Vector3(0f, -90f, 0f), walls),
                ("Floor", new Vector3(0f, -eyeHeight, 0f), new Vector3(90f, 0f, 0f), floor),
                ("Ceiling", new Vector3(0f, size - eyeHeight, 0f), new Vector3(-90f, 0f, 0f), ceiling),
            };

            foreach (var (name, position, rotation, material) in surfaces)
            {
                // Colliders stop the gaze, so the reticle sits on the walls.
                var surface = StereoBuilder.CreatePrimitive(PrimitiveType.Quad, name, room.transform, material, keepCollider: true);
                surface.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
                surface.transform.localScale = new Vector3(size, size, 1f);
            }
        }

        // Colored cubes in known directions. The white cube sits above the red one and green is on
        // the right, so a flipped or mirrored image is obvious. The orange cube is close, for depth.
        // Each lightens under the gaze.
        static void BuildMarkers(Transform parent, Shader unlit)
        {
            var markers = new GameObject("Markers");
            markers.transform.SetParent(parent, false);
            (string name, Color color, Vector3 position, float size)[] cubes =
            {
                ("Ahead", Color.red, new Vector3(0f, 0f, 3f), 0.4f),
                ("Above Ahead", Color.white, new Vector3(0f, 0.8f, 3f), 0.2f),
                ("Right", Color.green, new Vector3(3f, 0f, 0f), 0.4f),
                ("Left", Color.blue, new Vector3(-3f, 0f, 0f), 0.4f),
                ("Behind", Color.magenta, new Vector3(0f, 0f, -3f), 0.4f),
                ("Near", new Color(1f, 0.5f, 0f), new Vector3(0.25f, -0.1f, 0.7f), 0.08f),
            };

            foreach (var (name, color, position, size) in cubes)
            {
                var material = StereoBuilder.CreateMaterial(name, unlit, color, null, 1f, LobbyFolder);
                var cube = StereoBuilder.CreatePrimitive(PrimitiveType.Cube, name, markers.transform, material, keepCollider: true);
                cube.transform.localPosition = position;
                cube.transform.localScale = Vector3.one * size;
                cube.AddComponent<GazeHighlight>();
            }
        }
    }
}
