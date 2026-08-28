using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定资源构建可以在没有 Boot、HybridCLR 与 dnlib 的工程中独立保留。</summary>
    public sealed class BuildEditorModuleBoundaryTests
    {
        private const string AssemblyDefinitionPath =
            "Assets/Game/Framework/Build/Editor/Game.Framework.Build.Editor.asmdef";

        [Test]
        public void ResourceBuildAssembly_DoesNotReferenceHotUpdateToolchain()
        {
            string[] references = typeof(FrameworkAssetBuilder).Assembly.GetReferencedAssemblies()
                .Select(assembly => assembly.Name)
                .ToArray();

            Assert.That(references, Does.Not.Contain("Game.Framework.Boot"));
            Assert.That(references, Does.Not.Contain("Game.Framework.Build.HybridCLR.Editor"));
            Assert.That(references, Does.Not.Contain("HybridCLR.Editor"));
            Assert.That(references, Does.Not.Contain("dnlib"));
        }

        [Test]
        public void ResourceBuildAsmdef_DeclaresOnlyItsOwnedToolchain()
        {
            AsmdefDeclaration declaration = ReadDeclaration(AssemblyDefinitionPath);

            Assert.That(declaration.references, Is.EquivalentTo(new[]
            {
                "Game.Framework",
                "Game.Framework.Editor",
                "YooAsset",
                "YooAsset.Editor",
            }));
            Assert.That(declaration.precompiledReferences, Is.Empty);
            Assert.That(declaration.autoReferenced, Is.False,
                "可删除的资源构建 Module 不应让 Assembly-CSharp-Editor 获得隐式引用。");
            Assert.That(declaration.overrideReferences, Is.True,
                "资源构建 Module 必须关闭预编译 DLL 的全局 Auto Reference。");
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
