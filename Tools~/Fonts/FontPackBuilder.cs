// Maintainer recipe. Copy into a consumer project's Editor assembly to compile.
// This file is not imported or executed by installing the Framework UPM package.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using TkFont = UnityEngine.TextCore.Text.FontAsset;

namespace SSFramework.FontDistribution
{
    /// <summary>Builds opt-in font assets through Unity APIs without changing scenes or global text settings.</summary>
    public static class FontPackBuilder
    {
        public const string FontFile = "NotoSansSC-Regular.otf";
        public const string FontHash = "faa6c9df652116dde789d351359f3d7e5d2285a2b2a1f04a2d7244df706d5ea9";
        public const string LicenseHash = "6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2";
        private static readonly string[] SourceFiles = { FontFile, "OFL.txt", "NOTICE.txt", "README.txt" };

        /// <summary>Creates a new asset folder only. Existing destinations, source mismatches and busy editors fail before writing.</summary>
        public static string[] Create(string sourceDirectory, string destination)
        {
            RequireEditMode();
            string physical = ValidateDestination(destination);
            if (Directory.Exists(physical) || File.Exists(physical) || File.Exists(physical + ".meta"))
                throw new IOException("Destination already exists; preserve its assets and GUIDs: " + destination);
            foreach (string name in SourceFiles)
                if (!File.Exists(Path.Combine(sourceDirectory, name))) throw new FileNotFoundException("Missing source: " + name);
            VerifyHash(Path.Combine(sourceDirectory, FontFile), FontHash);
            VerifyHash(Path.Combine(sourceDirectory, "OFL.txt"), LicenseHash);
            if (Shader.Find("TextMeshPro/Mobile/Distance Field") == null)
                throw new InvalidOperationException("Import TMP Essential Resources before creating the font pack.");
            bool ownsDestination = false;
            try
            {
                string[] parts = destination.Split('/');
                string parent = parts[0];
                foreach (string part in parts.Skip(1))
                {
                    string next = parent + "/" + part;
                    if (!AssetDatabase.IsValidFolder(next))
                    {
                        if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, part))) throw new IOException("Could not create " + next);
                    }
                    parent = next;
                }
                ownsDestination = true;
                foreach (string name in SourceFiles)
                {
                    File.Copy(Path.Combine(sourceDirectory, name), Path.Combine(physical, name), false);
                    AssetDatabase.ImportAsset(destination + "/" + name, ImportAssetOptions.ForceSynchronousImport);
                }
                var font = AssetDatabase.LoadAssetAtPath<Font>(destination + "/" + FontFile);
                if (font == null) throw new InvalidOperationException("Source font failed to import.");
                var tmp = TMP_FontAsset.CreateFontAsset(font, 48, 5, GlyphRenderMode.SDFAA, 1024, 1024, TMPro.AtlasPopulationMode.Dynamic, true);
                if (tmp == null) throw new InvalidOperationException("TMP font creation failed.");
                SaveFont(tmp, tmp.material, tmp.atlasTextures, destination + "/NotoSansSC-Regular TMP.asset");
                var tk = TkFont.CreateFontAsset(font, 48, 5, GlyphRenderMode.SDFAA, 1024, 1024, UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic, true);
                if (tk == null) throw new InvalidOperationException("TextCore font creation failed.");
                SaveFont(tk, tk.material, tk.atlasTextures, destination + "/NotoSansSC-Regular TextCore.asset");
                AssetDatabase.SaveAssets();
                return AssetPaths(destination);
            }
            catch
            {
                // Only this invocation's new leaf folder is removed; existing parents/assets stay intact.
                if (ownsDestination && !AssetDatabase.DeleteAsset(destination))
                    Debug.LogError("Font creation failed; inspect the newly created folder: " + destination);
                throw;
            }
        }

        /// <summary>Exports the six reviewed assets only. TMP shaders remain an explicit TMP Essentials prerequisite.</summary>
        public static void Export(string destination, string outputPackage)
        {
            RequireEditMode();
            string physical = ValidateDestination(destination);
            if (File.Exists(outputPackage)) throw new IOException("Output package already exists; it was preserved.");
            if (!outputPackage.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use a .unitypackage output.");
            VerifyHash(Path.Combine(physical, FontFile), FontHash);
            VerifyHash(Path.Combine(physical, "OFL.txt"), LicenseHash);
            string[] paths = AssetPaths(destination);
            foreach (string path in paths) if (AssetDatabase.LoadMainAssetAtPath(path) == null) throw new IOException("Missing asset: " + path);
            var tmp = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(paths[4]);
            var tk = AssetDatabase.LoadAssetAtPath<TkFont>(paths[5]);
            if (tmp.sourceFontFile == null || tk.sourceFontFile == null ||
                tmp.atlasPopulationMode != TMPro.AtlasPopulationMode.Dynamic ||
                tk.atlasPopulationMode != UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic)
                throw new InvalidOperationException("Both fonts must be Dynamic with bundled source references.");
            // Runtime-populated glyphs must not accidentally become part of the distributed baseline.
            tmp.ClearFontAssetData(true);
            tk.ClearFontAssetData(true);
            EditorUtility.SetDirty(tmp);
            EditorUtility.SetDirty(tk);
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPackage)));
            AssetDatabase.ExportPackage(paths, outputPackage, ExportPackageOptions.Default);
        }

        private static string[] AssetPaths(string destination) => SourceFiles.Select(x => destination + "/" + x)
            .Concat(new[] { destination + "/NotoSansSC-Regular TMP.asset", destination + "/NotoSansSC-Regular TextCore.asset" }).ToArray();

        private static void SaveFont(UnityEngine.Object asset, Material material, Texture2D[] atlases, string path)
        {
            if (material == null || atlases == null || atlases.Length != 1) throw new InvalidOperationException("Unexpected font sub-assets.");
            asset.name = Path.GetFileNameWithoutExtension(path);
            material.name = asset.name + " Material";
            atlases[0].name = asset.name + " Atlas";
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.AddObjectToAsset(material, asset);
            AssetDatabase.AddObjectToAsset(atlases[0], asset);
            var serialized = new SerializedObject(asset);
            var clearOnBuild = serialized.FindProperty("m_ClearDynamicDataOnBuild");
            if (clearOnBuild == null) throw new InvalidOperationException("Unsupported font asset serialization layout.");
            clearOnBuild.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static string ValidateDestination(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination) || !destination.StartsWith("Assets/", StringComparison.Ordinal) ||
                destination.Contains('\\') || destination.Split('/').Any(x => string.IsNullOrEmpty(x) || x == "." || x == ".."))
                throw new ArgumentException("Use a new folder beneath Assets without relative segments.");
            string physical = Path.GetFullPath(Path.Combine(Application.dataPath, "..", destination));
            for (var folder = new DirectoryInfo(physical); folder != null; folder = folder.Parent)
            {
                if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked asset directories require a separate manual review.");
                if (string.Equals(folder.FullName, Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase)) break;
            }
            return physical;
        }

        private static void RequireEditMode()
        {
            if (!Application.unityVersion.StartsWith("6000.3.", StringComparison.Ordinal)) throw new InvalidOperationException("This recipe targets Unity 6.3.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for a stable Edit mode before building font assets.");
        }

        private static void VerifyHash(string path, string expected)
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidDataException("Source hash mismatch: " + path);
        }
    }
}
