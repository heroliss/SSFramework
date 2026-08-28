using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Network.Proto.Editor.Tests
{
    /// <summary>锁定 Protobuf 输出所有权与生成前置检查，避免危险清理及无意义的外部进程启动。</summary>
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
        public void InspectGenerationPrerequisites_RejectsBroadOrEscapingDirectory(string outputPath)
        {
            var profile = CreateProfile("Unsafe", outputPath);
            try
            {
                SetString(profile, "_protocDir", "Temp/ProtoOwnership/Protoc");
                SetString(profile, "_protoDir", "Temp/ProtoOwnership/Proto~");

                var report = ProtoCodeGenerator.InspectGenerationPrerequisites(profile);

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
            var readyClaim = CreateProfile("Ready", "Assets/Generated/Proto/Ready");
            var blank = CreateProfile("Blank", string.Empty);
            var invalid = CreateProfile("Invalid", "Assets/../../Escape");
            try
            {
                var result = ProtoCodeGenerator.ValidateOutputOwnership(
                    new[] { readyClaim, blank, invalid });

                Assert.That(result.ok, Is.True, result.message);
                Assert.That(result.message, Does.Contain("1 套").And.Contain("另有 2 套"));
            }
            finally
            {
                Object.DestroyImmediate(readyClaim);
                Object.DestroyImmediate(blank);
                Object.DestroyImmediate(invalid);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_ReportsEveryMissingField()
        {
            var profile = CreateProfile("Missing", string.Empty);
            try
            {
                SetString(profile, "_protocDir", string.Empty);
                SetString(profile, "_protoDir", string.Empty);

                var report = ProtoCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message,
                    Does.Contain("protoc 工具目录").And.Contain(".proto 源目录").And.Contain("代码输出目录"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_ReportsMissingToolAndSourceTogether()
        {
            string root = "Temp/SSFrameworkProtoReadiness_" + Guid.NewGuid().ToString("N");
            var profile = CreateConfiguredProfile(root);
            try
            {
                var report = ProtoCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("protoc 不存在").And.Contain(".proto 源目录不存在"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteDirectory(root);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsOutputPathOccupiedByFile()
        {
            string root = "Temp/SSFrameworkProtoReadiness_" + Guid.NewGuid().ToString("N");
            string outputPath = "Assets/ProtoOutputFileTest_" + Guid.NewGuid().ToString("N");
            var profile = CreateConfiguredProfile(root);
            try
            {
                SetString(profile, "_outputCodeDir", outputPath);
                File.WriteAllText(ProjectAbsolute(outputPath), "occupied");

                var report = ProtoCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.False);
                Assert.That(report.Message, Does.Contain("目标当前是普通文件"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteDirectory(root);
                string absoluteOutput = ProjectAbsolute(outputPath);
                if (File.Exists(absoluteOutput)) File.Delete(absoluteOutput);
                if (File.Exists(absoluteOutput + ".meta")) File.Delete(absoluteOutput + ".meta");
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_ReturnsSortedRecursiveInputsWhenReady()
        {
            string root = "Temp/SSFrameworkProtoReadiness_" + Guid.NewGuid().ToString("N");
            var profile = CreateConfiguredProfile(root);
            try
            {
                string toolRoot = ProjectAbsolute(root + "/Protoc");
                string protocPath = ProtoCodeGenerator.ResolveProtocPath(toolRoot, string.Empty);
                Directory.CreateDirectory(Path.GetDirectoryName(protocPath)!);
                File.WriteAllText(protocPath, string.Empty);

                string sourceRoot = ProjectAbsolute(root + "/Proto~");
                Directory.CreateDirectory(Path.Combine(sourceRoot, "nested"));
                File.WriteAllText(Path.Combine(sourceRoot, "nested", "b.proto"), "syntax = \"proto3\";");
                File.WriteAllText(Path.Combine(sourceRoot, "a.proto"), "syntax = \"proto3\";");

                var report = ProtoCodeGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(report.CanGenerate, Is.True, report.Message);
                Assert.That(report.ProtoFiles, Is.EqualTo(new[] { "a.proto", "nested/b.proto" }));
                Assert.That(report.OutputAssetPath, Does.StartWith("Assets/Generated/Proto/Readiness_"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteDirectory(root);
            }
        }

        [TestCase(0, true, 0, "共 0 套", "生成全部")]
        [TestCase(2, true, 0, "共 2 套 · 可生成 0 套", "暂无可生成配置")]
        [TestCase(3, true, 2, "共 3 套 · 可生成 2 套", "生成可用配置（2/3）")]
        [TestCase(3, false, 2, "共 3 套 · 输出预检失败，已暂停", "输出冲突，已暂停")]
        public void OverviewSummary_ExplainsBatchScope(
            int profileCount,
            bool ownershipOk,
            int readyCount,
            string expectedSummary,
            string expectedButton)
        {
            Assert.That(
                ProtoConfigOverviewWindow.FormatCountSummary(profileCount, ownershipOk, readyCount),
                Is.EqualTo(expectedSummary));
            Assert.That(
                ProtoConfigOverviewWindow.FormatBatchButtonLabel(profileCount, ownershipOk, readyCount),
                Is.EqualTo(expectedButton));
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

        private static ProtoConfigProfile CreateConfiguredProfile(string root)
        {
            var profile = CreateProfile(
                "Readiness",
                "Assets/Generated/Proto/Readiness_" + Guid.NewGuid().ToString("N"));
            SetString(profile, "_protocDir", root + "/Protoc");
            SetString(profile, "_protoDir", root + "/Proto~");
            return profile;
        }

        private static void SetString(ProtoConfigProfile profile, string propertyName, string value)
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
