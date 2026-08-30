using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    public sealed class FrameworkEditorProfileCatalogTests
    {
        [Test]
        public void TryResolveFirst_RepairsNonEmptyStalePathBeforeReportingMissing()
        {
            string folderName = "__FrameworkEditorProfileCatalogTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            string oldPath = folderPath + "/Old.asset";
            string newPath = folderPath + "/New.asset";
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var profile = ScriptableObject.CreateInstance<FrameworkEditorProfileCatalogProbe>();
                AssetDatabase.CreateAsset(profile, oldPath);
                FrameworkEditorProfileCatalog.Refresh(typeof(FrameworkEditorProfileCatalogProbe));
                Assert.That(FrameworkEditorProfileCatalog.GetPaths(typeof(FrameworkEditorProfileCatalogProbe)),
                    Is.EqualTo(new[] { oldPath }));

                Assert.That(AssetDatabase.MoveAsset(oldPath, newPath), Is.Empty);
                InjectCachedPaths(typeof(FrameworkEditorProfileCatalogProbe), oldPath);
                int staleRevision = FrameworkEditorProfileCatalog.Revision;

                Assert.That(FrameworkEditorProfileCatalog.TryResolveFirst(
                    out FrameworkEditorProfileCatalogProbe resolved, out IReadOnlyList<string> repairedPaths), Is.True);
                Assert.That(resolved, Is.SameAs(profile));
                Assert.That(AssetDatabase.GetAssetPath(resolved), Is.EqualTo(newPath));
                Assert.That(repairedPaths, Is.EqualTo(new[] { newPath }));
                Assert.That(FrameworkEditorProfileCatalog.Revision, Is.GreaterThan(staleRevision));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                FrameworkEditorProfileCatalog.Invalidate();
            }
        }

        [Test]
        public void ProjectSettingsLocation_ReusesMatchingAssetAndNeverOverwritesOccupiedPath()
        {
            string folderName = "__FrameworkProfileCreationPathTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            string matchingPath = folderPath + "/Matching.asset";
            string otherTypePath = folderPath + "/OtherType.asset";
            string unimportedPath = folderPath + "/Unimported.asset";
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var matching = ScriptableObject.CreateInstance<FrameworkEditorProfileCatalogProbe>();
                AssetDatabase.CreateAsset(matching, matchingPath);
                Assert.That(
                    FrameworkProjectSettingsLocation
                        .GetExistingProfileOrThrow<FrameworkEditorProfileCatalogProbe>(matchingPath),
                    Is.SameAs(matching));

                var other = ScriptableObject.CreateInstance<ServiceInstallerProfile>();
                AssetDatabase.CreateAsset(other, otherTypePath);
                string otherGuid = AssetDatabase.AssetPathToGUID(otherTypePath);
                Assert.That(
                    () => FrameworkProjectSettingsLocation
                        .GetExistingProfileOrThrow<FrameworkEditorProfileCatalogProbe>(otherTypePath),
                    Throws.TypeOf<InvalidOperationException>().And.Message.Contains("不会覆盖"));
                Assert.That(AssetDatabase.AssetPathToGUID(otherTypePath), Is.EqualTo(otherGuid));
                Assert.That(AssetDatabase.LoadAssetAtPath<ServiceInstallerProfile>(otherTypePath),
                    Is.SameAs(other));

                Assert.That(FrameworkProjectPath.TryResolve(
                    unimportedPath, out _, out string unimportedAbsolutePath, out string pathError),
                    Is.True, pathError);
                File.WriteAllText(unimportedAbsolutePath, "do-not-overwrite");
                Assert.That(
                    () => FrameworkProjectSettingsLocation
                        .GetExistingProfileOrThrow<FrameworkEditorProfileCatalogProbe>(unimportedPath),
                    Throws.TypeOf<InvalidOperationException>().And.Message.Contains("不会覆盖"));
                Assert.That(File.ReadAllText(unimportedAbsolutePath), Is.EqualTo("do-not-overwrite"));
                File.Delete(unimportedAbsolutePath);
            }
            finally
            {
                if (FrameworkProjectPath.TryResolve(unimportedPath, out _, out string absolutePath, out _) &&
                    File.Exists(absolutePath))
                    File.Delete(absolutePath);
                AssetDatabase.DeleteAsset(folderPath);
                FrameworkEditorProfileCatalog.Invalidate();
            }
        }

        [Test]
        public void ProjectSettingsLocation_ValidatesPhysicalBoundaryBeforeAnyFolderOrAssetAccess()
        {
            string source = ReadScriptSource(nameof(FrameworkProjectSettingsLocation));
            int firstDirectoryValidation = source.IndexOf("ValidateDirectoryPath();", StringComparison.Ordinal);
            int createSettings = source.IndexOf(
                "EnsureChildFolder(\"Assets\", \"Settings\", root);", StringComparison.Ordinal);
            int createFramework = source.IndexOf(
                "EnsureChildFolder(root, \"SSFramework\", Directory);", StringComparison.Ordinal);
            int lastDirectoryValidation = source.LastIndexOf("ValidateDirectoryPath();", StringComparison.Ordinal);
            int targetValidation = source.IndexOf(
                "FrameworkProjectPath.TryResolveAssetsFile", StringComparison.Ordinal);
            int targetLoad = source.IndexOf(
                "AssetDatabase.LoadMainAssetAtPath(normalizedPath)", StringComparison.Ordinal);

            Assert.That(firstDirectoryValidation, Is.LessThan(createSettings),
                "现存 Settings reparse 必须在首次 CreateFolder 前被拒绝。 ");
            Assert.That(createSettings, Is.LessThan(createFramework));
            Assert.That(lastDirectoryValidation, Is.GreaterThan(createFramework),
                "创建完成后必须再次证明目录仍位于物理工程边界内。 ");
            Assert.That(targetValidation, Is.LessThan(targetLoad),
                "固定目标必须在复用可加载资产前先拒绝 symlink / junction。 ");
        }

        private static void InjectCachedPaths(Type profileType, params string[] paths)
        {
            FieldInfo field = typeof(FrameworkEditorProfileCatalog).GetField(
                "PathsByType", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var cache = (Dictionary<Type, IReadOnlyList<string>>)field.GetValue(null);
            cache[profileType] = Array.AsReadOnly(paths);
        }

        private static string ReadScriptSource(string typeName)
        {
            string[] paths = AssetDatabase.FindAssets(typeName + " t:MonoScript");
            Assert.That(paths, Has.Length.EqualTo(1));
            return AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(paths[0])).text;
        }
    }
}
