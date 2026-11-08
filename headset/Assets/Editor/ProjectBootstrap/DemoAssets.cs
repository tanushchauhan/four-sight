using CardboardXR.Stereo;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectBootstrap
{
    // What demo setups (DemoSetup.BuildScene) share for making their starter scenes: the shader, the
    // virtual environment, a floor, and textures made in code, since the app bundles everything it
    // shows. Materials and textures are saved in the demo's own folder, not Core's generated one.
    internal static class DemoAssets
    {
        internal const string UnlitShaderName = "Universal Render Pipeline/Unlit";
        // The head sits at the origin, standing eye height above the floor.
        internal const float EyeHeight = 1.6f;
        // The lobby's checker room, and the sandbox's floor.
        internal const float RoomSize = 10f;

        internal static Shader Unlit => Shader.Find(UnlitShaderName);

        // The root of what the demo shows in VR: passthrough hides it (VirtualEnvironment), so the
        // room shows instead. Anything that should stay in passthrough goes outside it.
        internal static GameObject Environment()
        {
            var environment = new GameObject("Environment");
            environment.AddComponent<VirtualEnvironment>();
            return environment;
        }

        // A square floor of the given size under the head, at eye height below it. keepCollider: so
        // the gaze stops on it, as for placing on it.
        internal static GameObject Floor(Transform parent, Material material, float size, bool keepCollider = false)
        {
            var floor = StereoBuilder.CreatePrimitive(PrimitiveType.Quad, "Floor", parent, material, keepCollider);
            floor.transform.SetLocalPositionAndRotation(new Vector3(0f, -EyeHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            floor.transform.localScale = new Vector3(size, size, 1f);
            return floor;
        }

        // Transparent and additive, set up as URP's material inspector would. Saved as an asset, so
        // the build keeps the shader variant it needs.
        internal static void MakeAdditive(Material material)
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
        }

        // A faint blue line along two edges of a dark square: tiled, a grid on a dark floor, which
        // gives a sense of space and scale in VR. Not saved; pass it to StereoBuilder.SaveAsset.
        internal static Texture2D CreateGridTexture()
        {
            const int size = 128;
            var background = new Color32(14, 16, 24, 255);
            var line = new Color32(55, 75, 110, 255);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = x < 2 || y < 2 ? line : background;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Grid",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        // Two checks per repeat, saved as folder/Checker.asset.
        internal static Texture2D CreateCheckerTexture(string folder)
        {
            const int size = 256;
            var light = new Color32(215, 215, 215, 255);
            var dark = new Color32(60, 60, 75, 255);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = (x < size / 2) == (y < size / 2) ? light : dark;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Checker",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return StereoBuilder.SaveAsset(texture, $"{folder}/Checker.asset");
        }
    }
}
