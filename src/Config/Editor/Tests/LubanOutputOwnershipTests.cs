using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Config.Editor.Tests
{
    /// <summary>锁定 Luban 输出所有权与生成前置检查，确保批量预检发生在 CLI 写盘前。</summary>
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
        public void InspectGenerationPrerequisites_RejectsBroadOrEscapingOutput(string unsafePath)
        {
            var profile = CreateProfile("Unsafe", unsafePath, "Assets/Generated/Luban/SafeData");
            try
            {
                SetString(profile, "_confPath", "Temp/LubanOwnership/luban.conf");

                var report = LubanCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("输出目录无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_IgnoresProfilesWithoutResolvableClaims()
        {
            var valid = CreateProfile(
                "Valid",
                "Assets/Generated/Luban/ValidCode",
                "Assets/Generated/Luban/ValidData");
            var blank = CreateProfile("Blank", string.Empty, string.Empty);
            var invalid = CreateProfile("Invalid", "Assets/../../Escape", string.Empty);
            try
            {
                var result = LubanCodeGenerator.ValidateOutputOwnership(
                    new[] { valid, blank, invalid });

                Assert.That(result.ok, Is.True, result.message);
                Assert.That(result.message, Does.Contain("2 项").And.Contain("另有 4 项"));
            }
            finally
            {
                Object.DestroyImmediate(valid);
                Object.DestroyImmediate(blank);
                Object.DestroyImmediate(invalid);
            }
        }

        [Test]
        public void ValidateOutputOwnership_PreservesValidClaimFromPartiallyConfiguredProfile()
        {
            var partial = CreateProfile(
                "Partial",
                "Assets/Generated/Luban/Shared",
                string.Empty);
            var other = CreateProfile(
                "Other",
                "Assets/Generated/Luban/OtherCode",
                "Assets/Generated/Luban/Shared/Nested");
            try
            {
                var result = LubanCodeGenerator.ValidateOutputOwnership(new[] { partial, other });

                Assert.That(result.ok, Is.False);
                Assert.That(result.message,
                    Does.Contain("所有权冲突").And.Contain("Partial").And.Contain("Other"));
            }
            finally
            {
                Object.DestroyImmediate(partial);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_ReportsEveryMissingField()
        {
            var profile = CreateProfile("Missing", string.Empty, string.Empty);
            try
            {
                SetString(profile, "_lubanToolPath", string.Empty);
                SetString(profile, "_confPath", string.Empty);
                SetString(profile, "_target", string.Empty);

                var report = LubanCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message,
                    Does.Contain("Luban CLI 路径")
                        .And.Contain("luban.conf 路径")
                        .And.Contain("代码输出目录")
                        .And.Contain("数据输出目录")
                        .And.Contain("生成目标"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_ReportsMissingToolAndConfTogether()
        {
            string root = "Temp/SSFrameworkLubanReadiness_" + Guid.NewGuid().ToString("N");
            var profile = CreateConfiguredProfile(root);
            try
            {
                var report = LubanCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("Luban CLI 不存在").And.Contain("luban.conf 不存在"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteDirectory(root);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsInvalidManifestNamespace()
        {
            string root = "Temp/SSFrameworkLubanReadiness_" + Guid.NewGuid().ToString("N");
            var profile = CreateConfiguredProfile(root);
            try
            {
                SetString(profile, "_manifestNamespace", "Bad Namespace");

                var report = LubanCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("清单命名空间无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteDirectory(root);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_ReturnsResolvedPathsWhenReady()
        {
            string root = "Temp/SSFrameworkLubanReadiness_" + Guid.NewGuid().ToString("N");
            var profile = CreateConfiguredProfile(root);
            try
            {
                string toolPath = ProjectAbsolute(root + "/Luban/Luban.exe");
                string confPath = ProjectAbsolute(root + "/Config~/luban.conf");
                Directory.CreateDirectory(Path.GetDirectoryName(toolPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(confPath)!);
                File.WriteAllText(toolPath, string.Empty);
                File.WriteAllText(confPath, "{}\n");

                var report = LubanCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.True, report.Message);
                Assert.That(report.ToolPath, Is.EqualTo(toolPath));
                Assert.That(report.ConfPath, Is.EqualTo(confPath));
                Assert.That(report.OutputCodeAssetPath, Does.StartWith("Assets/Generated/Luban/ReadinessCode_"));
                Assert.That(report.OutputDataAssetPath, Does.StartWith("Assets/Generated/Luban/ReadinessData_"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteDirectory(root);
            }
        }

        [TestCase(0, true, 0, "共 0 套", "生成全部")]
        [TestCase(2, true, 0, "共 2 套 · 可生成 0 套", "暂无可生成配置")]
        [TestCase(3, true, 1, "共 3 套 · 可生成 1 套", "生成可用配置（1/3）")]
        [TestCase(2, false, 1, "共 2 套 · 输出预检失败，已暂停", "输出冲突，已暂停")]
        public void OverviewSummary_ExplainsBatchScope(
            int profileCount,
            bool ownershipOk,
            int readyCount,
            string expectedSummary,
            string expectedButton)
        {
            Assert.That(
                LubanConfigOverviewWindow.FormatCountSummary(profileCount, ownershipOk, readyCount),
                Is.EqualTo(expectedSummary));
            Assert.That(
                LubanConfigOverviewWindow.FormatBatchButtonLabel(profileCount, ownershipOk, readyCount),
                Is.EqualTo(expectedButton));
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

        private static LubanConfigProfile CreateConfiguredProfile(string root)
        {
            var profile = CreateProfile(
                "Readiness",
                "Assets/Generated/Luban/ReadinessCode_" + Guid.NewGuid().ToString("N"),
                "Assets/Generated/Luban/ReadinessData_" + Guid.NewGuid().ToString("N"));
            SetString(profile, "_lubanToolPath", root + "/Luban/Luban.exe");
            SetString(profile, "_confPath", root + "/Config~/luban.conf");
            return profile;
        }

        private static void SetString(LubanConfigProfile profile, string propertyName, string value)
        {
            var serialized = new SerializedObject(profile);
            serialized.FindProperty(propertyName).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string ProjectAbsolute(string relativePath) =>
            Path.GetFullPath(Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        private static void DeleteDirectory(string relativePath)
        {
            string absolutePath = ProjectAbsolute(relativePath);
            if (Directory.Exists(absolutePath)) Directory.Delete(absolutePath, true);
        }
    }
}
