using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
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

        private static void StampCurrentEvidence(FrameworkBuildSizeProbe.RunReport report)
        {
            var template = FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                FrameworkBuildSizeProbe.ChildTemplateFileName,
                FrameworkModuleAudit.CoreAssemblyName + ".Editor");
            string content = File.ReadAllText(template.PhysicalPath);
            report.ChildTemplateFingerprint =
                FrameworkBuildSizeProbe.ComputeChildTemplateFingerprint(content);
            report.EvidenceImplementationFingerprint =
                FrameworkBuildSizeProbe.ComputeEvidenceImplementationFingerprint(content);
        }

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
                Does.Not.Contain("com.unity.inputsystem").And.Contain("com.unity.ugui"),
                "UI Core 不应因项目返回键接线被迫安装 Input System。");
            Assert.That(plans.Single(plan => plan.Key == "toolkit").ManifestPackages,
                Does.Not.Contain("com.unity.inputsystem").And.Not.Contain("com.unity.ugui"));
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
        public void ModuleDestination_CombinesReadableSourceRoleWithStableAssemblyIdentity()
        {
            var core = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = "Game.Framework",
                AssetDirectory = "Assets/Game/Framework/Core",
            };
            var firstRuntime = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = "Game.Framework.Feature.One",
                AssetDirectory = "Packages/com.example.one/Runtime",
            };
            var secondRuntime = new FrameworkBuildSizeProbe.ModuleSourcePlan
            {
                AssemblyName = "Game_Framework_Feature_One",
                AssetDirectory = "Packages/com.example.two/Runtime",
            };

            string coreDestination = FrameworkBuildSizeProbe.ModuleDestinationDirectoryName(core);
            Assert.That(coreDestination, Does.StartWith("Core__Game_Framework__"));
            Assert.That(coreDestination, Is.EqualTo(
                    FrameworkBuildSizeProbe.ModuleDestinationDirectoryName(core)),
                "同一 Module 的复制目录必须跨重载稳定。 ");
            Assert.That(coreDestination,
                Is.Not.EqualTo(core.AssemblyName),
                "目录与其中 asmdef 同名会让部分 Unity 6000.3 导入路径产生空壳构建。 ");
            Assert.That(FrameworkBuildSizeProbe.ModuleDestinationDirectoryName(firstRuntime),
                Is.Not.EqualTo(FrameworkBuildSizeProbe.ModuleDestinationDirectoryName(secondRuntime)),
                "不同 Package 常共享 Runtime 叶目录；可读 slug 即使碰撞，稳定身份哈希仍必须区分目标。 ");
        }

        [Test]
        public void ModuleDestination_AvoidsTheActualAsmdefFileNameEvenWhenItDiffersFromAssemblyName()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeAsmdefCollision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var module = new FrameworkBuildSizeProbe.ModuleSourcePlan
                {
                    AssemblyName = "Game.Framework.Custom",
                    AssetDirectory = "Packages/com.example.custom/Runtime",
                };
                string originalCandidate = FrameworkBuildSizeProbe.ModuleDestinationDirectoryName(module);
                File.WriteAllText(Path.Combine(root, originalCandidate + ".asmdef"), "{}");
                module.PhysicalDirectory = root;

                Assert.That(FrameworkBuildSizeProbe.ModuleDestinationDirectoryName(module),
                    Is.EqualTo(originalCandidate + "__source"),
                    "Unity 允许 asmdef 文件名不同于 assembly name，目标目录仍不得与真实 asmdef 同名。 ");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void ChildTemplate_RejectsBuildsMissingExpectedPlayerAssemblies()
        {
            var template = FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                FrameworkBuildSizeProbe.ChildTemplateFileName,
                FrameworkModuleAudit.CoreAssemblyName + ".Editor");
            string source = File.ReadAllText(template.PhysicalPath);

            Assert.That(source, Does.Contain("-ssProbeAssemblies"));
            Assert.That(source, Does.Contain("CompilationPipeline.GetAssemblies(AssembliesType.Player)"));
            Assert.That(source, Does.Contain("ValidateExpectedAssemblies(expectedAssemblies)"));
            Assert.That(source, Does.Contain("拒绝把未实际编译 Framework IL 的空壳 Player 记为成功"));
            Assert.That(source, Does.Contain(".Where(assembly => !compiled.ContainsKey(assembly))"));
            Assert.That(source, Does.Contain("sourceFiles.Length == 0"));
            Assert.That(source, Does.Contain(".ssframework-write-"));
            Assert.That(source, Does.Contain("File.Replace(temporary, destination, null)"));
            Assert.That(source, Does.Not.Contain(
                "File.WriteAllText(path, JsonUtility.ToJson(record, true)"),
                "child 结果也是恢复证据，不能直接覆盖并留下半截 JSON。 ");
            Assert.That(source.IndexOf("ValidateExpectedAssemblies(expectedAssemblies);", StringComparison.Ordinal),
                Is.LessThan(source.IndexOf("BuildPipeline.BuildPlayer", StringComparison.Ordinal)),
                "期望程序集门禁必须发生在 Player Build 之前。 ");
        }

        [Test]
        public void EvidenceImplementationFingerprint_BindsCompiledEditorSourceAndChildTemplate()
        {
            string first = FrameworkBuildSizeProbe.ComputeEvidenceImplementationFingerprint(
                "child-template-v1");
            string second = FrameworkBuildSizeProbe.ComputeEvidenceImplementationFingerprint(
                "child-template-v2");

            Assert.That(first, Has.Length.EqualTo(64));
            Assert.That(second, Has.Length.EqualTo(64));
            Assert.That(second, Is.Not.EqualTo(first),
                "子模板变化必须改变整套证据实现身份，不能只依赖手工提升报告版本。 ");
        }

        [Test]
        public void FrozenChildTemplateSnapshot_IsAddressedOutsideProjectAndRejectsTampering()
        {
            string runRoot = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeChildSnapshot-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(runRoot, "Project");
            string frozen = FrameworkBuildSizeProbe.FrozenChildTemplatePath(project);
            try
            {
                Assert.That(frozen, Is.EqualTo(Path.Combine(
                    runRoot,
                    FrameworkBuildSizeProbe.FrozenInputsDirectoryName,
                    FrameworkBuildSizeProbe.ChildTemplateFileName)));
                Directory.CreateDirectory(Path.GetDirectoryName(frozen)!);
                File.WriteAllText(frozen, "child-template-v1");
                string expected = FrameworkBuildSizeProbe.ComputeChildTemplateFingerprint(
                    "child-template-v1");

                Assert.That(FrameworkBuildSizeProbe.FindFrozenChildTemplateDrift(frozen, expected),
                    Is.Empty);
                File.WriteAllText(frozen, "child-template-v2");
                Assert.That(FrameworkBuildSizeProbe.FindFrozenChildTemplateDrift(frozen, expected),
                    Does.Contain("启动快照已变化"));
            }
            finally
            {
                if (Directory.Exists(runRoot)) Directory.Delete(runRoot, true);
            }
        }

        [Test]
        public void ProfilePreparation_ReadsRunOwnedChildTemplateInsteadOfLiveEditorSource()
        {
            var source = FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                nameof(FrameworkBuildSizeProbe) + ".cs",
                FrameworkModuleAudit.CoreAssemblyName + ".Editor");
            string implementation = File.ReadAllText(source.PhysicalPath);
            int start = implementation.IndexOf(
                "private static void PrepareProfileSources(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            int end = implementation.IndexOf(
                "internal static string FindFrozenProfileInputDrift", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start));
            string method = implementation.Substring(start, end - start);

            Assert.That(method, Does.Contain("FrozenChildTemplatePath(projectDirectory)"));
            Assert.That(method, Does.Contain("ReadFrozenChildTemplate("));
            Assert.That(method, Does.Not.Contain("ResolveChildTemplate()"),
                "每档不得回到主工程读取可能已变化的 live 模板。 ");
        }

        [Test]
        public void ProfileSwitch_DeletesDerivedUnityStateButKeepsFrozenInputs()
        {
            string runsRoot = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", FrameworkBuildSizeProbe.RunsRoot));
            string runRoot = Path.Combine(
                runsRoot, "Test-ProfileReset-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(runRoot, "Project");
            try
            {
                string asset = Path.Combine(root, "Assets", "Framework", "Runtime.cs");
                string childTemplate = Path.Combine(
                    root, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs");
                string manifest = Path.Combine(root, "Packages", "manifest.json");
                string projectVersion = Path.Combine(root, "ProjectSettings", "ProjectVersion.txt");
                string staleAssembly = Path.Combine(root, "Library", "ScriptAssemblies", "Game.Framework.dll");
                string staleTemp = Path.Combine(root, "Temp", "UnityLockfile");
                string staleObj = Path.Combine(root, "obj", "cache.bin");
                foreach (string path in new[]
                         {
                             asset, childTemplate, manifest, projectVersion,
                             staleAssembly, staleTemp, staleObj,
                         })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "fixture");
                }

                FrameworkBuildSizeProbe.ResetDerivedProjectState(root);

                Assert.That(Directory.Exists(Path.Combine(root, "Library")), Is.False);
                Assert.That(Directory.Exists(Path.Combine(root, "Temp")), Is.False);
                Assert.That(Directory.Exists(Path.Combine(root, "obj")), Is.False);
                Assert.That(File.Exists(asset), Is.True, "冻结的 Module 输入不能随派生缓存一起删除。");
                Assert.That(File.Exists(manifest), Is.True, "冻结的 Package 计划不能随派生缓存一起删除。");
            }
            finally
            {
                if (Directory.Exists(runRoot)) Directory.Delete(runRoot, true);
            }
        }

        [Test]
        public void ProfileSwitch_RejectsNonProbeWorkspaceBeforeDeletingAnything()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeUnsafeReset-" + Guid.NewGuid().ToString("N"));
            string sentinel = Path.Combine(root, "Library", "keep.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
            File.WriteAllText(sentinel, "must-survive");
            try
            {
                Assert.That(() => FrameworkBuildSizeProbe.ResetDerivedProjectState(root),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains("拒绝清理非探针工作区"));
                Assert.That(File.Exists(sentinel), Is.True,
                    "路径身份检查必须发生在任何递归删除之前。 ");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void RequestedProfiles_FailWhenAnyRequestedModuleProfileIsUnavailable()
        {
            var available = new[]
            {
                new FrameworkBuildSizeProbe.ProfilePlan { Key = "core" },
                new FrameworkBuildSizeProbe.ProfilePlan { Key = "ugui" },
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                FrameworkBuildSizeProbe.SelectRequestedPlans(
                    new[] { "core", "ugui", "toolkit" }, available));
            Assert.That(exception?.Message, Does.Contain("toolkit").And.Contain("物理删除"));
            Assert.That(FrameworkBuildSizeProbe.SelectRequestedPlans(
                    new[] { "ugui", "core", "core" }, available).Select(plan => plan.Key),
                Is.EqualTo(new[] { "core", "ugui" }),
                "成功路径沿稳定 Profile 拓扑排序，重复请求不应重复构建。 ");
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
        public void SourceFingerprint_CanOpenWindowsPackageFilesBeyondLegacyMaxPath()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows MAX_PATH regression only.");
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeLongPath-" + Guid.NewGuid().ToString("N"));
            string directory = root;
            while (Path.Combine(directory, "package.targets.meta").Length <= 270)
                directory = Path.Combine(directory, "buildTransitive0123456789");
            string file = Path.Combine(directory, "package.targets.meta");
            try
            {
                Directory.CreateDirectory(FrameworkBuildSizeProbe.ExtendedLengthPath(directory));
                File.WriteAllText(
                    FrameworkBuildSizeProbe.ExtendedLengthPath(file),
                    "long-path-fixture");

                Assert.That(
                    FrameworkBuildSizeProbe.ComputeCopiedPackageSourceFingerprint(root),
                    Has.Length.EqualTo(64),
                    "目录枚举成功后，文件流也必须用 extended-length 路径打开。 ");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void FrozenProfileInputDrift_DetectsModuleAndCopiedPackageWritesBetweenProfiles()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeFrozenInputs-" + Guid.NewGuid().ToString("N"));
            string moduleRoot = Path.Combine(root, "Module");
            string packageRoot = Path.Combine(root, "Package");
            Directory.CreateDirectory(moduleRoot);
            Directory.CreateDirectory(packageRoot);
            string moduleFile = Path.Combine(moduleRoot, "Runtime.cs");
            string packageFile = Path.Combine(packageRoot, "package.json");
            File.WriteAllText(moduleFile, "module-v1");
            File.WriteAllText(packageFile, "package-v1");
            try
            {
                var profile = new FrameworkBuildSizeProbe.ProfilePlan
                {
                    Key = "fixture",
                    Sources = new[]
                    {
                        new FrameworkBuildSizeProbe.ModuleSourcePlan
                        {
                            AssemblyName = "Game.Framework.Fixture",
                            PhysicalDirectory = moduleRoot,
                            SourceFingerprint = FrameworkBuildSizeProbe.ComputeModuleSourceFingerprint(moduleRoot),
                        },
                    },
                    CopiedPackages = new[]
                    {
                        new FrameworkBuildSizeProbe.PackageSourcePlan
                        {
                            PackageName = "com.example.fixture",
                            PhysicalDirectory = packageRoot,
                            SourceFingerprint =
                                FrameworkBuildSizeProbe.ComputeCopiedPackageSourceFingerprint(packageRoot),
                        },
                    },
                };

                Assert.That(FrameworkBuildSizeProbe.FindFrozenProfileInputDrift(profile), Is.Empty);
                File.WriteAllText(packageFile, "package-v2");
                Assert.That(FrameworkBuildSizeProbe.FindFrozenProfileInputDrift(profile),
                    Does.Contain("复制 Package com.example.fixture").And.Contain("本轮启动后变化"));

                profile.CopiedPackages[0].SourceFingerprint =
                    FrameworkBuildSizeProbe.ComputeCopiedPackageSourceFingerprint(packageRoot);
                File.WriteAllText(moduleFile, "module-v2");
                Assert.That(FrameworkBuildSizeProbe.FindFrozenProfileInputDrift(profile),
                    Does.Contain("Module Game.Framework.Fixture").And.Contain("本轮启动后变化"));
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
                FormatVersion = FrameworkBuildSizeProbe.CurrentReportFormatVersion,
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
            StampCurrentEvidence(report);

            string drift = FrameworkBuildSizeProbe.FindRecoveryDrift(
                report,
                new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal));

            Assert.That(drift, Does.Contain("已不在当前 Module 拓扑"));
        }

        [Test]
        public void RecoveryDrift_RejectsPreV8EvidenceContract()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                FormatVersion = FrameworkBuildSizeProbe.CurrentReportFormatVersion - 1,
            };

            string drift = FrameworkBuildSizeProbe.FindRecoveryDrift(
                report,
                new Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan>(StringComparer.Ordinal));

            Assert.That(drift,
                Does.Contain("早于 v" + FrameworkBuildSizeProbe.CurrentReportFormatVersion)
                    .And.Contain("冻结输入前后复核")
                    .And.Contain("子进程身份").And.Contain("Player 编译图证据契约"));
        }

        [Test]
        public void StopAfterCurrentReason_SurvivesReloadAndExplainsSkippedProfiles()
        {
            const string driftReason =
                "检测到证据输入漂移；当前组合完成后停止：子进程模板已变化。";
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                StopAfterCurrentReason = driftReason,
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "core", Status = "成功", Message = "child result replaced building message",
                    },
                    new FrameworkBuildSizeProbe.ProfileRecord
                    {
                        Key = "ugui", Status = "等待", Message = "temporary recovery message",
                    },
                },
            };

            var restored = JsonUtility.FromJson<FrameworkBuildSizeProbe.RunReport>(
                JsonUtility.ToJson(report));
            FrameworkBuildSizeProbe.CompleteWaitingProfiles(restored);

            Assert.That(restored.StopAfterCurrentReason, Is.EqualTo(driftReason),
                "停止原因必须是可恢复报告状态，不能只存在于 Domain Reload 会清空的 static bool。 ");
            Assert.That(restored.Profiles[0].Message,
                Is.EqualTo("child result replaced building message"),
                "已完成档位仍保留真实 child 结果。 ");
            Assert.That(restored.Profiles[1].Status, Is.EqualTo("跳过"));
            Assert.That(restored.Profiles[1].Message, Is.EqualTo(driftReason),
                "自动证据漂移不能在最终报告中被改写为用户请求停止。 ");
        }

        [Test]
        public void RecoveryTopologyFailure_BecomesDriftInsteadOfOrphaningRunningChild()
        {
            var report = new FrameworkBuildSizeProbe.RunReport();

            string drift = FrameworkBuildSizeProbe.TryCreateRecoveryPlans(
                report,
                () => throw new IOException("package manifest is being rewritten"),
                out Dictionary<string, FrameworkBuildSizeProbe.ProfilePlan> plans);

            Assert.That(plans, Is.Empty);
            Assert.That(drift,
                Does.Contain("无法重建当前 Module / Package 拓扑")
                    .And.Contain("完成已启动档位后停止")
                    .And.Contain("拒绝让子进程失去 owner")
                    .And.Contain("package manifest is being rewritten"));
        }

        [Test]
        public void Recovery_RebuildsOnlyProfilesRecordedByCurrentRun()
        {
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                Profiles = new[]
                {
                    new FrameworkBuildSizeProbe.ProfileRecord { Key = "core" },
                    new FrameworkBuildSizeProbe.ProfileRecord { Key = "ugui" },
                    new FrameworkBuildSizeProbe.ProfileRecord { Key = "core" },
                    null,
                    new FrameworkBuildSizeProbe.ProfileRecord { Key = " " },
                },
            };

            Assert.That(FrameworkBuildSizeProbe.GetRecoveryProfileKeys(report),
                Is.EqualTo(new[] { "core", "ugui" }));
        }

        [Test]
        public void Recovery_UnknownChildOwnerStopsBeforeStartingNextProfile()
        {
            string missing = Path.Combine(
                Path.GetTempPath(), "SSFrameworkMissingChild-" + Guid.NewGuid().ToString("N") + ".json");
            var building = new FrameworkBuildSizeProbe.ProfileRecord
            {
                Status = "构建中",
                ChildProcessId = 0,
                ResultPath = missing,
            };

            Assert.That(FrameworkBuildSizeProbe.MustStopRecoveryForUnknownChild(building), Is.True,
                "PID 写入失败后的 Domain Reload 不能把未知 child 当成已结束并启动下一档。 ");

            building.ChildProcessId = 12345;
            Assert.That(FrameworkBuildSizeProbe.MustStopRecoveryForUnknownChild(building), Is.True,
                "只有 PID 而没有启动时间仍无法排除 PID 复用。 ");

            building.ChildProcessStartUtcTicks = 67890;
            Assert.That(FrameworkBuildSizeProbe.MustStopRecoveryForUnknownChild(building), Is.False,
                "PID 与启动时间都已提交时才进入正常附着/进程终态判断。 ");
        }

        [Test]
        public void Recovery_UnknownOwnerWithFinishedResultKeepsResultAndExplainsSkippedProfiles()
        {
            string reason = FrameworkBuildSizeProbe.CreateUnknownChildOwnerStopReason(hasResultFile: true);
            var completed = new FrameworkBuildSizeProbe.ProfileRecord
            {
                Key = "core",
                Status = "成功",
                Message = "冻结输入构建完成。",
            };
            var waiting = new FrameworkBuildSizeProbe.ProfileRecord
            {
                Key = "ugui",
                Status = "等待",
            };
            var report = new FrameworkBuildSizeProbe.RunReport
            {
                StopAfterCurrentReason = reason,
                Profiles = new[] { completed, waiting },
            };

            FrameworkBuildSizeProbe.CompleteWaitingProfiles(report, reason);

            Assert.That(completed.Status, Is.EqualTo("成功"));
            Assert.That(completed.Message, Is.EqualTo("冻结输入构建完成。"),
                "当前档位的真实 child 结果不能被 owner 警告覆盖。 ");
            Assert.That(waiting.Status, Is.EqualTo("跳过"));
            Assert.That(waiting.Message, Is.EqualTo(reason));
            Assert.That(reason, Does.Contain("已接收原子结果").And.Contain("停止后续组合"),
                "后续档位必须记录真正的 fail-closed 原因，不能误写成当前档构建成功。 ");
        }

        [Test]
        public void Recovery_ProcessIdentityRejectsPidReuseAndOwnerSelfAttachment()
        {
            const int childPid = 123;
            const long childStart = 456789;

            Assert.That(FrameworkBuildSizeProbe.MatchesChildProcessIdentity(
                childPid, childStart, childPid, childStart, "Unity", ownerProcessId: 999), Is.True);
            Assert.That(FrameworkBuildSizeProbe.MatchesChildProcessIdentity(
                childPid, childStart, childPid, childStart + 1, "Unity", ownerProcessId: 999), Is.False,
                "相同 PID 的新进程不能冒充原 child。 ");
            Assert.That(FrameworkBuildSizeProbe.MatchesChildProcessIdentity(
                childPid, childStart, childPid, childStart, "Unity", ownerProcessId: childPid), Is.False,
                "恢复逻辑绝不能把当前主 Editor 附着成自己的 child。 ");
            Assert.That(FrameworkBuildSizeProbe.MatchesChildProcessIdentity(
                childPid, childStart, childPid, childStart, "unrelated-tool", ownerProcessId: 999), Is.False);
        }

        [Test]
        public void Recovery_ProcessInspectionFailureRemainsUnknownAndFailsClosed()
        {
            Process ignored;
            FrameworkBuildSizeProbe.ChildProcessAttachResult inaccessible =
                FrameworkBuildSizeProbe.TryAttachUnityProcess(
                    123,
                    456789,
                    _ => throw new System.ComponentModel.Win32Exception("access denied"),
                    ownerProcessId: 999,
                    out ignored);
            FrameworkBuildSizeProbe.ChildProcessAttachResult exited =
                FrameworkBuildSizeProbe.TryAttachUnityProcess(
                    123,
                    456789,
                    _ => throw new ArgumentException("process no longer exists"),
                    ownerProcessId: 999,
                    out ignored);

            Assert.That(inaccessible,
                Is.EqualTo(FrameworkBuildSizeProbe.ChildProcessAttachResult.UnknownInspectionFailure),
                "权限或平台检查失败不等于进程已终止，恢复逻辑必须停止后续 child。 ");
            Assert.That(exited,
                Is.EqualTo(FrameworkBuildSizeProbe.ChildProcessAttachResult.ConfirmedNotOwned),
                "GetProcessById 的不存在语义可以确认旧 child 已终止。 ");
        }

        [Test]
        public void AtomicReportWrite_FailureKeepsPreviousGenerationAndCleansTemporaryFile()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkAtomicReport-" + Guid.NewGuid().ToString("N"));
            string destination = Path.Combine(root, "report.json");
            Directory.CreateDirectory(root);
            File.WriteAllText(destination, "stable-generation");
            try
            {
                Assert.That(() => FrameworkBuildSizeProbe.WriteTextAtomically(
                        destination,
                        "new-generation",
                        (temporary, _) =>
                        {
                            File.WriteAllText(temporary, "truncated");
                            throw new IOException("simulated disk failure");
                        }),
                    Throws.TypeOf<IOException>().With.Message.Contains("simulated disk failure"));

                Assert.That(File.ReadAllText(destination), Is.EqualTo("stable-generation"));
                Assert.That(Directory.GetFiles(root, "*.ssframework-write-*"), Is.Empty);

                FrameworkBuildSizeProbe.WriteTextAtomically(destination, "new-generation");
                Assert.That(File.ReadAllText(destination), Is.EqualTo("new-generation"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void ChangedObserverFailure_DoesNotBlockLaterObservers()
        {
            int healthyCalls = 0;
            Action failing = () => throw new InvalidOperationException("broken probe window");
            Action healthy = () => healthyCalls++;
            FrameworkBuildSizeProbe.Changed += failing;
            FrameworkBuildSizeProbe.Changed += healthy;
            try
            {
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                    "状态观察者刷新失败.*broken probe window"));

                Assert.That(() => FrameworkBuildSizeProbe.NotifyChanged(), Throws.Nothing);
                Assert.That(healthyCalls, Is.EqualTo(1));
            }
            finally
            {
                FrameworkBuildSizeProbe.Changed -= failing;
                FrameworkBuildSizeProbe.Changed -= healthy;
            }
        }

        [Test]
        public void DriftRecovery_ConsumesFinishedChildResultBeforeStoppingPendingProfiles()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "SSFrameworkProbeFinishedDrift-" + Guid.NewGuid().ToString("N"));
            string resultPath = Path.Combine(root, "core.json");
            Directory.CreateDirectory(root);
            try
            {
                var childResult = new FrameworkBuildSizeProbe.ProfileRecord
                {
                    Key = "core",
                    Status = "成功",
                    Message = "冻结输入构建完成。",
                    BuildReportBytes = 1024,
                    OutputBytes = 512,
                    Errors = 0,
                    Warnings = 0,
                };
                File.WriteAllText(resultPath, JsonUtility.ToJson(childResult));
                var building = new FrameworkBuildSizeProbe.ProfileRecord
                {
                    Key = "core",
                    Status = "构建中",
                    ResultPath = resultPath,
                    ChildProcessId = 12345,
                };
                var waiting = new FrameworkBuildSizeProbe.ProfileRecord
                {
                    Key = "ugui", Status = "等待",
                };
                var report = new FrameworkBuildSizeProbe.RunReport
                {
                    Profiles = new[] { building, waiting },
                };
                const string driftStopReason =
                    "检测到证据输入漂移；当前组合完成后停止：template changed";

                Assert.That(FrameworkBuildSizeProbe.TryApplyCompletedChildResultDuringDrift(
                    report, building, driftStopReason), Is.True);
                FrameworkBuildSizeProbe.CompleteWaitingProfiles(report);

                Assert.That(building.Status, Is.EqualTo("成功"));
                Assert.That(building.Message, Is.EqualTo("冻结输入构建完成。"));
                Assert.That(building.ChildProcessId, Is.Zero);
                Assert.That(report.StopAfterCurrentReason, Is.EqualTo(driftStopReason));
                Assert.That(waiting.Status, Is.EqualTo("跳过"));
                Assert.That(waiting.Message, Is.EqualTo(driftStopReason));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
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
                FormatVersion = FrameworkBuildSizeProbe.CurrentReportFormatVersion,
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
            StampCurrentEvidence(report);
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
        public void FailedChildLog_UsesBoundedTailWhenNoDiagnosticSignalExists()
        {
            string path = Path.Combine(Path.GetTempPath(), "SSFrameworkProbeTail-" + Guid.NewGuid() + ".log");
            try
            {
                File.WriteAllLines(path, Enumerable.Range(0, 2000).Select(index => "noise-" + index));

                string excerpt = FrameworkBuildSizeProbe.ReadDiagnosticLogExcerpt(path, 3);

                Assert.That(excerpt.Split('\n'),
                    Is.EqualTo(new[] { "noise-1997", "noise-1998", "noise-1999" }));
                Assert.That(FrameworkBuildSizeProbe.ReadDiagnosticLogExcerpt(path, 0), Is.Empty);
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
            FrameworkModuleAuditCache.Invalidate();
            var window = ScriptableObject.CreateInstance<FrameworkBuildSizeProbeWindow>();
            try
            {
                window.Show(false);
                window.position = new Rect(-10000f, -10000f, 360f, 520f);
                window.CreateGUI();
                Assert.That(window.rootVisualElement.panel, Is.Not.Null,
                    "键盘渐进披露必须在真实 UI Toolkit Panel 上验证。 ");

                var content = window.rootVisualElement.Q<ScrollView>("build-size-probe-content");
                var actions = window.rootVisualElement.Q<VisualElement>("build-size-probe-actions");
                var loader = window.rootVisualElement.Q<VisualElement>("build-size-probe-profile-loader");
                Assert.That(loader, Is.Not.Null);
                Assert.That(window.rootVisualElement.Q<Toggle>("build-size-probe-toggle-core"), Is.Null,
                    "打开体积窗口不得隐式执行 Module Audit 与全目录指纹扫描。 ");

                window.LoadProfilesForTests();
                Assert.That(FrameworkModuleAuditCache.TryGet(out FrameworkModuleAuditCache.Entry evidence),
                    Is.True);

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
                Assert.That(bridge, Is.Null,
                    "进阶区折叠时不应急切创建全部 Module 卡片。 ");
                int expectedProfiles = evidence.Result.CommonProfiles.Length + 1 +
                                       evidence.Result.ModuleProfiles.Length;
                Assert.That(window.rootVisualElement.Q<HelpBox>("build-size-probe-status").text,
                    Does.Contain($"已读取 {expectedProfiles} 个组合"),
                    "组合计数应描述完整可选集合，而不是只统计已经创建的折叠卡片。 ");

                Toggle advancedTitle = advanced.Q<Toggle>();
                Assert.That(advancedTitle, Is.Not.Null);
                using (NavigationMoveEvent evt = NavigationMoveEvent.GetPooled(
                           NavigationMoveEvent.Direction.Right, EventModifiers.None))
                {
                    evt.target = advancedTitle;
                    advancedTitle.SendEvent(evt);
                }
                Assert.That(advanced.value, Is.True,
                    "右方向键应通过 Foldout 的生产导航路径展开进阶组合。 ");
                bridge = window.rootVisualElement.Q<Toggle>(
                    "build-size-probe-toggle-module-game-framework-ui-bridge");
                Assert.That(bridge, Is.Not.Null);
                Assert.That(bridge.value, Is.False, "任意 Module 慢构建必须由用户按需选择，不能默认全跑。 ");
                bridge.value = true;
                advanced.value = false;
                window.LoadProfilesForTests();
                Assert.That(window.rootVisualElement.Q<Toggle>(
                    "build-size-probe-toggle-module-game-framework-ui-bridge"), Is.Null,
                    "刷新后折叠的进阶区仍应保持惰性，不能为了保存选择重建全部卡片。 ");
                advancedTitle = advanced.Q<Toggle>();
                using (NavigationMoveEvent evt = NavigationMoveEvent.GetPooled(
                           NavigationMoveEvent.Direction.Right, EventModifiers.None))
                {
                    evt.target = advancedTitle;
                    advancedTitle.SendEvent(evt);
                }
                bridge = window.rootVisualElement.Q<Toggle>(
                    "build-size-probe-toggle-module-game-framework-ui-bridge");
                Assert.That(bridge?.value, Is.True,
                    "执行证据刷新与折叠只更新视图，不能把用户已经选择的构建意图重置成默认值。 ");
                const string advancedHint =
                    "每项以一个 Runtime Module 为入口并自动带上真实依赖闭包；适合验证 Config、Fonts、Proto、Bridge 等任意 Module，不是全局启用开关。";
                Assert.That(window.rootVisualElement.Query<Label>().ToList()
                        .Count(label => string.Equals(label.text, advancedHint, StringComparison.Ordinal)),
                    Is.EqualTo(1), "刷新和展开不能重复添加同一段进阶说明。 ");
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
                window.Close();
                FrameworkModuleAuditCache.Invalidate();
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
