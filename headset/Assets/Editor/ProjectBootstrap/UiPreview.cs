using System.IO;
using CardboardXR.Demos;
using CardboardXR.UI;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    // Renders the Material UI as the phone's mono view would show it, without a phone: Core's settings
    // panel and app library as built for the app, each demo's panels, and scanned surfaces, seen from
    // the head's starting point. For reviewing
    // spacing, shapes, type and icons. Writes PNGs to the folder given after -previewPath.
    // Invoke with: -executeMethod ProjectBootstrap.UiPreview.Capture -previewPath <folder>
    public static class UiPreview
    {
        public static void Capture()
        {
            var folder = ArgumentAfter("-previewPath") ?? Path.Combine(Path.GetTempPath(), "cardboardxr-preview");
            Directory.CreateDirectory(folder);
            StereoBuilder.BuildScenes();

            var camera = new GameObject("Preview Camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.1f, 0.12f);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.05f;

            // The settings panel as SettingsSummoner opens it, below eye level, with the app library at
            // eye level above it, one app looked at.
            var panel = (RectTransform)GameObject.Find("Gaze Panel").transform;
            var above = (RectTransform)panel.Find("App Library");
            CardboardXR.Hands.SettingsSummoner.Place(panel, above, Vector3.zero, 0f);
            Render(camera, Path.Combine(folder, "toolbar.png"), -12f, 0f);
            var library = Object.FindAnyObjectByType<AppLibrary>();
            library.Show(true);
            Render(camera, Path.Combine(folder, "library.png"), -6f, 0f);
            var tagline = panel.Find("App Library/Tagline").GetComponent<TMP_Text>();
            tagline.text = "Paint in the air with your hand";
            Render(camera, Path.Combine(folder, "library-gaze.png"), -6f, 0f);
            panel.gameObject.SetActive(false);
            RenderDemoPanels(camera, folder);
            RenderSurfaces(camera, folder);
            Debug.Log($"[UiPreview] Wrote previews to {folder}");
            EditorApplication.Exit(0);
        }

        // Each demo's panels, built as the demo builds them (its private Build methods, by reflection:
        // they need nothing from Core) and filled with sample content, straight ahead.
        static void RenderDemoPanels(Camera camera, string folder)
        {
            var video = Demo("CardboardXR.Demos.Video.VideoDemo, CardboardXR.Demos.Video");
            if (video != null)
            {
                Call(video, "BuildPanel");
                var library = (System.Collections.IList)Get(video, "m_Library");
                var entryType = library.GetType().GetGenericArguments()[0];
                foreach (var (title, file, drive) in new[]
                         {
                             ("Sample_360", "Sample_360.mp4", false), ("Sample_180_sbs", "Sample_180_sbs.mp4", false),
                             ("Beach walk", "beach_360_tb.mp4", false), ("Concert", "concert_180.mp4", true),
                         })
                {
                    var entry = System.Activator.CreateInstance(entryType);
                    entryType.GetField("Title").SetValue(entry, title);
                    var format = entryType.GetField("Format").FieldType.GetMethod("FromFileName")?.Invoke(null, new object[] { file });
                    if (format != null)
                        entryType.GetField("Format").SetValue(entry, format);
                    entryType.GetField(drive ? "DriveId" : "Url").SetValue(entry, file);
                    library.Add(entry);
                }
                Set(video, "m_Current", 1);
                ((RectTransform)Get(video, "m_ControlsPage")).gameObject.SetActive(false);
                Call(video, "UpdateLibraryLabels");
                var panel = Show(video, "m_Panel", 1.5f, -12f);
                panel.sizeDelta = new Vector2(600f, 536f);
                Render(camera, Path.Combine(folder, "video-library.png"), -12f, 0f);
                ((RectTransform)Get(video, "m_LibraryPage")).gameObject.SetActive(false);
                ((RectTransform)Get(video, "m_ControlsPage")).gameObject.SetActive(true);
                panel.sizeDelta = new Vector2(600f, 352f);
                ((TMP_Text)Get(video, "m_Title")).text = "Sample_180_sbs";
                ((TMP_Text)Get(video, "m_FormatLine")).text = "180 3D side by side";
                ((TMP_Text)Get(video, "m_Time")).text = "0:42 / 2:10";
                ((M3Slider)Get(video, "m_Seek")).Value = 0.32f;
                Render(camera, Path.Combine(folder, "video-controls.png"), -12f, 0f);
                panel.gameObject.SetActive(false);
            }

            var paint = Demo("CardboardXR.Demos.Paint.PaintDemo, CardboardXR.Demos.Paint");
            if (paint != null)
            {
                Call(paint, "BuildPanel");
                ((TMP_Text)Get(paint, "m_Status")).text = "3 strokes. Pinch in the air to paint; look here and pinch to choose.";
                ((M3Button)((System.Collections.IList)Get(paint, "m_Swatches"))[5]).Selected = true;
                ((M3Button[])Get(paint, "m_BrushButtons"))[0].Selected = true;
                ((M3Button[])Get(paint, "m_SizeButtons"))[1].Selected = true;
                var panel = Show(paint, "m_Panel", 1.1f, 0f);
                Render(camera, Path.Combine(folder, "paint.png"), 0f, 0f);
                panel.gameObject.SetActive(false);
            }

            var rush = Demo("CardboardXR.Demos.Rush.RushDemo, CardboardXR.Demos.Rush");
            if (rush != null)
            {
                Call(rush, "BuildMenu");
                Call(rush, "BuildPauseMenu");
                Call(rush, "BuildHud");
                ((TMP_Text)Get(rush, "m_MenuBody")).text = "Orbs fly toward you: hit them with your hand as they reach the rings.\n" +
                                                           "Face the way you want to play, then press Start.\nBest score 1240";
                var menu = Show(rush, "m_Menu", 1.6f, -8f);
                Render(camera, Path.Combine(folder, "rush-menu.png"), -8f, 0f);
                menu.gameObject.SetActive(false);
                ((TMP_Text)Get(rush, "m_PauseBody")).text = "Score 860, 34 s left";
                var pause = Show(rush, "m_PauseMenu", 1.6f, -8f);
                Render(camera, Path.Combine(folder, "rush-pause.png"), -8f, 0f);
                pause.gameObject.SetActive(false);
                ((TMP_Text)Get(rush, "m_HudText")).text = "860    Combo 12 ×2    0:34";
                ((TMP_Text)Get(rush, "m_Callout")).text = "Perfect";
                ((TMP_Text)Get(rush, "m_Callout")).color = M3Theme.Primary;
                Show(rush, "m_Hud", 2.4f, 10f);
                Render(camera, Path.Combine(folder, "rush-hud.png"), 0f, 0f);
            }
        }

        // Detected surfaces as a scan shows them: a floor and a table-height patch, mid-wave and after.
        static void RenderSurfaces(Camera camera, string folder)
        {
            var material = new Material(Shader.Find("CardboardXR/Boundary"));
            Surface(material, new Vector3(0f, -1.3f, 2f), new[]
            {
                new Vector2(-2f, -1.2f), new Vector2(1.6f, -1.6f), new Vector2(2.2f, 1.4f), new Vector2(-0.4f, 2.2f), new Vector2(-2.4f, 1f),
            });
            Surface(material, new Vector3(0.6f, -0.6f, 1.3f), new[]
            {
                new Vector2(-0.45f, -0.3f), new Vector2(0.45f, -0.3f), new Vector2(0.45f, 0.3f), new Vector2(-0.45f, 0.3f),
            });
            Shader.SetGlobalMatrix("_BoundaryWorldToSession", Matrix4x4.identity);
            Shader.SetGlobalFloat("_BoundaryRadius", 0f);
            Shader.SetGlobalFloat("_BoundaryScan", 1f);
            Shader.SetGlobalVector("_BoundaryWave", new Vector4(0f, 0f, 0f, 2.4f));
            Render(camera, Path.Combine(folder, "surfaces-wave.png"), -35f, 0f);
            Shader.SetGlobalVector("_BoundaryWave", new Vector4(0f, 0f, 0f, 100f));
            Render(camera, Path.Combine(folder, "surfaces.png"), -35f, 0f);

            // Passthrough: a lit cube on the table patch, its shadow caught on the camera image (a
            // flat gray here), after the scan has faded.
            var catcher = new Material(Shader.Find("CardboardXR/ShadowCatcher"));
            foreach (var renderer in Object.FindObjectsByType<MeshRenderer>())
            {
                if (renderer.name != "Preview Surface")
                    continue;
                renderer.sharedMaterials = new[] { catcher, material };
                renderer.sortingOrder = CardboardXR.Stereo.SurfaceMesh.SortingOrder(renderer.transform.position.y);
            }
            var light = new GameObject("Preview Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(60f, -40f, 0f);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = new Vector3(0.6f, -0.55f, 1.3f);
            cube.transform.localScale = Vector3.one * 0.1f;
            cube.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Simple Lit"))
            {
                color = new Color(1f, 0.75f, 0.2f),
            };
            Shader.SetGlobalFloat("_BoundaryScan", 0f);
            Shader.SetGlobalFloat("_ShadowCatcherStrength", 0.55f);
            var background = camera.backgroundColor;
            camera.backgroundColor = new Color(0.55f, 0.52f, 0.48f);
            Render(camera, Path.Combine(folder, "shadows.png"), -35f, 15f);
            camera.backgroundColor = background;
        }

        static void Surface(Material material, Vector3 position, Vector2[] boundary)
        {
            var vertices = new System.Collections.Generic.List<Vector3>();
            var fades = new System.Collections.Generic.List<Vector2>();
            var triangles = new System.Collections.Generic.List<int>();
            CardboardXR.Stereo.SurfaceMesh.Build(boundary, Vector2.zero, 0.15f, vertices, fades, triangles);
            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetUVs(1, fades);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            var surface = new GameObject("Preview Surface", typeof(MeshFilter), typeof(MeshRenderer));
            surface.transform.position = position;
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
            surface.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        static Component Demo(string typeName)
        {
            var type = System.Type.GetType(typeName);
            if (type == null)
            {
                Debug.LogWarning($"[UiPreview] No {typeName}");
                return null;
            }
            return new GameObject(type.Name).AddComponent(type);
        }

        const System.Reflection.BindingFlags k_Private = System.Reflection.BindingFlags.Instance |
                                                          System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

        static void Call(Component target, string method) => target.GetType().GetMethod(method, k_Private).Invoke(target, null);

        static object Get(Component target, string field) => target.GetType().GetField(field, k_Private).GetValue(target);

        static void Set(Component target, string field, object value) => target.GetType().GetField(field, k_Private).SetValue(target, value);

        // A panel straight ahead at a distance and pitch, facing away from the camera as GazeCanvas expects.
        static RectTransform Show(Component target, string field, float distance, float pitch)
        {
            var panel = (RectTransform)Get(target, field);
            var direction = Quaternion.Euler(-pitch, 0f, 0f) * Vector3.forward;
            panel.SetPositionAndRotation(direction * distance, Quaternion.LookRotation(direction, Vector3.up));
            panel.gameObject.SetActive(true);
            return panel;
        }

        static void Render(Camera camera, string path, float pitch, float yaw)
        {
            camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(-pitch, yaw, 0f));
            foreach (var text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Exclude))
                text.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
            // A landscape phone's shape.
            var texture = new RenderTexture(1600, 720, 24) { antiAliasing = 4 };
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
            RenderTexture.active = null;
            camera.targetTexture = null;
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(image);
        }

        static string ArgumentAfter(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                    return args[i + 1];
            }
            return null;
        }
    }
}
