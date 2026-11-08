using System.IO;
using System.Linq;
using CardboardXR.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace ProjectBootstrap
{
    // Makes what the Material UI needs, when it's missing: TextMesh Pro's essential resources (its
    // shaders and settings, imported from the ugui package, offline), SDF font assets from the
    // bundled Roboto and Material Icons fonts, the rounded sprite, and the M3Assets that runtime UI
    // finds them through. Runs before Core is built (StereoBuilder.BuildScenes).
    public static class MaterialAssets
    {
        const string UiFolder = "Assets/_Project/UI";
        const string FontsFolder = UiFolder + "/Fonts";
        const string ResourcesFolder = UiFolder + "/Resources";
        const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";
        const string MobileSdfShader = "TextMeshPro/Mobile/Distance Field";

        public static void Ensure()
        {
            EnsureTmpResources();
            StereoBuilder.EnsureFolder(ResourcesFolder);
            var path = $"{ResourcesFolder}/{M3Assets.k_ResourcePath}.asset";
            var assets = AssetDatabase.LoadAssetAtPath<M3Assets>(path);
            if (assets != null && assets.Regular != null && assets.Medium != null && assets.Icons != null && assets.Round != null)
            {
                AddMissingIcons(assets.Icons);
                return;
            }

            var regular = FontAsset("Roboto-Regular.ttf", "Roboto Regular SDF", null);
            var medium = FontAsset("Roboto-Medium.ttf", "Roboto Medium SDF", null);
            var icons = FontAsset("MaterialIconsRound-Regular.otf", "Material Icons SDF", M3Icons.AllGlyphs());
            var round = RoundSprite();
            if (assets == null)
            {
                assets = ScriptableObject.CreateInstance<M3Assets>();
                AssetDatabase.CreateAsset(assets, path);
            }
            assets.Set(regular, medium, icons, round);
            EditorUtility.SetDirty(assets);
            AssetDatabase.SaveAssets();
            Debug.Log("[MaterialAssets] Made the Material UI's fonts and shapes");
        }

        // TMP's shaders and settings come in a unitypackage inside the ugui package. It's unpacked here
        // rather than with AssetDatabase.ImportPackage, which in batch mode only queues the import, to
        // finish after this build has needed it.
        static void EnsureTmpResources()
        {
            if (File.Exists(TmpSettingsPath))
                return;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_Text).Assembly);
            var unityPackage = Path.Combine(package.resolvedPath, "Package Resources", "TMP Essential Resources.unitypackage");
            var count = UnpackUnityPackage(unityPackage, Path.GetDirectoryName(Application.dataPath));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[MaterialAssets] Unpacked {count} of TextMesh Pro's essential resources from {unityPackage}");
        }

        // A .unitypackage is a gzipped tar of one folder per asset, holding "pathname" (where it goes),
        // "asset" (its contents, absent for a folder) and "asset.meta". Writes each under the project.
        // Returns how many assets it wrote.
        static int UnpackUnityPackage(string unityPackage, string projectRoot)
        {
            var entries = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, byte[]>>();
            using (var file = File.OpenRead(unityPackage))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            {
                var header = new byte[512];
                while (ReadFully(gzip, header))
                {
                    if (header.All(b => b == 0))
                        break;
                    var name = System.Text.Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                    var prefix = System.Text.Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0');
                    if (prefix.Length > 0)
                        name = prefix + "/" + name;
                    var sizeText = System.Text.Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                    var size = sizeText.Length == 0 ? 0 : System.Convert.ToInt64(sizeText, 8);
                    var data = new byte[size];
                    ReadFully(gzip, data);
                    // Entries are padded to 512 bytes.
                    var padding = (512 - size % 512) % 512;
                    if (padding > 0)
                        ReadFully(gzip, new byte[padding]);
                    var parts = name.TrimStart('.', '/').Split('/');
                    if (parts.Length != 2 || header[156] == (byte)'5')
                        continue;
                    if (!entries.TryGetValue(parts[0], out var files))
                        entries[parts[0]] = files = new System.Collections.Generic.Dictionary<string, byte[]>();
                    files[parts[1]] = data;
                }
            }

            var written = 0;
            foreach (var files in entries.Values)
            {
                if (!files.TryGetValue("pathname", out var pathBytes))
                    continue;
                var relative = System.Text.Encoding.UTF8.GetString(pathBytes).Split('\n')[0].Trim();
                var target = Path.Combine(projectRoot, relative);
                if (files.TryGetValue("asset", out var asset))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, asset);
                    written++;
                }
                else
                {
                    Directory.CreateDirectory(target);
                }
                if (files.TryGetValue("asset.meta", out var meta))
                    File.WriteAllBytes(target + ".meta", meta);
            }
            return written;
        }

        static bool ReadFully(Stream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    return false;
                read += n;
            }
            return true;
        }

        // An SDF font asset saved with its atlas and material. Text fonts stay dynamic, adding glyphs
        // as they're used; the icon font gets exactly the icons the app names.
        static TMP_FontAsset FontAsset(string fileName, string assetName, string glyphs)
        {
            var path = $"{UiFolder}/{assetName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (existing != null)
                return existing;
            var font = AssetDatabase.LoadAssetAtPath<Font>($"{FontsFolder}/{fileName}");
            if (font == null)
            {
                Debug.LogError($"[MaterialAssets] Missing {FontsFolder}/{fileName}");
                return null;
            }

            var fontAsset = TMP_FontAsset.CreateFontAsset(font, 64, 8, GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            fontAsset.name = assetName;
            var shader = Shader.Find(MobileSdfShader);
            if (shader != null)
                fontAsset.material.shader = shader;
            AssetDatabase.CreateAsset(fontAsset, path);
            fontAsset.atlasTexture.name = $"{assetName} Atlas";
            fontAsset.material.name = $"{assetName} Material";
            AssetDatabase.AddObjectToAsset(fontAsset.atlasTexture, fontAsset);
            AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            if (!string.IsNullOrEmpty(glyphs))
            {
                if (!fontAsset.TryAddCharacters(glyphs, out var missing))
                    Debug.LogWarning($"[MaterialAssets] {assetName} lacks {missing.Length} of the icons");
                fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            }
            EditorUtility.SetDirty(fontAsset);
            return fontAsset;
        }

        // Icons named since the icon font was made: its atlas holds only the listed glyphs.
        static void AddMissingIcons(TMP_FontAsset icons)
        {
            var missing = new System.Text.StringBuilder();
            var count = 0;
            foreach (var codepoint in M3Icons.Codepoints.Values)
            {
                if (icons.characterLookupTable.ContainsKey((uint)codepoint))
                    continue;
                missing.Append(char.ConvertFromUtf32(codepoint));
                count++;
            }
            if (missing.Length == 0)
                return;
            icons.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            icons.TryAddCharacters(missing.ToString(), out _);
            icons.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(icons);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MaterialAssets] Added {count} icons to the icon font");
        }

        static Sprite RoundSprite()
        {
            var path = $"{UiFolder}/M3 Round.asset";
            var existing = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
            if (existing != null)
                return existing;
            var sprite = M3Shapes.CreateRound();
            AssetDatabase.CreateAsset(sprite.texture, path);
            AssetDatabase.AddObjectToAsset(sprite, sprite.texture);
            return sprite;
        }
    }
}
