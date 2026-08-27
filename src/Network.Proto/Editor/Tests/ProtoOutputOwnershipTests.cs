using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Network.Proto.Editor.Tests
{
    /// <summary>锁定每套 Protobuf 配置独占输出目录，避免差量清理删除另一套配置的产物。</summary>
    public sealed class ProtoOutputOwnershipTests
    {
        [Test]
        public void ValidateOutputOwnership_AcceptsDisjointAssetsDirectories()
        {
            var first = CreateProfile("First", "Assets/Generated/Proto/First");
            var second = CreateProfile("Second", "Assets/Generated/Proto/Second");
            try
            {
                var result = ProtoCodeGenerator.ValidateOutputOwnership(new[] { first, second });
                Assert.That(result.ok, Is.True, result.message);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [TestCase("Assets/Generated/Proto", "Assets/Generated/Proto")]
        [TestCase("Assets/Generated/Proto", "Assets/Generated/Proto/Nested")]
        public void ValidateOutputOwnership_RejectsSameOrNestedDirectories(string firstPath, string secondPath)
        {
            var first = CreateProfile("First", firstPath);
            var second = CreateProfile("Second", secondPath);
            try
            {
                var result = ProtoCodeGenerator.ValidateOutputOwnership(new[] { first, second });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("所有权冲突"));
                Assert.That(result.message, Does.Contain("递归清理"));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [TestCase("Assets")]
        [TestCase("Assets/../../Escape")]
        [TestCase("ProjectSettings/Generated")]
        public void ValidateOutputOwnership_RejectsBroadOrEscapingDirectory(string outputPath)
        {
            var profile = CreateProfile("Unsafe", outputPath);
            try
            {
                var result = ProtoCodeGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("输出目录无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static ProtoConfigProfile CreateProfile(string name, string outputPath)
        {
            var profile = ScriptableObject.CreateInstance<ProtoConfigProfile>();
            profile.name = name;
            var serialized = new SerializedObject(profile);
            serialized.FindProperty("_outputCodeDir").stringValue = outputPath;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return profile;
        }
    }
}
