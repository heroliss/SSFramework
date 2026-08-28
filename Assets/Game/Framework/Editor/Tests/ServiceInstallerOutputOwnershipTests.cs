using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定服务安装器的输出所有权、逐条失败隔离与人工按钮就绪契约。</summary>
    public sealed class ServiceInstallerOutputOwnershipTests
    {
        [Test]
        public void GenerateEntry_NullEntryReturnsStructuredFailure()
        {
            var result = ServiceInstallerGenerator.GenerateEntry(null);
            Assert.That(result.ok, Is.False);
            Assert.That(result.message, Does.Contain("不能为空"));
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsEmptyProfile()
        {
            var profile = ScriptableObject.CreateInstance<ServiceInstallerProfile>();
            try
            {
                var result = ServiceInstallerGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(result.CanGenerate, Is.False);
                Assert.That(result.Message, Does.Contain("没有任何安装器条目"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsMissingNamespace()
        {
            var profile = CreateProfileWithEntry(namespaceName: string.Empty, includeScanFolder: true);
            try
            {
                var result = ServiceInstallerGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(result.CanGenerate, Is.False);
                Assert.That(result.Message, Does.Contain("命名空间"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsInvalidNamespace()
        {
            var profile = CreateProfileWithEntry(namespaceName: "Bad Namespace", includeScanFolder: true);
            try
            {
                var result = ServiceInstallerGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(result.CanGenerate, Is.False);
                Assert.That(result.Message, Does.Contain("命名空间无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_RejectsMissingScanFolder()
        {
            var profile = CreateProfileWithEntry(namespaceName: "Game.Generated", includeScanFolder: false);
            try
            {
                var result = ServiceInstallerGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(result.CanGenerate, Is.False);
                Assert.That(result.Message, Does.Contain("扫描目录"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_AcceptsCompleteEntry()
        {
            var profile = CreateProfileWithEntry(namespaceName: "Game.Generated", includeScanFolder: true);
            try
            {
                var result = ServiceInstallerGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(result.CanGenerate, Is.True, result.Message);
                Assert.That(result.ReadyEntryCount, Is.EqualTo(1));
                Assert.That(result.TotalEntryCount, Is.EqualTo(1));
                Assert.That(result.HasInvalidEntries, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InspectGenerationPrerequisites_PreservesReadyEntriesWhenAnotherEntryIsInvalid()
        {
            var profile = ScriptableObject.CreateInstance<ServiceInstallerProfile>();
            profile.name = "InstallerPartialPrerequisiteTest";
            profile.Installers.Add(CreateEntry(
                "Assets/Generated/Installers/Ready.g.cs", "Game.Generated", includeScanFolder: true));
            profile.Installers.Add(CreateEntry(
                "Assets/Generated/Installers/MissingNamespace.g.cs", string.Empty, includeScanFolder: true));
            try
            {
                var result = ServiceInstallerGenerator.InspectGenerationPrerequisites(profile);

                Assert.That(result.CanGenerate, Is.True, result.Message);
                Assert.That(result.ReadyEntryCount, Is.EqualTo(1));
                Assert.That(result.TotalEntryCount, Is.EqualTo(2));
                Assert.That(result.HasInvalidEntries, Is.True);
                Assert.That(result.Message, Does.Contain("1/2"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void GenerateEntriesIndependently_ContinuesAfterKnownFailureAndUnexpectedException()
        {
            var first = new ServiceInstallerProfile.InstallerEntry { OutputPath = "First.g.cs" };
            var knownFailure = new ServiceInstallerProfile.InstallerEntry { OutputPath = "Known.g.cs" };
            var unexpectedFailure = new ServiceInstallerProfile.InstallerEntry { OutputPath = "Unexpected.g.cs" };
            var last = new ServiceInstallerProfile.InstallerEntry { OutputPath = "Last.g.cs" };
            var visited = new List<string>();

            var result = ServiceInstallerGenerator.GenerateEntriesIndependently(
                new[] { first, knownFailure, unexpectedFailure, last },
                entry =>
                {
                    visited.Add(entry.OutputPath);
                    if (ReferenceEquals(entry, knownFailure)) return (false, "已知配置失败");
                    if (ReferenceEquals(entry, unexpectedFailure))
                        throw new System.InvalidOperationException("模拟反射失败");
                    return (true, entry.OutputPath + " 已生成");
                });

            Assert.That(result.ok, Is.False);
            Assert.That(visited, Is.EqualTo(new[]
            {
                "First.g.cs", "Known.g.cs", "Unexpected.g.cs", "Last.g.cs",
            }));
            Assert.That(result.message, Does.Contain("已知配置失败"));
            Assert.That(result.message, Does.Contain("InvalidOperationException"));
            Assert.That(result.message, Does.Contain("模拟反射失败"));
            Assert.That(result.message, Does.Contain("Last.g.cs 已生成"),
                "前一条抛异常后仍必须继续生成后续独立输出。");
        }

        [TestCase(true, true, 1, true)]
        [TestCase(false, true, 1, false)]
        [TestCase(true, false, 1, false)]
        [TestCase(true, true, 0, false)]
        public void GenerationActionAvailability_RequiresGateOwnershipAndReadyWork(
            bool canWrite,
            bool ownershipOk,
            int readyWorkCount,
            bool expected)
        {
            Assert.That(
                ServiceInstallerGenerator.CanStartGenerationAction(
                    canWrite, ownershipOk, readyWorkCount),
                Is.EqualTo(expected));
        }

        [TestCase(
            "ServiceInstallerOverviewWindow.cs",
            "private void OnGUI()",
            "internal static string FormatCountSummary")]
        [TestCase(
            "ServiceInstallerMenu.cs",
            "public override void OnInspectorGUI()",
            null)]
        public void InstallerEditors_ConsumeSharedActionAvailabilityInTheirButtonMember(
            string fileName,
            string memberMarker,
            string nextMemberMarker)
        {
            FrameworkModuleSourceCatalog.SourceLocation source =
                FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                    fileName, "Game.Framework.Editor");
            string content = File.ReadAllText(source.PhysicalPath);
            int memberStart = content.IndexOf(memberMarker, System.StringComparison.Ordinal);
            int memberEnd = string.IsNullOrEmpty(nextMemberMarker)
                ? content.Length
                : content.IndexOf(nextMemberMarker, memberStart, System.StringComparison.Ordinal);

            Assert.That(memberStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(memberEnd, Is.GreaterThan(memberStart));
            string memberSource = content.Substring(memberStart, memberEnd - memberStart);
            Assert.That(memberSource, Does.Contain("CanStartGenerationAction"),
                $"{fileName} 的人工按钮区域必须消费 Gate、所有权与就绪数量的共同 evaluator。");
            Assert.That(memberSource, Does.Contain("canWrite, ownershipOk"));
        }

        [TestCase(0, false, 0, "共 0 份")]
        [TestCase(2, false, 1, "共 2 份 · 输出预检失败，已暂停")]
        [TestCase(2, true, 1, "共 2 份 · 可生成 1 份")]
        public void OverviewCountSummary_DistinguishesEmptyOwnershipFailureAndReadiness(
            int profileCount,
            bool ownershipOk,
            int readyCount,
            string expected)
        {
            Assert.That(
                ServiceInstallerOverviewWindow.FormatCountSummary(
                    profileCount, ownershipOk, readyCount),
                Is.EqualTo(expected));
        }

        [Test]
        public void ValidateOutputOwnership_AcceptsUniqueFilesInSameDirectory()
        {
            var profile = CreateProfile(
                "Assets/Generated/Installers/First.g.cs",
                "Assets/Generated/Installers/Second.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.True, result.message);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsNormalizedDuplicateFiles()
        {
            var profile = CreateProfile(
                "Assets/Generated/Installers/Duplicate.g.cs",
                "Assets/Generated/Other/../Installers/Duplicate.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("所有权冲突"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsCaseOnlyDuplicateFiles()
        {
            var profile = CreateProfile(
                "Assets/Generated/Installers/Case.g.cs",
                "Assets/Generated/installers/case.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("所有权冲突"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ValidateOutputOwnership_RejectsEscapingFile()
        {
            var profile = CreateProfile("Assets/../../Installer.g.cs");
            try
            {
                var result = ServiceInstallerGenerator.ValidateOutputOwnership(new[] { profile });
                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("输出路径无效"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static ServiceInstallerProfile CreateProfile(params string[] outputPaths)
        {
            var profile = ScriptableObject.CreateInstance<ServiceInstallerProfile>();
            profile.name = "InstallerOwnershipTest";
            foreach (string path in outputPaths)
                profile.Installers.Add(new ServiceInstallerProfile.InstallerEntry { OutputPath = path });
            return profile;
        }

        private static ServiceInstallerProfile CreateProfileWithEntry(
            string namespaceName,
            bool includeScanFolder)
        {
            var profile = ScriptableObject.CreateInstance<ServiceInstallerProfile>();
            profile.name = "InstallerPrerequisiteTest";
            profile.Installers.Add(CreateEntry(
                "Assets/Generated/Installers/Ready.g.cs", namespaceName, includeScanFolder));
            return profile;
        }

        private static ServiceInstallerProfile.InstallerEntry CreateEntry(
            string outputPath,
            string namespaceName,
            bool includeScanFolder)
        {
            var entry = new ServiceInstallerProfile.InstallerEntry
            {
                OutputPath = outputPath,
                Namespace = namespaceName,
            };
            if (includeScanFolder)
            {
                FrameworkModuleSourceCatalog.SourceLocation source =
                    FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                        "ServiceInstallerProfile.cs", "Game.Framework.Editor");
                DefaultAsset folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(source.AssetDirectory);
                Assert.That(folder, Is.Not.Null,
                    "测试需要当前安装形态下 ServiceInstallerProfile.cs 所在的文件夹资产。");
                entry.ScanFolders.Add(folder);
            }
            return entry;
        }
    }
}
