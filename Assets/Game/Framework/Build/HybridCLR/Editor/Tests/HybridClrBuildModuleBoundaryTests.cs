using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定热更新构建是资源构建的可删除下游，并验证既有 Profile 在程序集迁移后仍可加载。</summary>
    public sealed class HybridClrBuildModuleBoundaryTests
    {
        private const string AssemblyDefinitionPath =
            "Assets/Game/Framework/Build/HybridCLR/Editor/Game.Framework.Build.HybridCLR.Editor.asmdef";
        private const string ExistingProfilePath = "Assets/Game/Settings/FrameworkHotUpdateProfile.asset";
        private const string ProfileScriptGuid = "879c7d85708f1fe45a4c19bb4f116929";

        [Test]
        public void HybridClrBuildAssembly_ReferencesRequiredDownstreamToolchain()
        {
            string[] references = typeof(FrameworkHotUpdateBuilder).Assembly.GetReferencedAssemblies()
                .Select(assembly => assembly.Name)
                .ToArray();

            Assert.That(references, Does.Contain("Game.Framework.Build.Editor"));
            Assert.That(references, Does.Contain("Game.Framework.Boot"));
            Assert.That(references, Does.Contain("HybridCLR.Editor"));
            Assert.That(references, Does.Contain("dnlib"));
        }

        [Test]
        public void HybridClrBuildAsmdef_DeclaresOneWayDownstreamDependencies()
        {
            AsmdefDeclaration declaration = ReadDeclaration(AssemblyDefinitionPath);

            Assert.That(declaration.references, Is.EquivalentTo(new[]
            {
                "Game.Framework",
                "Game.Framework.Editor",
                "Game.Framework.Boot",
                "Game.Framework.Build.Editor",
                "YooAsset",
                "YooAsset.Editor",
                "HybridCLR.Editor",
            }));
            Assert.That(declaration.precompiledReferences, Is.EquivalentTo(new[] { "dnlib.dll" }));
            Assert.That(declaration.autoReferenced, Is.False,
                "可删除的热更新构建 Module 不应让 Assembly-CSharp-Editor 获得隐式引用。");
            Assert.That(declaration.overrideReferences, Is.True,
                "热更新构建 Module 必须显式声明自己的预编译 DLL 闭包。");
        }

        [Test]
        public void ExistingHotUpdateProfiles_RemainLoadableAfterAssemblySplit()
        {
            string[] paths = AssetDatabase.FindAssets("t:" + nameof(FrameworkHotUpdateProfile))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.That(paths, Is.Not.Empty,
                "工程中已有热更新 Profile，却无法按类型检索；程序集迁移兼容性门禁不能空跑。");
            Assert.That(paths, Does.Contain(ExistingProfilePath),
                $"既有热更新 Profile 未被新程序集识别：{ExistingProfilePath}");
            Assert.That(AssetDatabase.GUIDToAssetPath(ProfileScriptGuid),
                Is.EqualTo("Assets/Game/Framework/Build/HybridCLR/Editor/FrameworkHotUpdateProfile.cs"),
                "移动脚本时必须保留 MonoScript GUID，否则既有 Profile 会丢失类型。");

            foreach (string path in paths)
            {
                FrameworkHotUpdateProfile profile =
                    AssetDatabase.LoadAssetAtPath<FrameworkHotUpdateProfile>(path);
                Assert.That(profile, Is.Not.Null,
                    $"热更新 Profile 无法加载：{path}。请检查脚本 GUID 与程序集迁移兼容性。");
                Assert.That(profile.GetType().Assembly.GetName().Name,
                    Is.EqualTo("Game.Framework.Build.HybridCLR.Editor"),
                    $"热更新 Profile 仍来自旧程序集：{path}");
            }
        }

        private static AsmdefDeclaration ReadDeclaration(string assetPath)
        {
            AssemblyDefinitionAsset asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(assetPath);
            Assert.That(asset, Is.Not.Null, $"找不到程序集定义：{assetPath}");
            return JsonUtility.FromJson<AsmdefDeclaration>(asset.text);
        }

        [Serializable]
        private sealed class AsmdefDeclaration
        {
            public string[] references = Array.Empty<string>();
            public string[] precompiledReferences = Array.Empty<string>();
            public bool overrideReferences;
            public bool autoReferenced = true;
        }
    }
}
