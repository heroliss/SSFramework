using System;
using System.Collections.Generic;
using Game.Framework;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定包名常量生成在读取收集器或写文件前拒绝非法 C# 命名空间。</summary>
    public sealed class AssetPackageConstantsGeneratorTests
    {
        [Test]
        public void Generate_InvalidNamespaceFailsBeforeReadingPackagesOrWritingOutput()
        {
            var profile = ScriptableObject.CreateInstance<FrameworkAssetBuildProfile>();
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("_packageConstantsPath").stringValue =
                    "Assets/Generated/Tests/AssetPackages.g.cs";
                serialized.FindProperty("_packageConstantsNamespace").stringValue = "Bad Namespace";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var result = AssetPackageConstantsGenerator.Generate(profile);

                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("包名与构建常量命名空间无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void GeneratedSource_EmitsAssetBundleOffsetWithoutApplyingItToRawPackage()
        {
            var profile = CreateProfile("Assets/Generated/Tests/AssetPackages.g.cs", "Game.Generated.Tests", 64);
            try
            {
                var result = AssetPackageConstantsGenerator.RenderForTests(
                    profile,
                    profile.PackageConstantsPath,
                    profile.PackageConstantsNamespace,
                    new List<(string Name, string Desc)>
                    {
                        ("MainPackage", "ordinary assets"),
                        ("RawCodePackage", "raw files"),
                    });

                Assert.That(result.ok, Is.True, result.error);
                string source = result.content;
                Assert.That(source, Does.Contain("public const ulong AssetBundleFileOffset = 64UL;"));
                Assert.That(source, Does.Contain("public const string RawCodePackage = \"RawCodePackage\";"),
                    "RawFile 包仍应拥有包名常量。");
                Assert.That(source, Does.Contain("不适用于 RawFile / CodePackage"),
                    "生成物必须明确普通 AssetBundle offset 不属于代码包格式。");
                Assert.That(source, Does.Not.Contain("RawCodePackageFileOffset"),
                    "不能把普通资源偏移隐式派生成代码包解密配置。");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void RenderedSource_ChangesWhenProfileOffsetChanges_AndAlwaysUsesLf()
        {
            var profile = CreateProfile("Assets/Generated/Tests/AssetPackages.g.cs", "Game.Generated.Tests", 16);
            var packages = new List<(string Name, string Desc)> { ("MainPackage", "main") };
            try
            {
                var before = AssetPackageConstantsGenerator.RenderForTests(
                    profile, profile.PackageConstantsPath, profile.PackageConstantsNamespace, packages);

                SetFileOffset(profile, 32);
                var after = AssetPackageConstantsGenerator.RenderForTests(
                    profile, profile.PackageConstantsPath, profile.PackageConstantsNamespace, packages);

                Assert.That(before.ok, Is.True, before.error);
                Assert.That(after.ok, Is.True, after.error);
                Assert.That(before.content, Does.Contain("AssetBundleFileOffset = 16UL"));
                Assert.That(after.content, Does.Contain("AssetBundleFileOffset = 32UL"));
                Assert.That(after.content, Is.Not.EqualTo(before.content));
                Assert.That(after.content, Does.Not.Contain("\r"),
                    "生成代码必须固定为 LF，不能让 Windows checkout 永久误报 freshness 失败。");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void BuildPreflight_OnlyOrdinaryBundleConsumesGeneratedOffsetConstant()
        {
            Assert.That(
                FrameworkAssetBuilder.RequiresGeneratedAssetBundleConstants(
                    new[] { "RawFiles" }, _ => false, name => name == "RawFiles"),
                Is.False,
                "RawFile 包由独立构建 Module 拥有，不能读取普通 AssetBundle 的生成 offset。");
            Assert.That(
                FrameworkAssetBuilder.RequiresGeneratedAssetBundleConstants(
                    new[] { "Empty", "Main" }, name => name == "Empty", _ => false),
                Is.True,
                "普通 AssetBundle 构建必须校验引导期生成常量的新鲜度。");
        }

        [Test]
        public void BuiltInFileOffset_RejectsUnreasonableSizeAndSupportsWebMemoryDecryption()
        {
            var profile = CreateProfile("Assets/Generated/Tests/AssetPackages.g.cs", "Game.Generated.Tests", 1);
            try
            {
                Assert.That(
                    FrameworkAssetBuilder.ValidateBuiltInFileOffset(profile, false),
                    Is.Null,
                    "Web Adapter 已向 WebServer / WebNetwork 文件系统注入内存解密器。");
                Assert.That(
                    FrameworkAssetBuilder.ValidateBuiltInFileOffset(profile, false),
                    Is.Null);

                SetFileOffset(profile, AssetProviderConfig.MaxBuiltInFileOffset + 1UL);
                Assert.That(
                    FrameworkAssetBuilder.ValidateBuiltInFileOffset(profile, false),
                    Does.Contain(AssetProviderConfig.MaxBuiltInFileOffset.ToString()));
                Assert.That(
                    FrameworkAssetBuilder.ValidateBuiltInFileOffset(profile, true),
                    Is.Null,
                    "项目自定义加密器接管时，内置 FileOffset 不参与本次构建。");
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new GameBundleOffsetEncryptor(AssetProviderConfig.MaxBuiltInFileOffset + 1UL));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static FrameworkAssetBuildProfile CreateProfile(string path, string ns, ulong fileOffset)
        {
            var profile = ScriptableObject.CreateInstance<FrameworkAssetBuildProfile>();
            var serialized = new SerializedObject(profile);
            serialized.FindProperty("_packageConstantsPath").stringValue = path;
            serialized.FindProperty("_packageConstantsNamespace").stringValue = ns;
            serialized.FindProperty("_fileOffset").ulongValue = fileOffset;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return profile;
        }

        private static void SetFileOffset(FrameworkAssetBuildProfile profile, ulong value)
        {
            var serialized = new SerializedObject(profile);
            serialized.FindProperty("_fileOffset").ulongValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
