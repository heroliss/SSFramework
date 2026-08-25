using System;
using System.IO;
using NUnit.Framework;
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
                Assert.That(result.message, Does.Contain("必须是 Assets/ 下"));
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
