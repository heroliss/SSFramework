using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定多套 Luban 配置的代码 / 数据输出目录互斥，确保批量预检发生在 CLI 写盘前。</summary>
    public sealed class LubanOutputOwnershipTests
    {
        [Test]
        public void ValidateOutputOwnership_AcceptsDisjointDirectories()
        {
            var first = CreateProfile("First", "Assets/Generated/Luban/FirstCode", "Assets/Generated/Luban/FirstData");
            var second = CreateProfile("Second", "Assets/Generated/Luban/SecondCode", "Assets/Generated/Luban/SecondData");
            try
            {
                var result = LubanCodeGenerator.ValidateOutputOwnership(new[] { first, second });
                Assert.That(result.ok, Is.True, result.message);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [TestCase("Assets/Generated/Luban/Same", "Assets/Generated/Luban/Same")]
        [TestCase("Assets/Generated/Luban/Code", "Assets/Generated/Luban/Code/Nested")]
        public void ValidateOutputOwnership_RejectsCodeDataOverlap(string codePath, string dataPath)
        {
            var profile = CreateProfile("Overlap", codePath, dataPath);
            try
            {
                var result = LubanCodeGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("所有权冲突"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsCrossProfileOverlap()
        {
            var first = CreateProfile("First", "Assets/Generated/Luban/Shared", "Assets/Generated/Luban/DataA");
            var second = CreateProfile("Second", "Assets/Generated/Luban/Shared/Nested", "Assets/Generated/Luban/DataB");
            try
            {
                var result = LubanCodeGenerator.ValidateOutputOwnership(new[] { first, second });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("First").And.Contain("Second"));
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
        public void ValidateOutputOwnership_RejectsBroadOrEscapingOutput(string unsafePath)
        {
            var profile = CreateProfile("Unsafe", unsafePath, "Assets/Generated/Luban/SafeData");
            try
            {
                var result = LubanCodeGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("输出目录无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static LubanConfigProfile CreateProfile(string name, string codePath, string dataPath)
        {
            var profile = ScriptableObject.CreateInstance<LubanConfigProfile>();
            profile.name = name;
            var serialized = new SerializedObject(profile);
            serialized.FindProperty("_outputCodeDir").stringValue = codePath;
            serialized.FindProperty("_outputDataDir").stringValue = dataPath;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return profile;
        }
    }
}
