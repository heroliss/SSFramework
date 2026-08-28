using System;
using System.IO;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor.Tests
{
    /// <summary>锁定 UI 代码生成器不猜测业务命名空间与目录的项目无关契约。</summary>
    public sealed class UICodeGenProjectAgnosticTests
    {
        [Test]
        public void NewProfile_LeavesProjectSpecificTargetsEmpty()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            try
            {
                Assert.That(profile.NamespaceRoot, Is.Empty);
                Assert.That(profile.OutputCodeDir, Is.Empty);
                Assert.That(profile.GeneratedCodeDir, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void MissingProfile_ReadonlyPreviewUsesNewProfileDefaultsWithoutAssetCreation()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            try
            {
                var entry = new UIBindingEntry
                {
                    Path = "Confirm",
                    ComponentTypes = { typeof(GameObject).AssemblyQualifiedName },
                };

                string withProfile = UIBindingUtil.EffectiveFieldName(entry, typeof(GameObject), 1, "Root", profile);
                string withoutProfile = UIBindingUtil.EffectiveFieldName(entry, typeof(GameObject), 1, "Root", null);

                Assert.That(withoutProfile, Is.EqualTo(withProfile));
                Assert.That(UIBindingUtil.ResolveClassName("Assets/UI/LoginPanel.prefab", null, null),
                    Is.EqualTo("LoginPanel"));
                Assert.That(UIBindingUtil.ResolveNamespace("Assets/UI/LoginPanel.prefab", null, null), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ExistingProfile_BlankFieldTemplate_PreservesLegacyNodeOnlyFallback()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("_fieldNameTemplate").stringValue = " ";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(profile.FieldNameTemplate, Is.EqualTo("{node}"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Generate_WithUnconfiguredTargets_FailsBeforeWritingFiles()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            var root = new GameObject("BindingRoot");
            try
            {
                var data = root.AddComponent<UIBindingData>();
                data.Entries.Add(new UIBindingEntry
                {
                    Node = root.transform,
                    Path = string.Empty,
                    ComponentTypes = { typeof(GameObject).AssemblyQualifiedName },
                });

                var result = UIBindingCodeGenerator.Generate(
                    "Assets/DoesNotNeedToExist/BindingRoot.prefab",
                    data,
                    profile);

                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("命名空间"));
                Assert.That(result.message, Does.Contain("逻辑目录"));
                Assert.That(result.message, Does.Contain("生成目录"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ResolveNames_WithReservedKeywords_ProducesLegalIdentifiers()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            var root = new GameObject("BindingRoot");
            try
            {
                var data = root.AddComponent<UIBindingData>();
                data.NamespaceOverride = "class.event";
                data.FileNameOverride = "class";
                var entry = new UIBindingEntry
                {
                    Node = root.transform,
                    Path = string.Empty,
                    FieldName = "namespace",
                    ComponentTypes = { typeof(GameObject).AssemblyQualifiedName },
                };

                string resolvedNamespace = UIBindingUtil.ResolveNamespace(
                    "Assets/DoesNotNeedToExist/BindingRoot.prefab", data, profile);

                Assert.That(resolvedNamespace, Is.EqualTo("_class._event"));
                Assert.That(UIBindingUtil.ResolveClassName(
                    "Assets/DoesNotNeedToExist/BindingRoot.prefab", data, profile), Is.EqualTo("_class"));
                Assert.That(UIBindingUtil.EffectiveFieldName(
                    entry, typeof(GameObject), 1, root.name, profile), Is.EqualTo("_namespace"));
                Assert.That(
                    FrameworkCSharpSyntax.TryValidateNamespace(resolvedNamespace, out string error),
                    Is.True,
                    error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Generate_WithTraversalTarget_RejectsPathWithoutCreatingExternalDirectory()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            var root = new GameObject("BindingRoot");
            string escapeName = "SSFrameworkUICodeGenEscape_" + Guid.NewGuid().ToString("N");
            string maliciousTarget = "Assets/../../" + escapeName;
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string escapedAbsolutePath = Path.GetFullPath(Path.Combine(projectRoot, maliciousTarget));
            try
            {
                var data = root.AddComponent<UIBindingData>();
                data.NamespaceOverride = "Tests.Generated";
                data.OutputDirOverride = maliciousTarget;
                data.GeneratedDirOverride = maliciousTarget;
                data.Entries.Add(new UIBindingEntry
                {
                    Node = root.transform,
                    Path = string.Empty,
                    ComponentTypes = { typeof(GameObject).AssemblyQualifiedName },
                });

                var result = UIBindingCodeGenerator.Generate(
                    "Assets/DoesNotNeedToExist/BindingRoot.prefab",
                    data,
                    profile);

                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("目录无效").And.Contain("路径越过了工程根目录"));
                Assert.That(Directory.Exists(escapedAbsolutePath), Is.False,
                    "非法路径必须在创建目录或写文件前被拒绝。");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Generate_WithOutputAncestorOccupiedByFile_FailsBeforeWriting()
        {
            var profile = ScriptableObject.CreateInstance<UICodeGenProfile>();
            var root = new GameObject("BindingRoot");
            string blockingAssetPath =
                "Assets/UIBindingAncestorFile_" + Guid.NewGuid().ToString("N");
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string blockingAbsolutePath = Path.GetFullPath(Path.Combine(projectRoot, blockingAssetPath));
            try
            {
                File.WriteAllText(blockingAbsolutePath, "occupied");
                var data = root.AddComponent<UIBindingData>();
                data.NamespaceOverride = "Tests.Generated";
                data.OutputDirOverride = blockingAssetPath + "/Logic";
                data.GeneratedDirOverride = blockingAssetPath + "/Generated";
                data.Entries.Add(new UIBindingEntry
                {
                    Node = root.transform,
                    Path = string.Empty,
                    ComponentTypes = { typeof(GameObject).AssemblyQualifiedName },
                });

                var result = UIBindingCodeGenerator.Generate(
                    "Assets/DoesNotNeedToExist/BindingRoot.prefab",
                    data,
                    profile);

                Assert.That(result.ok, Is.False);
                Assert.That(result.message,
                    Does.Contain("父级已被普通文件占用").And.Contain(blockingAssetPath));
                Assert.That(Directory.Exists(blockingAbsolutePath + "/Logic"), Is.False);
                Assert.That(Directory.Exists(blockingAbsolutePath + "/Generated"), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
                if (File.Exists(blockingAbsolutePath)) File.Delete(blockingAbsolutePath);
                if (File.Exists(blockingAbsolutePath + ".meta")) File.Delete(blockingAbsolutePath + ".meta");
            }
        }

        [Test]
        public void NativeFolderPicker_StoresPortableAssetPaths()
        {
            Assert.That(UICodeGenEditorGUI.TryToAssetPath(Application.dataPath, out string assetsRoot), Is.True);
            Assert.That(assetsRoot, Is.EqualTo("Assets"));

            string frameworkFolder = Path.Combine(Application.dataPath, "Game", "Framework");
            Assert.That(UICodeGenEditorGUI.TryToAssetPath(frameworkFolder, out string frameworkPath), Is.True);
            Assert.That(frameworkPath, Is.EqualTo("Assets/Game/Framework"));
        }
    }
}
