using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor.Tests
{
    /// <summary>
    /// 锁定隔离构建探针的组合来源、依赖最小化、删除复制规则与窄窗结构；真实 Player Build 留给显式慢任务。
    /// </summary>
    public sealed class FrameworkBuildSizeProbeTests
    {
        private const string Manifest = @"{
  ""dependencies"": {
    ""com.cysharp.r3"": ""r3-version"",
    ""com.cysharp.unitask"": ""unitask-version"",
    ""com.tuyoogame.yooasset"": ""3.0.5"",
    ""com.unity.inputsystem"": ""1.20.0"",
    ""com.unity.ugui"": ""2.0.0"",
    ""com.unity.entities"": ""1.4.8"",
    ""com.unity.modules.audio"": ""1.0.0"",
    ""com.unity.modules.ui"": ""1.0.0""
  },
  ""scopedRegistries"": [
    {
      ""name"": ""example.registry"",
      ""url"": ""https://packages.example.invalid"",
      ""scopes"": [""com.example""]
    }
  ]
}";

        [Test]
        public void Plans_ReuseAuditProfilesAndRuntimeClosures()
        {
            var plans = FrameworkBuildSizeProbe.CreatePlans();

            Assert.That(plans.Take(4).Select(plan => plan.Key),
                Is.EqualTo(new[] { "core", "ugui", "toolkit", "full" }));
            Assert.That(plans.Skip(4), Is.Not.Empty);
            Assert.That(plans.Skip(4).All(plan => plan.IsAdvanced), Is.True);
            Assert.That(plans.Where(plan => plan.IsAdvanced).SelectMany(plan => plan.RootAssemblies),
                Does.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "core").Assemblies,
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
            Assert.That(plans.Single(plan => plan.Key == "ugui").Assemblies,
                Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "ugui").Assemblies,
                Does.Not.Contain(FrameworkModuleAudit.ToolkitAssemblyName));
            Assert.That(plans.Single(plan => plan.Key == "toolkit").Assemblies,
                Does.Not.Contain(FrameworkModuleAudit.UGuiAssemblyName));
            Assert.That(plans.SelectMany(plan => plan.Sources).Select(source => source.AssetDirectory),
                Has.None.Matches<string>(path => path.Replace('\\', '/').Contains("/Editor/")));
            Assert.That(plans.SelectMany(plan => plan.Sources)
                    .All(source => System.IO.Directory.Exists(source.PhysicalDirectory)), Is.True,
                "隔离构建必须拿到 Assets 或 PackageCache 中真实存在的 Module 源码目录。");
            var core = plans.Single(plan => plan.Key == "core");
            Assert.That(core.ManifestPackages,
                Does.Not.Contain("com.cysharp.r3").And.Not.Contain("com.cysharp.unitask"),
                "Git Package 应冻结已解析源码，不让可变 branch/tag 在不同档位重新解析。 ");
            Assert.That(core.ManifestFingerprint, Has.Length.EqualTo(64));
            Assert.That(core.MinimalManifest,
                Does.Not.Contain("com.cysharp.r3").And.Not.Contain("com.unity.ugui"),
                "每档应在启动时冻结最小 manifest，后续组合不能重新读取可能已变化的主工程版本。 ");
            Assert.That(core.CopiedPackages.Select(package => package.PackageName),
                Does.Contain("nuget-packages").And.Contain("com.cysharp.r3")
                    .And.Contain("com.cysharp.unitask"),
                "embedded 与 Git Package 来源必须以已解析内容进入可恢复的 Profile 计划。 ");
            Assert.That(core.CopiedPackages.All(package => Directory.Exists(package.PhysicalDirectory)), Is.True);
            Assert.That(core.CopiedPackages.Single(package => package.PackageName == "nuget-packages").PackageId,
                Is.EqualTo("nuget-packages@1.0.0"),
                "embedded Package 的 Unity packageId 含本机 file: 路径；报告应使用可移植身份。 ");
            Assert.That(core.CopiedPackages.Single(package => package.PackageName == "com.cysharp.r3")
                    .SourceFingerprint,
                Has.Length.EqualTo(64));
            Assert.That(plans.Single(plan => plan.Key == "ugui").ManifestPackages,
                Does.Contain("com.unity.inputsystem").And.Contain("com.unity.ugui"));
            Assert.That(plans.Single(plan => plan.Key == "toolkit").ManifestPackages,
                Does.Contain("com.unity.inputsystem").And.Not.Contain("com.unity.ugui"));
        }

        [Test]
        public void LinkXml_PreservesSelectedAssembliesOnceAndEscapesNames()
        {
            string xml = FrameworkBuildSizeProbe.CreateLinkXml(new[]
            {
                "Game.Framework.UI", "Game.Framework", "Game.Framework.UI", "A&B",
            });

            Assert.That(xml, Does.Contain("fullname=\"Game.Framework\" preserve=\"all\""));
            Assert.That(xml, Does.Contain("fullname=\"A&amp;B\""));
            Assert.That(Count(xml, "fullname=\"Game.Framework.UI\""), Is.EqualTo(1));
        }

        [Test]
        public void MinimalManifest_OnlyAddsPackagesRequiredBySelectedModules()
        {
            string core = FrameworkBuildSizeProbe.CreateMinimalManifest(
                Manifest, new[] { "com.cysharp.r3", "com.cysharp.unitask" });
            Assert.That(core, Does.Contain("com.cysharp.r3"));
            Assert.That(core, Does.Contain("com.cysharp.unitask"));
            Assert.That(core, Does.Contain("com.unity.modules.audio"));
            Assert.That(core, Does.Not.Contain("com.unity.inputsystem"));
            Assert.That(core, Does.Not.Contain("com.unity.ugui"));
            Assert.That(core, Does.Not.Contain("com.tuyoogame.yooasset\": \"3.0.5"));
            Assert.That(core, Does.Not.Contain("com.unity.entities"));

            string ugui = FrameworkBuildSizeProbe.CreateMinimalManifest(Manifest, new[]
            {
                "com.cysharp.r3",
                "com.cysharp.unitask",
                "com.unity.inputsystem",
                "com.unity.ugui",
            });
            Assert.That(ugui, Does.Contain("com.unity.inputsystem"));
            Assert.That(ugui, Does.Contain("com.unity.ugui"));

            string yoo = FrameworkBuildSizeProbe.CreateMinimalManifest(
                Manifest, new[] { "com.tuyoogame.yooasset" });
            Assert.That(yoo, Does.Contain("com.tuyoogame.yooasset\": \"3.0.5"));

            string arbitrary = FrameworkBuildSizeProbe.CreateMinimalManifest(
                Manifest, new[] { "com.unity.entities" });
            Assert.That(arbitrary, Does.Contain("com.unity.entities\": \"1.4.8"),
                "manifest 生成必须消费派生 Package 名，而不是只认识内置 Module 映射。 ");
            Assert.That(arbitrary, Does.Contain("example.registry"));
            Assert.That(arbitrary, Does.Contain("https://packages.example.invalid"),
                "隔离 manifest 应保留主工程 scoped registry，不为某个供应商硬编码 registry。 ");
        }

        [Test]
        public void PackagePlan_UsesActualAutoReferencedPackageWithoutNameMapping()
        {
            const string framework = FrameworkModuleAudit.CoreAssemblyName;
            const string dependency = "Arbitrary.AutoReferenced.Runtime";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(StringComparer.Ordinal)
                {
                    [framework] = new()
                    {
                        Name = framework,
                        DeclaredReferences = Array.Empty<string>(),
                        ActualReferences = new[] { dependency, "System.Runtime" },
                    },
                    [dependency] = new()
                    {
                        Name = dependency,
                        DeclaredReferences = Array.Empty<string>(),
                        ActualReferences = Array.Empty<string>(),
                    },
                },
                new Dictionary<string, string>(StringComparer.Ordinal),
                Array.Empty<string>(),
                string.Empty,
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>(
                    StringComparer.Ordinal)
                {
                    [dependency] = new()
                    {
                        AssemblyName = dependency,
                        AssetPath = "Packages/com.example.arbitrary/Runtime/Arbitrary.asmdef",
                        PackageName = "com.example.arbitrary",
                        PackageVersion = "1.0.0",
                        PackageId = "com.example.arbitrary@1.0.0",
                        SourceKind = FrameworkModuleSourceCatalog.SourceKind.RegistryPackage,
                        IsExternal = true,
                    },
                });

            FrameworkBuildSizeProbe.PackageDependencyPlan plan =
                FrameworkBuildSizeProbe.BuildPackageDependencyPlan(snapshot, new[] { framework });

            Assert.That(plan.ManifestPackages, Is.EqualTo(new[] { "com.example.arbitrary" }),
                "autoReferenced Package 可被实际 DLL 使用但不出现在 asmdef references；探针必须消费元数据边。 ");

            snapshot.Assemblies[framework].ActualReferences = new[] { "Mystery.External.Runtime" };
            Assert.That(() => FrameworkBuildSizeProbe.BuildPackageDependencyPlan(
                    snapshot, new[] { framework }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("外部依赖来源"),
                "只有明确的 BCL / Unity 平台程序集可无 Package 来源；普通未知 DLL 不能被静默漏掉。 ");
        }

        [Test]
        public void FrameworkCompileClosure_IncludesDeclaredOnlyRuntimeModule()
        {
            const string core = FrameworkModuleAudit.CoreAssemblyName;
            const string optional = "Game.Framework.Optional";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(StringComparer.Ordinal)
                {
                    [core] = new()
                    {
                        Name = core,
                        DeclaredReferences = new[] { optional },
                        ActualReferences = Array.Empty<string>(),
                    },
                    [optional] = new()
                    {
                        Name = optional,
                        DeclaredReferences = Array.Empty<string>(),
                        ActualReferences = Array.Empty<string>(),
                    },
                },
                new Dictionary<string, string>(StringComparer.Ordinal),
                Array.Empty<string>(),
                string.Empty);

            string[] closure = FrameworkBuildSizeProbe.BuildFrameworkCompileClosure(
                snapshot, new[] { core }, new[] { core, optional });

            Assert.That(closure, Is.EqualTo(new[] { core, optional }),
                "asmdef 声明边即使尚未产生 IL 引用，也要求目标 Module 源码存在于隔离编译工程。 ");
            Assert.That(() => FrameworkBuildSizeProbe.BuildFrameworkCompileClosure(
                    snapshot, new[] { core }, new[] { core }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("不能把声明边静默当成未使用"));
        }

        [Test]
        public void ModuleCopy_ExcludesEditorAndTestImplementations()
        {
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Editor/Foo.cs"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Tests/Editor/Foo.cs"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Editor.meta"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Runtime/Foo.cs"), Is.False);
            Assert.That(FrameworkBuildSizeProbe.ShouldSkipModulePath("Contest/Foo.cs"), Is.False);
        }

        [Test]
        public void SourceDirectories_MustBeDisjointToKeepDeletionEvidenceHonest()
        {
            string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "SSFrameworkProbeSourceOverlap");
            var exception = Assert.Throws<System.IO.InvalidDataException>(() =>
                FrameworkBuildSizeProbe.ValidateDisjointSourceDirectories(new[]
                {
                    new FrameworkBuildSizeProbe.ModuleSourcePlan
                    {
                        AssemblyName = "Game.Framework.Parent",
                        AssetDirectory = "Packages/example/Runtime",
                        PhysicalDirectory = root,
                    },
                    new FrameworkBuildSizeProbe.ModuleSourcePlan
                    {
                        AssemblyName = "Game.Framework.Child",
                        AssetDirectory = "Packages/example/Runtime/Child",
                        PhysicalDirectory = System.IO.Path.Combine(root, "Child"),
                    },
                }));

            Assert.That(exception?.Message, Does.Contain("夹带另一个"));
        }

        [Test]
        public void SourceIdentity_IgnoresCacheRelocationButRejectsPackageDrift()
        {
            var recorded = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = "Game.Framework",
                AssetDirectory = "Packages/com.example.framework/Runtime",
                PhysicalDirectory = "C:/cache/old",
                PackageName = "com.example.framework",
                PackageVersion = "1.0.0",
                PackageId = "com.example.framework@1.0.0",
                SourceFingerprint = "source-hash-1",
            };
            var relocated = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = recorded.AssemblyName,
                AssetDirectory = recorded.AssetDirectory,
                PhysicalDirectory = "D:/cache/new",
                PackageName = recorded.PackageName,
                PackageVersion = recorded.PackageVersion,
                PackageId = recorded.PackageId,
                SourceFingerprint = recorded.SourceFingerprint,
            };

            Assert.That(FrameworkBuildSizeProbe.FindSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Is.Empty);

            relocated.SourceFingerprint = "source-hash-2";
            Assert.That(FrameworkBuildSizeProbe.FindSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Does.Contain("源码身份已变化"));

            relocated.SourceFingerprint = recorded.SourceFingerprint;
            relocated.PackageId = "com.example.framework@2.0.0";
            Assert.That(FrameworkBuildSizeProbe.FindSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Does.Contain("源码身份已变化"));
        }

        [Test]
        public void EmbeddedPackageIdentity_IgnoresRelocationButRejectsCopiedContentDrift()
        {
            var recorded = new FrameworkBuildSizeProbe.PackageSourcePlan
            {
                PackageName = "nuget-packages",
                AssetDirectory = "Packages/nuget-packages",
                PhysicalDirectory = "C:/project/Packages/nuget-packages",
                PackageVersion = "1.0.0",
                PackageId = "nuget-packages@1.0.0",
                SourceFingerprint = "package-hash-1",
            };
            var relocated = new FrameworkBuildSizeProbe.PackageSourcePlan
            {
                PackageName = recorded.PackageName,
                AssetDirectory = recorded.AssetDirectory,
                PhysicalDirectory = "D:/worktree/Packages/nuget-packages",
                PackageVersion = recorded.PackageVersion,
                PackageId = recorded.PackageId,
                SourceFingerprint = recorded.SourceFingerprint,
            };

            Assert.That(FrameworkBuildSizeProbe.FindPackageSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Is.Empty);

            relocated.SourceFingerprint = "package-hash-2";
            Assert.That(FrameworkBuildSizeProbe.FindPackageSourceIdentityMismatch(
                new[] { recorded }, new[] { relocated }), Does.Contain("复制 Package 源码身份已变化"));
        }

        [Test]
        public void StablePackageId_RemovesLocationsFromEveryCopiedPackageSource()
        {
            Assert.That(FrameworkBuildSizeProbe.StablePackageIdForReport(
                    FrameworkModuleSourceCatalog.SourceKind.EmbeddedPackage,
                    "com.example.embedded", "1.2.3", "com.example.embedded@file:D:/private/package"),
                Is.EqualTo("com.example.embedded@1.2.3"));
            Assert.That(FrameworkBuildSizeProbe.StablePackageIdForReport(
                    FrameworkModuleSourceCatalog.SourceKind.LocalPackage,
                    "com.example.local", "2.0.0", "com.example.local@file:../local"),
                Is.EqualTo("com.example.local@2.0.0"));
            Assert.That(FrameworkBuildSizeProbe.StablePackageIdForReport(
                    FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                    "com.example.git", "3.0.0",
                    "com.example.git@git+https://secret-token@example.invalid/repo#commit"),
                Is.EqualTo("com.example.git@3.0.0"),
                "Git 内容已有 SHA-256 证据，报告身份不得保留 URL userinfo 或 token。 ");
        }

        [Test]
        public void CopiedPackageSources_IncludeGitLocalTarballAndEmbeddedOnly()
        {
            Assert.That(FrameworkBuildSizeProbe.IsCopiedPackageSource(
                FrameworkModuleSourceCatalog.SourceKind.EmbeddedPackage), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsCopiedPackageSource(
                FrameworkModuleSourceCatalog.SourceKind.LocalPackage), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsCopiedPackageSource(
                FrameworkModuleSourceCatalog.SourceKind.LocalTarballPackage), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsCopiedPackageSource(
                FrameworkModuleSourceCatalog.SourceKind.GitPackage), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsCopiedPackageSource(
                FrameworkModuleSourceCatalog.SourceKind.RegistryPackage), Is.False);
        }

        [Test]
        public void CopiedPackage_RejectsWorkspaceRelativeTransitiveDependency()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeLocalPackage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "package.json"), @"{
  ""name"": ""com.example.local"",
  ""version"": ""1.0.0"",
  ""dependencies"": {
    ""com.example.sibling"": ""file:../Sibling""
  }
}");
                var location = new FrameworkModuleSourceCatalog.SourceLocation
                {
                    PackageName = "com.example.local",
                    PhysicalPath = root,
                };

                Assert.That(() => FrameworkBuildSizeProbe.ValidateCopiedPackageDependencies(location),
                    Throws.TypeOf<InvalidDataException>().With.Message.Contains("本地传递依赖"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void SourceFingerprint_TracksCopiedContentButIgnoresSkippedEditorFiles()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeFingerprint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Editor"));
            try
            {
                string runtimeFile = Path.Combine(root, "Runtime.cs");
                string editorFile = Path.Combine(root, "Editor", "Inspector.cs");
                File.WriteAllText(runtimeFile, "runtime-v1");
                File.WriteAllText(editorFile, "editor-v1");
                string original = FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(root);

                File.WriteAllText(editorFile, "editor-v2");
                Assert.That(FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(root), Is.EqualTo(original),
                    "Editor/Test 内容不会复制进隔离工程，不应让恢复身份误报漂移。");

                File.WriteAllText(runtimeFile, "runtime-v2");
                Assert.That(FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(root), Is.Not.EqualTo(original),
                    "实际复制内容变化后，Domain Reload 恢复必须拒绝混用旧报告。 ");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void RecoveryDrift_RejectsProfileRemovedFromCurrentModuleTopology()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                FormatVersion = 5,
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "removed-module",
                        Sources = Array.Empty<FrameworkBuildSizeProbe.ModuleSourcePlan>(),
                        ManifestPackages = Array.Empty<string>(),
                        CopiedPackages = Array.Empty<FrameworkBuildSizeProbe.PackageSourcePlan>(),
                    },
                },
            };

            string drift = FrameworkBuildSizeProbe.FindRecoveryDrift(
                report,
                new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal));

            Assert.That(drift, Does.Contain("已不在当前 Module 拓扑"));
        }

        [Test]
        public void RecoveryDrift_RejectsPreV5ReportWithoutPackageEvidence()
        {
            var report = new FrameworkBuildSizeProbe.RunReport { FormatVersion = 4 };

            string drift = FrameworkBuildSizeProbe.FindRecoveryDrift(
                report,
                new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal));

            Assert.That(drift, Does.Contain("早于 v5"));
        }

        [Test]
        public void FutureReport_IsRejectedForRecoveryAndRebuild()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                FormatVersion = FrameworkBuildSizeProbe.CurrentReportFormatVersion + 1,
            };

            string drift = FrameworkBuildSizeProbe.FindRecoveryDrift(
                report,
                new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal));

            Assert.That(drift, Does.Contain("新于当前工具"));
            Assert.That(() => FrameworkBuildSizeProbe.EnsureReportCanBeRebuilt(report),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("拒绝用旧代码重写"));
        }

        [Test]
        public void RecoveryDrift_RejectsManifestAndEmbeddedPackageChanges()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                FormatVersion = 5,
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "core",
                        Sources = Array.Empty<FrameworkBuildSizeProbe.ModuleSourcePlan>(),
                        ManifestPackages = new[] { "com.example.old" },
                        ManifestFingerprint = "same-manifest-hash",
                        CopiedPackages = new[]
                        {
                            new FrameworkBuildSizeProbe.PackageSourcePlan
                            {
                                PackageName = "nuget-packages",
                                AssetDirectory = "Packages/nuget-packages",
                                PackageId = "nuget-packages@1.0.0",
                                SourceFingerprint = "old-hash",
                            },
                        },
                    },
                },
            };
            var current = new FrameworkBuildSizeProbe.ProfilePlan
            {
                Key = "core",
                Sources = Array.Empty<FrameworkBuildSizeProbe.ModuleSourcePlan>(),
                ManifestPackages = new[] { "com.example.new" },
                ManifestFingerprint = "same-manifest-hash",
                CopiedPackages = report.Profiles[0].CopiedPackages,
            };
            var plans = new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal)
            {
                [current.Key] = current,
            };

            Assert.That(FrameworkBuildSizeProbe.FindRecoveryDrift(report, plans),
                Does.Contain("manifest Package 依赖已变化"));

            current.ManifestPackages = report.Profiles[0].ManifestPackages;
            current.ManifestFingerprint = "new-manifest-hash";
            Assert.That(FrameworkBuildSizeProbe.FindRecoveryDrift(report, plans),
                Does.Contain("冻结 manifest 的版本规格"));

            current.ManifestFingerprint = report.Profiles[0].ManifestFingerprint;
            current.CopiedPackages = new[]
            {
                new FrameworkBuildSizeProbe.PackageSourcePlan
                {
                    PackageName = "nuget-packages",
                    AssetDirectory = "Packages/nuget-packages",
                    PackageId = "nuget-packages@1.0.0",
                    SourceFingerprint = "new-hash",
                },
            };
            Assert.That(FrameworkBuildSizeProbe.FindRecoveryDrift(report, plans),
                Does.Contain("复制 Package 源码身份已变化"));
        }

        [Test]
        public void ShippingOutput_ExcludesUnityDoNotShipEvidenceAndDebugSymbols()
        {
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("GameAssembly.dll"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("Player_Data/globalgamemanagers"), Is.True);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath(
                "Probe_BackUpThisFolder_ButDontShipItWithYourGame/GameAssembly.pdb"), Is.False);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("BurstDebugInformation_DoNotShip/file.txt"),
                Is.False);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("Symbols/GameAssembly.pdb"), Is.False);
            Assert.That(FrameworkBuildSizeProbe.IsShippingOutputPath("Probe.dSYM/Contents/file"), Is.False);
        }

        [Test]
        public void OperationalPaths_AreReconstructedLocallyAndExcludedFromSharedJson()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord { Key = "core" },
                },
            };
            string runDirectory = Path.Combine(
                Path.GetTempPath(), "SSFramework-private-run-" + Guid.NewGuid().ToString("N"));

            FrameworkBuildSizeProbe.RestoreOperationalPaths(report, runDirectory);

            Assert.That(report.RunDirectory, Is.EqualTo(Path.GetFullPath(runDirectory)));
            Assert.That(report.Profiles[0].OutputPath,
                Is.EqualTo(Path.Combine(Path.GetFullPath(runDirectory), "Output", "core")));
            string json = JsonUtility.ToJson(report);
            Assert.That(json, Does.Not.Contain("SSFramework-private-run"));
            Assert.That(json,
                Does.Not.Contain("OutputPath").And.Not.Contain("ResultPath").And.Not.Contain("LogPath"));
        }

        [Test]
        public void ChildEnvironment_DoesNotInheritHybridClrIl2CppOverride()
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false };
            startInfo.EnvironmentVariables[FrameworkBuildSizeProbe.UnityIl2CppPathEnvironmentVariable] =
                "D:/main-project/HybridCLRData/LocalIl2CppData";

            FrameworkBuildSizeProbe.SanitizeChildEnvironment(startInfo);

            Assert.That(startInfo.EnvironmentVariables.ContainsKey(
                FrameworkBuildSizeProbe.UnityIl2CppPathEnvironmentVariable), Is.False,
                "隔离工程未安装 HybridCLR，不能继承主 Unity 进程的本地 IL2CPP 路径。 ");
        }

        [Test]
        public void FailedChildLog_PrefersFirstActionableDiagnosticOverMisleadingTail()
        {
            string path = Path.Combine(Path.GetTempPath(), "SSFrameworkProbeLog-" + Guid.NewGuid() + ".log");
            try
            {
                File.WriteAllLines(path, new[]
                {
                    "startup",
                    "DirectoryNotFoundException: missing local il2cpp",
                    "noise after the real failure",
                    "executeMethod class 'SSFrameworkBuildProbeChild' could not be found.",
                });

                string excerpt = FrameworkBuildSizeProbe.ReadDiagnosticLogExcerpt(path, 12);

                Assert.That(excerpt, Does.StartWith("DirectoryNotFoundException:"));
                Assert.That(excerpt, Does.Contain("executeMethod class"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void MarkdownReport_ComputesDeltaAgainstSuccessfulCore()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                RunDirectory = "D:/private/build-size-run",
                UnityVersion = "6000.test",
                Target = "WebGL",
                ScriptingBackend = "IL2CPP",
                StrippingLevel = "High",
                EvidenceScope = "test",
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "core", Title = "只用核心", Status = "成功", OutputBytes = 1024,
                        RawOutputBytes = 4096,
                        OutputPath = "D:/private/build-size-run/Output/core",
                        ResultPath = "D:/private/build-size-run/Results/core.json",
                        LogPath = "D:/private/build-size-run/Logs/core.log",
                        ManifestPackages = new[] { "com.example.framework" },
                        ManifestFingerprint = "1122334455667788",
                        CopiedPackages = new[]
                        {
                            new FrameworkBuildSizeProbe.PackageSourcePlan
                            {
                                PackageName = "nuget-packages",
                                AssetDirectory = "Packages/nuget-packages",
                                PhysicalDirectory = "D:/private/Packages/nuget-packages",
                                PackageId = "nuget-packages@1.0.0",
                                SourceFingerprint = "fedcba9876543210",
                            },
                        },
                        Sources = new[]
                        {
                            new FrameworkBuildSizeProbe.ModuleSourcePlan
                            {
                                AssemblyName = FrameworkModuleAudit.CoreAssemblyName,
                                AssetDirectory = "Packages/com.example.framework/Runtime",
                                PhysicalDirectory = "Library/PackageCache/com.example.framework/Runtime",
                                PackageName = "com.example.framework",
                                PackageVersion = "1.2.3",
                                PackageId = "com.example.framework@1.2.3",
                                SourceFingerprint = "abcdef0123456789",
                            },
                        },
                    },
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "ugui", Title = "核心 + UGUI", Status = "成功", OutputBytes = 3072,
                        RawOutputBytes = 8192,
                    },
                },
            };

            string markdown = FrameworkBuildSizeProbe.CreateMarkdownReport(report);

            Assert.That(markdown, Does.Contain("+2.0 KiB"));
            Assert.That(markdown, Does.Contain("体积上界"));
            Assert.That(markdown, Does.Contain("未选 Module"));
            Assert.That(markdown, Does.Contain("非发布构建证据"));
            Assert.That(markdown, Does.Contain(
                "Game.Framework ← Packages/com.example.framework/Runtime (com.example.framework@1.2.3)"));
            Assert.That(markdown, Does.Contain("实际复制内容 SHA-256：`abcdef0123456789`"));
            Assert.That(markdown, Does.Contain("manifest Package：com.example.framework"));
            Assert.That(markdown, Does.Contain("冻结 manifest SHA-256：`1122334455667788`"));
            Assert.That(markdown, Does.Contain(
                "nuget-packages ← Packages/nuget-packages (nuget-packages@1.0.0)"));
            Assert.That(markdown, Does.Contain("实际复制内容 SHA-256：`fedcba9876543210`"));
            Assert.That(JsonUtility.ToJson(report), Does.Not.Contain("PackageCache"),
                "可分享 JSON 只记录稳定 Asset/package 身份，不得泄漏机器专属物理缓存路径。");
            Assert.That(JsonUtility.ToJson(report), Does.Not.Contain("D:/private"),
                "运行目录、结果路径与复制 Package 物理目录都只属于当前进程，不得写入可分享报告。 ");
        }

        [Test]
        public void Window_UsesProgressiveDisclosureAndResponsiveActions()
        {
            var window = ScriptableObject.CreateInstance<FrameworkBuildSizeProbeWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 360f, 520f);
                window.CreateGUI();

                var content = window.rootVisualElement.Q<ScrollView>("build-size-probe-content");
                var actions = window.rootVisualElement.Q<VisualElement>("build-size-probe-actions");
                var core = window.rootVisualElement.Q<Toggle>("build-size-probe-toggle-core");
                var full = window.rootVisualElement.Q<Toggle>("build-size-probe-toggle-full");
                var advanced = window.rootVisualElement.Q<Foldout>("build-size-probe-advanced-profiles");
                var bridge = window.rootVisualElement.Q<Toggle>(
                    "build-size-probe-toggle-module-game-framework-ui-bridge");
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(actions, Is.Not.Null);
                Assert.That(actions.childCount, Is.EqualTo(4));
                Assert.That(core?.value, Is.True);
                Assert.That(full?.value, Is.False);
                Assert.That(advanced, Is.Not.Null);
                Assert.That(advanced.value, Is.False);
                Assert.That(bridge, Is.Not.Null);
                Assert.That(bridge.value, Is.False, "任意 Module 慢构建必须由用户按需选择，不能默认全跑。 ");
                Assert.That(window.rootVisualElement.Q<TextField>(), Is.Null);

                window.ApplyResponsiveLayoutForTests(360f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                Assert.That(actions[0].style.flexBasis.keyword, Is.EqualTo(StyleKeyword.Auto));

                window.ApplyResponsiveLayoutForTests(900f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
                Assert.That(actions[0].style.flexGrow.value, Is.EqualTo(1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        private static int Count(string text, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            return count;
        }
    }
}
