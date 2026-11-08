using CardboardXR.Demos;
using CardboardXR.Demos.Portal;
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    // Portal: the portal prefab (built out in code when placed) with its materials, and the demo.
    class PortalSetup : DemoSetup
    {
        const string DepthMaskShaderPath = "Assets/_Project/Shaders/DepthMask.shader";

        public override string Name => "Portal";
        public override int Order => 50;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "portal",
            Title = "Portal",
            Description = "Open a doorway on your floor and walk through it into another world, then look back at your room through the door.",
            Tagline = "A doorway on your floor into another world",
            Icon = "door_front",
            Tint = new Color(0.23f, 0.25f, 0.6f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On },
        };

        public override void BuildScene(string folder)
        {
            var unlit = DemoAssets.Unlit;
            var sky = StereoBuilder.CreateMaterial("Night Sky", unlit, Color.white,
                StereoBuilder.SaveAsset(CreateSkyTexture(), $"{folder}/Night Sky.asset"), 1f, folder);
            var ground = StereoBuilder.CreateMaterial("Portal Ground", unlit, new Color(0.8f, 0.7f, 1f),
                StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset"), 46f / 1f, folder);
            var glow = StereoBuilder.CreateMaterial("Portal Glow", unlit, Color.white, null, 1f, folder);
            DemoAssets.MakeAdditive(glow);
            var mask = StereoBuilder.SaveAsset(new Material(AssetDatabase.LoadAssetAtPath<Shader>(DepthMaskShaderPath)) { name = "Depth Mask" },
                $"{folder}/Depth Mask.mat");

            var portal = new GameObject("Portal").AddComponent<PortalWorld>();
            var serializedPortal = new SerializedObject(portal);
            serializedPortal.FindProperty("m_Sky").objectReferenceValue = sky;
            serializedPortal.FindProperty("m_Ground").objectReferenceValue = ground;
            serializedPortal.FindProperty("m_Glow").objectReferenceValue = glow;
            serializedPortal.FindProperty("m_Mask").objectReferenceValue = mask;
            serializedPortal.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(portal.gameObject, $"{folder}/Portal.prefab");
            Object.DestroyImmediate(portal.gameObject);

            var demo = new GameObject("Portal Demo").AddComponent<PortalDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_PortalPrefab").objectReferenceValue = prefab;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();
        }

        // A night sky: deep blue overhead, a violet glow at the horizon, and stars, denser higher up.
        // Across is longitude (the dome maps half a turn to one repeat), up is height above the horizon.
        static Texture2D CreateSkyTexture()
        {
            const int width = 1024;
            const int height = 256;
            var zenith = new Color(0.01f, 0.02f, 0.07f);
            var horizon = new Color(0.16f, 0.08f, 0.3f);
            var pixels = new Color[width * height];
            for (var y = 0; y < height; y++)
            {
                var color = Color.Lerp(horizon, zenith, Mathf.Pow(y / (height - 1f), 0.6f));
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = color;
            }
            var random = new System.Random(5);
            for (var i = 0; i < 900; i++)
            {
                var y = (int)(Mathf.Sqrt((float)random.NextDouble()) * (height - 1));
                var x = random.Next(width);
                var brightness = Mathf.Lerp(0.4f, 1f, (float)random.NextDouble());
                pixels[y * width + x] = new Color(brightness, brightness, Mathf.Min(1f, brightness * 1.1f));
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "Night Sky",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            texture.SetPixels(pixels);
            texture.Apply(true);
            return texture;
        }
    }
}
