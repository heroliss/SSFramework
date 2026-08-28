using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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
                Assert.That(result.message, Does.Contain("包名常量命名空间无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
