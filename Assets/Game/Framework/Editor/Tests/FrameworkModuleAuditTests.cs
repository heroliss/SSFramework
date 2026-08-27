using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor.Tests
{
    /// <summary>
    /// 锁定 Module 审计的当前编译快照引用闭包、删除测试与窄窗口结构；不把它冒充目标 Player 变体。
    /// </summary>
    public sealed class FrameworkModuleAuditTests
    {
        [Test]
        public void Reachability_UsesActualMetadataReferences()
        {
            var assemblies = new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
            {
                ["Core"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "Core",
                    ActualReferences = new[] { "Reactive", "netstandard" },
                },
                ["Reactive"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "Reactive",
                    ActualReferences = new[] { "ThirdParty" },
                },
                // 该程序集存在于编译图，却没有写进 Core 的真实元数据引用；不应仅因“编译可见”被算入。
                ["UnusedUi"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "UnusedUi",
                    ActualReferences = Array.Empty<string>(),
                },
            };

            var reachable = FrameworkModuleAudit.ComputeReachableAssemblies(
                assemblies,
                new[] { "Core" },
                name => name == "ThirdParty" ? new[] { "System.Runtime" } : Array.Empty<string>());

            Assert.That(reachable, Is.EquivalentTo(new[] { "Core", "Reactive", "ThirdParty" }));
            Assert.That(reachable, Does.Not.Contain("UnusedUi"));
        }

        [Test]
        public void DeclaredReachability_RemainsSeparateFromCurrentDllUsage()
        {
            var assemblies = new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
            {
                ["Core"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "Core",
                    DeclaredReferences = new[] { "Optional" },
                    ActualReferences = Array.Empty<string>(),
                },
                ["Optional"] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = "Optional",
                    DeclaredReferences = new[] { "Transitive" },
                    ActualReferences = Array.Empty<string>(),
                },
                ["Transitive"] = new FrameworkModuleAudit.AssemblyInfo { Name = "Transitive" },
            };

            var declared = FrameworkModuleAudit.ComputeDeclaredReachableAssemblies(
                assemblies, new[] { "Core" });
            var actual = FrameworkModuleAudit.ComputeReachableAssemblies(
                assemblies, new[] { "Core" });

            Assert.That(declared, Is.EquivalentTo(new[] { "Core", "Optional", "Transitive" }));
            Assert.That(actual, Is.EqualTo(new[] { "Core" }));
        }

        [Test]
        public void DependencyBoundaryChecks_CatchAnyOptionalModuleFromCoreOrBoot()
        {
            const string missingOptionalName = "Game.Framework.Optional.MissingFromPlayerCatalog";
            var core = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = FrameworkModuleAudit.CoreAssemblyName,
                DeclaredReferences = new[]
                {
                    "Project.DeclaredMediator",
                    FrameworkModuleAudit.BootAssemblyName,
                },
                ActualReferences = Array.Empty<string>(),
            };
            var boot = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = FrameworkModuleAudit.BootAssemblyName,
                ActualReferences = new[] { "Project.ActualMediator" },
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
                {
                    [core.Name] = core,
                    [boot.Name] = boot,
                    ["Project.DeclaredMediator"] = new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = "Project.DeclaredMediator",
                        DeclaredReferences = new[] { missingOptionalName },
                    },
                    ["Project.ActualMediator"] = new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = "Project.ActualMediator",
                        ActualReferences = new[] { missingOptionalName },
                    },
                },
                new Dictionary<string, string>(),
                Array.Empty<string>(),
                "test");

            FrameworkModuleAudit.DeletionCheck[] checks =
                FrameworkModuleAudit.BuildDependencyBoundaryChecks(snapshot);

            var coreCheck = checks.Single(check => check.Name.StartsWith("Core ", StringComparison.Ordinal));
            var bootCheck = checks.Single(check => check.Name.StartsWith("Boot ", StringComparison.Ordinal));
            Assert.That(coreCheck.Passed, Is.False,
                "声明传递边指向未进入 Player Catalog 的 Framework Module 仍会阻塞物理删除。 ");
            Assert.That(coreCheck.Explanation, Does.Contain(missingOptionalName));
            Assert.That(coreCheck.Explanation, Does.Contain(FrameworkModuleAudit.BootAssemblyName),
                "Core 反向引用可删除的 AOT Boot 也必须由同一通用门禁捕获。 ");
            Assert.That(bootCheck.Passed, Is.False,
                "AOT Boot 经当前 DLL 传递闭包接触缺失的 Framework Runtime Module 也必须失败。 ");
            Assert.That(bootCheck.Explanation, Does.Contain(missingOptionalName));
        }

        [Test]
        public void UndeclaredReferences_ExposeAutoReferenceCoupling()
        {
            var info = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.UI",
                DeclaredReferences = new[] { "Game.Framework", "UniTask" },
                ActualReferences = new[]
                {
                    "Game.Framework", "UniTask", "ObservableCollections", "ObservableCollections.R3", "netstandard",
                },
            };

            var hidden = FrameworkModuleAudit.FindUndeclaredDirectReferences(
                info,
                reference => reference != "Game.Framework" && reference != "netstandard");

            Assert.That(hidden, Is.EqualTo(new[] { "ObservableCollections", "ObservableCollections.R3" }));
        }

        [Test]
        public void UndeclaredReferences_DistinguishAsmdefAndPrecompiledDeclarations()
        {
            var info = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.UI",
                // 把 DLL 名误写进 references 的旧配置不能冒充有效的预编译引用声明。
                DeclaredReferences = new[] { "Game.Framework", "R3" },
                DeclaredPrecompiledReferences = new[] { "ObservableCollections" },
                ActualReferences = new[] { "Game.Framework", "R3", "ObservableCollections" },
            };

            var hidden = FrameworkModuleAudit.FindUndeclaredDirectReferences(
                info,
                _ => true,
                reference => reference is "R3" or "ObservableCollections");

            Assert.That(hidden, Is.EqualTo(new[] { "R3" }));
        }

        [Test]
        public void ExternalDependencyEvidence_GroupsPackageAndKeepsConsumerLayersSeparate()
        {
            var core = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = FrameworkModuleAudit.CoreAssemblyName,
                ActualReferences = new[] { "R3.Unity" },
            };
            var sources = new Dictionary<string, FrameworkModuleAudit.DependencySource>(StringComparer.Ordinal)
            {
                ["R3"] = PackageSource("R3", "com.cysharp.r3"),
                ["R3.Unity"] = PackageSource("R3.Unity", "com.cysharp.r3"),
            };
            var declared = new[]
            {
                new FrameworkModuleAudit.DeclaredConsumerEvidence
                {
                    DependencyAssemblyName = "R3.Unity",
                    ConsumerAssemblyName = core.Name,
                    ConsumerAsmdefPath = "Assets/Game/Framework/Core/Game.Framework.asmdef",
                    ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
                    PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                },
            };
            var actual = new[]
            {
                new FrameworkModuleAudit.ActualConsumerEvidence
                {
                    DependencyAssemblyName = "R3.Unity",
                    ConsumerAssemblyName = core.Name,
                    ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
                    PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                },
                new FrameworkModuleAudit.ActualConsumerEvidence
                {
                    DependencyAssemblyName = "R3",
                    ConsumerAssemblyName = "R3.Unity",
                    ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                    ConsumerPackageName = "com.cysharp.r3",
                    PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                },
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo> { [core.Name] = core },
                new Dictionary<string, string>(),
                Array.Empty<string>(),
                "test",
                declaredConsumers: declared,
                dependencySources: sources,
                actualConsumers: actual);
            var footprint = new FrameworkModuleAudit.Footprint();
            footprint.ExternalAssemblies["R3"] = 10;
            footprint.ExternalAssemblies["R3.Unity"] = 20;
            footprint.ExternalBytes = 30;
            var profile = new FrameworkModuleAudit.AuditProfile
            {
                Key = "core",
                Roots = new[] { core.Name },
                Footprint = footprint,
            };

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(snapshot, new[] { profile }).Single();

            Assert.That(evidence.Key, Is.EqualTo("upm:com.cysharp.r3"));
            Assert.That(evidence.Assemblies.Select(item => item.AssemblyName),
                Is.EquivalentTo(new[] { "R3", "R3.Unity" }));
            Assert.That(evidence.ProfileRawBytesByKey["core"], Is.EqualTo(30));
            Assert.That(evidence.MaxProfileRawBytes, Is.EqualTo(30));
            Assert.That(evidence.ActualConsumers.Select(item => item.ConsumerAssemblyName),
                Is.EqualTo(new[] { core.Name }),
                "同一 Package 内部边不能重复冒充项目直接消费者。 ");
            Assert.That(evidence.DeclaredConsumers, Has.Length.EqualTo(1));
            Assert.That(evidence.DirectProfileKeys, Is.EqualTo(new[] { "core" }));
            Assert.That(evidence.Role, Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.BaseRuntime));
            Assert.That(evidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.RequiredByCore));
        }

        [Test]
        public void ExternalDependencyEvidence_DistinguishesOptionalEditorAndUnknownRemovalStates()
        {
            var optional = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Optional",
                ActualReferences = new[] { "Optional.Plugin" },
            };
            FrameworkModuleAudit.DependencySource optionalSource = PackageSource(
                "Optional.Plugin", "com.example.optional");
            var optionalSnapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo> { [optional.Name] = optional },
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                declaredConsumers: new[]
                {
                    DeclaredEdge("Optional.Plugin", optional.Name,
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                },
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    ["Optional.Plugin"] = optionalSource,
                },
                actualConsumers: new[]
                {
                    ActualEdge("Optional.Plugin", optional.Name,
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                });
            var optionalFootprint = new FrameworkModuleAudit.Footprint();
            optionalFootprint.ExternalAssemblies["Optional.Plugin"] = 42;
            optionalFootprint.ExternalBytes = 42;
            var optionalProfile = new FrameworkModuleAudit.AuditProfile
            {
                Key = "module-game-framework-optional",
                Roots = new[] { optional.Name },
                Footprint = optionalFootprint,
            };
            var optionalEvidence = FrameworkModuleAudit.BuildExternalDependencyEvidence(
                optionalSnapshot, new[] { optionalProfile }).Single();
            Assert.That(optionalEvidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.RemoveWithOptionalModuleCandidate));

            var editorSource = new FrameworkModuleAudit.DependencySource
            {
                AssemblyName = "System.OptionalPlugin",
                AssetPath = "Assets/Plugins/Optional/System.OptionalPlugin.dll",
                SourceKind = FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
                IsPrecompiledAssembly = true,
                IsExternal = true,
            };
            var editorSnapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                declaredConsumers: new[]
                {
                    DeclaredEdge("System.OptionalPlugin", "Game.Framework.Odin.Editor",
                        FrameworkModuleAudit.ConsumerPlatformScope.Editor),
                },
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    ["System.OptionalPlugin"] = editorSource,
                },
                actualConsumers: new[]
                {
                    ActualEdge("System.OptionalPlugin", "Game.Framework.Odin.Editor",
                        FrameworkModuleAudit.ConsumerPlatformScope.Editor),
                });
            var editorEvidence = FrameworkModuleAudit.BuildExternalDependencyEvidence(
                editorSnapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();
            Assert.That(editorEvidence.Role, Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.EditorTool));
            Assert.That(editorEvidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.RemoveWithEditorToolCandidate));
            Assert.That(editorEvidence.Assemblies.Single().AssemblyName, Is.EqualTo("System.OptionalPlugin"),
                "已解析到 Assets 的 Editor-only System.* DLL 不能被 BCL 名称规则静默吞掉。 ");

            var unknownSnapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                declaredConsumers: new[]
                {
                    DeclaredEdge("Mystery", "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                },
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    ["Mystery"] = new FrameworkModuleAudit.DependencySource
                    {
                        AssemblyName = "Mystery",
                        SourceKind = FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                        IsExternal = true,
                    },
                },
                dependencyEvidenceIssues: new[]
                {
                    new FrameworkModuleAudit.EvidenceIssue { Code = "unknown", Message = "test" },
                });
            var unknownEvidence = FrameworkModuleAudit.BuildExternalDependencyEvidence(
                unknownSnapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();
            Assert.That(unknownEvidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.ReviewRequired));
            Assert.That(unknownEvidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.OptionalRuntime),
                "全局扫描问题只能收紧删除结论，不能抹掉已经成立的引入者角色证据。 ");
        }

        [Test]
        public void ExternalDependencyEvidence_IncludesActualOnlyConsumersAndKeepsStructuredScope()
        {
            const string module = "Game.Framework.ActualOnly";
            const string plugin = "Actual.Only.Plugin";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = PackageSource(plugin, "com.example.actual-only"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(plugin, module, FrameworkModuleAudit.ConsumerPlatformScope.Player),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.ActualConsumers, Has.Length.EqualTo(1));
            Assert.That(evidence.ActualConsumers[0].PlatformScope,
                Is.EqualTo(FrameworkModuleAudit.ConsumerPlatformScope.Player));
            Assert.That(evidence.Introducers.Select(item => item.ConsumerAssemblyName),
                Is.EqualTo(new[] { module }));
            Assert.That(evidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.OptionalRuntime));
            Assert.That(evidence.HasProfileMeasurement, Is.False,
                "Editor-only 或未进入 what-if 档位的程序集应显示未测得，而不是伪装成 0 B。 ");
        }

        [Test]
        public void ExternalDependencyEvidence_IncludesActualOnlyExternalAsmdefAlreadyInCompilationGraph()
        {
            const string packageAssembly = "External.Package.Asmdef";
            var source = PackageSource(packageAssembly, "com.example.asmdef");
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
                {
                    [packageAssembly] = new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = packageAssembly,
                        AsmdefPath = source.AssetPath,
                        PackageName = source.PackageName,
                        PackageVersion = source.PackageVersion,
                        PackageId = source.PackageId,
                        SourceKind = source.SourceKind,
                    },
                },
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [packageAssembly] = source,
                },
                actualConsumers: new[]
                {
                    ActualEdge(packageAssembly, "Project.Runtime",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.PackageName, Is.EqualTo("com.example.asmdef"));
            Assert.That(evidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.ProjectConsumer));
        }

        [Test]
        public void ExternalDependencyEvidence_DoesNotEnumeratePackageInternalEdgesWithoutFirstPartySeed()
        {
            const string facade = "Unseeded.Facade";
            const string leaf = "Unseeded.Leaf";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [facade] = PackageSource(facade, "com.example.facade"),
                    [leaf] = PackageSource(leaf, "com.example.leaf"),
                },
                actualConsumers: new[]
                {
                    new FrameworkModuleAudit.ActualConsumerEvidence
                    {
                        DependencyAssemblyName = leaf,
                        ConsumerAssemblyName = facade,
                        ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                        ConsumerPackageName = "com.example.facade",
                        PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                    },
                });

            Assert.That(FrameworkModuleAudit.BuildExternalDependencyEvidence(
                snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()), Is.Empty);
        }

        [Test]
        public void ExternalDependencyEvidence_SeparatesIntroducerFromProfilePropagation()
        {
            const string optional = "Game.Framework.Optional";
            const string upper = "Game.Framework.UI.UGui";
            const string plugin = "Optional.Plugin";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
                {
                    [optional] = new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = optional,
                        ActualReferences = new[] { plugin },
                    },
                    [upper] = new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = upper,
                        ActualReferences = new[] { optional },
                    },
                },
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = PackageSource(plugin, "com.example.optional"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(plugin, optional, FrameworkModuleAudit.ConsumerPlatformScope.Player),
                    ActualEdge(optional, upper, FrameworkModuleAudit.ConsumerPlatformScope.Player),
                });
            var optionalFootprint = new FrameworkModuleAudit.Footprint();
            optionalFootprint.ExternalAssemblies[plugin] = 10;
            var upperFootprint = new FrameworkModuleAudit.Footprint();
            upperFootprint.ExternalAssemblies[plugin] = 10;
            var profiles = new[]
            {
                new FrameworkModuleAudit.AuditProfile
                {
                    Key = "module-optional", Roots = new[] { optional }, Footprint = optionalFootprint,
                },
                new FrameworkModuleAudit.AuditProfile
                {
                    Key = "ugui", Roots = new[] { upper }, Footprint = upperFootprint,
                },
            };

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(snapshot, profiles).Single();

            Assert.That(evidence.AffectedProfileKeys, Is.EquivalentTo(new[] { "module-optional", "ugui" }));
            Assert.That(evidence.DirectProfileKeys, Is.EqualTo(new[] { "module-optional" }));
            Assert.That(evidence.TransitiveProfileKeys, Is.EqualTo(new[] { "ugui" }));
            Assert.That(evidence.Introducers.Select(item => item.ConsumerAssemblyName),
                Is.EqualTo(new[] { optional }),
                "上层入口只传播依赖，不应被重复计算为新的第三方依赖引入者。 ");
            Assert.That(evidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.OptionalRuntime));
        }

        [Test]
        public void ExternalDependencyEvidence_KeepsRawBytesPerProfileInsteadOfSummingIndependentMaxima()
        {
            const string module = "Game.Framework.Optional";
            const string first = "Split.Package.First";
            const string second = "Split.Package.Second";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
                {
                    [module] = new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = module,
                        ActualReferences = new[] { first },
                    },
                },
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [first] = PackageSource(first, "com.example.split"),
                    [second] = PackageSource(second, "com.example.split"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(first, module, FrameworkModuleAudit.ConsumerPlatformScope.Player),
                    new FrameworkModuleAudit.ActualConsumerEvidence
                    {
                        DependencyAssemblyName = second,
                        ConsumerAssemblyName = first,
                        ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                        ConsumerPackageName = "com.example.split",
                        PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                    },
                });
            var firstFootprint = new FrameworkModuleAudit.Footprint();
            firstFootprint.ExternalAssemblies[first] = 10;
            var secondFootprint = new FrameworkModuleAudit.Footprint();
            secondFootprint.ExternalAssemblies[second] = 20;
            var profiles = new[]
            {
                new FrameworkModuleAudit.AuditProfile
                {
                    Key = "first", Roots = new[] { module }, Footprint = firstFootprint,
                },
                new FrameworkModuleAudit.AuditProfile
                {
                    Key = "second", Roots = new[] { module }, Footprint = secondFootprint,
                },
            };

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(snapshot, profiles).Single();

            Assert.That(evidence.ProfileRawBytesByKey["first"], Is.EqualTo(10));
            Assert.That(evidence.ProfileRawBytesByKey["second"], Is.EqualTo(20));
            Assert.That(evidence.MaxProfileRawBytes, Is.EqualTo(20),
                "不同 Profile 中互斥出现的程序集不能被拼成一个不存在的 30 B 档位。 ");
        }

        [Test]
        public void ExternalDependencyEvidence_ScopedScanIssueOnlyTightensMatchingDependencyGroup()
        {
            const string plugin = "Scoped.Plugin";
            Dictionary<string, FrameworkModuleAudit.AssemblyInfo> HealthyFrameworkAssemblies() =>
                new[]
                    {
                        FrameworkModuleAudit.CoreAssemblyName,
                        FrameworkModuleAudit.UGuiAssemblyName,
                        FrameworkModuleAudit.ToolkitAssemblyName,
                    }
                    .ToDictionary(name => name, name => new FrameworkModuleAudit.AssemblyInfo
                    {
                        Name = name,
                        AutoReferenced = false,
                    }, StringComparer.Ordinal);
            FrameworkModuleAudit.Snapshot SnapshotWithIssue(string subject) => new(
                HealthyFrameworkAssemblies(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = PackageSource(plugin, "com.example.scoped"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(plugin, "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                },
                dependencyEvidenceIssues: new[]
                {
                    new FrameworkModuleAudit.EvidenceIssue
                    {
                        Code = "editor-assembly-missing",
                        Message = "test",
                        SubjectAssemblyName = subject,
                    },
                });

            FrameworkModuleAudit.Snapshot unrelatedSnapshot = SnapshotWithIssue("Other.Plugin");
            FrameworkModuleAudit.Snapshot matchingSnapshot = SnapshotWithIssue(plugin);
            FrameworkModuleAudit.ExternalDependencyEvidence unrelated =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    unrelatedSnapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();
            FrameworkModuleAudit.ExternalDependencyEvidence matching =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    matchingSnapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(unrelated.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.RemoveWithOptionalModuleCandidate));
            Assert.That(unrelated.EvidenceIssues, Is.Empty);
            Assert.That(matching.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.ReviewRequired));
            Assert.That(matching.EvidenceIssues.Select(item => item.Code),
                Does.Contain("editor-assembly-missing"));

            FrameworkModuleAudit.AuditResult unrelatedResult =
                FrameworkModuleAudit.Analyze(unrelatedSnapshot);
            FrameworkModuleAudit.AuditResult matchingResult =
                FrameworkModuleAudit.Analyze(matchingSnapshot);
            FrameworkModuleAudit.AuditResult globalResult =
                FrameworkModuleAudit.Analyze(SnapshotWithIssue(string.Empty));

            Assert.That(unrelatedResult.DependencyEvidenceIssues, Is.Empty,
                "可归属但不在一方依赖图中的问题不应冒充全局扫描失败。 ");
            Assert.That(unrelatedResult.DependencyEvidenceIssueCount, Is.Zero);
            Assert.That(unrelatedResult.RequiresAttention, Is.False);
            Assert.That(matchingResult.DependencyEvidenceIssues, Is.Empty,
                "scoped issue 只在匹配卡片显示，不能在顶部重复出现。 ");
            Assert.That(matchingResult.DependencyEvidenceIssueCount, Is.EqualTo(1));
            Assert.That(matchingResult.ExternalDependencies.Single().EvidenceIssues, Has.Length.EqualTo(1));
            Assert.That(matchingResult.RequiresAttention, Is.True);
            Assert.That(globalResult.DependencyEvidenceIssues, Has.Length.EqualTo(1));
            Assert.That(globalResult.DependencyEvidenceIssueCount, Is.EqualTo(1),
                "一条全局问题只能计数一次。 ");
            Assert.That(globalResult.ExternalDependencies.Single().EvidenceIssues, Is.Empty);
            Assert.That(globalResult.RequiresAttention, Is.True);
        }

        [Test]
        public void ExternalDependencyEvidence_EditorOnlyProjectConsumerWinsOverProjectRuntimeRole()
        {
            const string plugin = "Editor.Tool.Plugin";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = PackageSource(plugin, "com.example.editor-tool"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(plugin, "Project.Editor.Tool",
                        FrameworkModuleAudit.ConsumerPlatformScope.Editor),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.Role, Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.EditorTool));
            Assert.That(evidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.RemoveWithEditorToolCandidate));
        }

        [Test]
        public void ExternalDependencyEvidence_ProjectEditorPlusFrameworkRuntimeRequiresProjectMigration()
        {
            const string plugin = "Mixed.Consumer.Plugin";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = PackageSource(plugin, "com.example.mixed"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(plugin, "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                    ActualEdge(plugin, "Project.Editor.Tool",
                        FrameworkModuleAudit.ConsumerPlatformScope.Editor),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.ProjectConsumer));
            Assert.That(evidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.ProjectConsumerMigrationRequired));
        }

        [Test]
        public void ExternalDependencyEvidence_DoesNotCrossEditorAndPlayerVariantEdges()
        {
            const string facade = "Variant.Facade";
            const string editorLeaf = "Variant.Editor.Leaf";
            const string playerLeaf = "Variant.Player.Leaf";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [facade] = PackageSource(facade, "com.example.facade"),
                    [editorLeaf] = PackageSource(editorLeaf, "com.example.editor"),
                    [playerLeaf] = PackageSource(playerLeaf, "com.example.player"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(facade, "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                    new FrameworkModuleAudit.ActualConsumerEvidence
                    {
                        DependencyAssemblyName = editorLeaf,
                        ConsumerAssemblyName = facade,
                        ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                        PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Editor,
                    },
                    new FrameworkModuleAudit.ActualConsumerEvidence
                    {
                        DependencyAssemblyName = playerLeaf,
                        ConsumerAssemblyName = facade,
                        ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                        PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                    },
                });

            FrameworkModuleAudit.ExternalDependencyEvidence[] evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>());

            Assert.That(evidence.Select(item => item.PackageName),
                Does.Contain("com.example.player"));
            Assert.That(evidence.Select(item => item.PackageName),
                Does.Not.Contain("com.example.editor"),
                "Player 一方种子不能沿同 AssemblyName 的 Editor-only 变体串入依赖。 ");
        }

        [Test]
        public void ExternalDependencyEvidence_TestSeedCanTraverseEditorDependencyEdge()
        {
            const string facade = "Test.Facade";
            const string leaf = "Editor.Leaf";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [facade] = PackageSource(facade, "com.example.test-facade"),
                    [leaf] = PackageSource(leaf, "com.example.editor-leaf"),
                },
                actualConsumers: new[]
                {
                    ActualEdge(facade, "Project.Editor.Tests",
                        FrameworkModuleAudit.ConsumerPlatformScope.Tests),
                    new FrameworkModuleAudit.ActualConsumerEvidence
                    {
                        DependencyAssemblyName = leaf,
                        ConsumerAssemblyName = facade,
                        ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                        ConsumerPackageName = "com.example.test-facade",
                        PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Editor,
                    },
                });

            FrameworkModuleAudit.ExternalDependencyEvidence leafEvidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                        snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>())
                    .Single(item => item.PackageName == "com.example.editor-leaf");

            Assert.That(leafEvidence.Introducers.Single().PlatformScope,
                Is.EqualTo(FrameworkModuleAudit.ConsumerPlatformScope.Tests));
            Assert.That(leafEvidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.EditorTool));
        }

        [Test]
        public void ExternalDependencyEvidence_TracesAcrossExternalAssemblyRefsToFirstPartyIntroducer()
        {
            const string facade = "External.Facade";
            const string leaf = "External.Leaf";
            const string module = "Game.Framework.Optional";
            var sources = new Dictionary<string, FrameworkModuleAudit.DependencySource>
            {
                [facade] = PackageSource(facade, "com.example.facade"),
                [leaf] = PackageSource(leaf, "com.example.leaf"),
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: sources,
                actualConsumers: new[]
                {
                    ActualEdge(facade, module, FrameworkModuleAudit.ConsumerPlatformScope.Player),
                    new FrameworkModuleAudit.ActualConsumerEvidence
                    {
                        DependencyAssemblyName = leaf,
                        ConsumerAssemblyName = facade,
                        ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
                        ConsumerPackageName = "com.example.facade",
                        PlatformScope = FrameworkModuleAudit.ConsumerPlatformScope.Player,
                    },
                });

            FrameworkModuleAudit.ExternalDependencyEvidence leafEvidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                        snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>())
                    .Single(item => item.PackageName == "com.example.leaf");

            Assert.That(leafEvidence.Introducers.Select(item => item.ConsumerAssemblyName),
                Is.EqualTo(new[] { module }));
            Assert.That(leafEvidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.OptionalRuntime));
        }

        [TestCase("UNITY_EDITOR", "Editor")]
        [TestCase("!UNITY_EDITOR", "Player")]
        [TestCase("UNITY_INCLUDE_TESTS", "Tests")]
        [TestCase("!UNITY_INCLUDE_TESTS", "Mixed")]
        [TestCase("UNITY_EDITOR || DEVELOPMENT_BUILD", "Unknown")]
        public void DefineConstraintScope_RequiresExactUnambiguousTokens(
            string constraint,
            string expected)
        {
            Assert.That(FrameworkModuleAudit.ClassifyDefineConstraintScope(new[] { constraint }).ToString(),
                Is.EqualTo(expected));
        }

        [Test]
        public void PlatformScope_RejectsConflictingConstraintAndIncludePlatformEvidence()
        {
            Assert.That(FrameworkModuleAudit.ClassifyDefineConstraintScope(
                    new[] { "UNITY_INCLUDE_TESTS", "!UNITY_EDITOR" }),
                Is.EqualTo(FrameworkModuleAudit.ConsumerPlatformScope.Unknown));
            Assert.That(FrameworkModuleAudit.ClassifyPlatformScopeForTests(
                    new[] { "UNITY_EDITOR" },
                    new[] { "Standalone" },
                    Array.Empty<string>()),
                Is.EqualTo(FrameworkModuleAudit.ConsumerPlatformScope.Unknown));
            Assert.That(FrameworkModuleAudit.ClassifyPlatformScopeForTests(
                    new[] { "UNITY_INCLUDE_TESTS" },
                    new[] { "Editor" },
                    Array.Empty<string>()),
                Is.EqualTo(FrameworkModuleAudit.ConsumerPlatformScope.Tests));
        }

        [TestCase("Player", "Editor")]
        [TestCase("Mixed", "Editor")]
        [TestCase("Unknown", "Editor")]
        [TestCase("Tests", "Tests")]
        public void EditorSnapshotScope_NeverPretendsToBePlayerEvidence(
            string declared,
            string expected)
        {
            Assert.That(FrameworkModuleAudit.ClassifyEditorSnapshotScope(
                    Enum.Parse<FrameworkModuleAudit.ConsumerPlatformScope>(declared)).ToString(),
                Is.EqualTo(expected));
        }

        [Test]
        public void DependencySourceVariants_OnlyAcceptProvenPlatformExclusiveDlls()
        {
            var editor = new FrameworkModuleAudit.DependencySourceVariant
            {
                HasCompatibilityEvidence = true,
                IsEditorCompatible = true,
            };
            var player = new FrameworkModuleAudit.DependencySourceVariant
            {
                HasCompatibilityEvidence = true,
                IsActiveBuildTargetCompatible = true,
                CompatibleBuildTargets = new[] { "StandaloneWindows64" },
            };
            var overlapping = new FrameworkModuleAudit.DependencySourceVariant
            {
                HasCompatibilityEvidence = true,
                IsEditorCompatible = true,
                IsActiveBuildTargetCompatible = true,
                CompatibleBuildTargets = new[] { "StandaloneWindows64" },
            };
            var unknown = new FrameworkModuleAudit.DependencySourceVariant();
            var alsoUnknown = new FrameworkModuleAudit.DependencySourceVariant
            {
                HasCompatibilityEvidence = true,
            };

            Assert.That(FrameworkModuleAudit.AreDependencySourceVariantsPlatformExclusive(editor, player),
                Is.True);
            Assert.That(FrameworkModuleAudit.AreDependencySourceVariantsPlatformExclusive(editor, overlapping),
                Is.False);
            Assert.That(FrameworkModuleAudit.AreDependencySourceVariantsPlatformExclusive(editor, unknown),
                Is.False);
            Assert.That(FrameworkModuleAudit.AreDependencySourceVariantsPlatformExclusive(
                alsoUnknown,
                new FrameworkModuleAudit.DependencySourceVariant { HasCompatibilityEvidence = true }),
                Is.False,
                "只观察到 Editor/当前目标都不兼容，不能证明它们在 Android、iOS 等其他平台互斥。 ");
        }

        [Test]
        public void ExternalDependencyEvidence_DeclaredOnlyCoreStillKeepsCoreRole()
        {
            const string plugin = "Declared.Only.Plugin";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                declaredConsumers: new[]
                {
                    DeclaredEdge(plugin, FrameworkModuleAudit.CoreAssemblyName,
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                },
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = PackageSource(plugin, "com.example.declared-only"),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.Role, Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.BaseRuntime));
            Assert.That(evidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.RequiredByCore));
            Assert.That(evidence.Introducers.Select(item => item.ConsumerAssemblyName),
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
        }

        [Test]
        public void ExternalDependencyEvidence_UnknownPackageDirectnessDoesNotPretendTransitive()
        {
            const string plugin = "Directness.Unknown";
            FrameworkModuleAudit.DependencySource source = PackageSource(plugin, "com.example.unknown-depth");
            source.HasPackageDirectness = false;
            source.IsDirectPackageDependency = false;
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [plugin] = source,
                },
                actualConsumers: new[]
                {
                    ActualEdge(plugin, "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.RemovalSteps, Has.Some.Contains("层级当前不可用"));
            Assert.That(evidence.RemovalSteps, Has.None.Contains("间接 Package"));
        }

        [Test]
        public void ExternalDependencyEvidence_InconsistentPackageGroupForcesReviewWithoutErasingRole()
        {
            const string firstAssembly = "Example.First";
            const string secondAssembly = "Example.Second";
            FrameworkModuleAudit.DependencySource first = PackageSource(firstAssembly, "com.example.group");
            FrameworkModuleAudit.DependencySource second = PackageSource(secondAssembly, "com.example.group");
            second.PackageVersion = "2.0.0";
            second.PackageId = "com.example.group@2.0.0";
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                new Dictionary<string, string>(), Array.Empty<string>(), "test",
                dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                {
                    [firstAssembly] = first,
                    [secondAssembly] = second,
                },
                actualConsumers: new[]
                {
                    ActualEdge(firstAssembly, "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                    ActualEdge(secondAssembly, "Game.Framework.Optional",
                        FrameworkModuleAudit.ConsumerPlatformScope.Player),
                });

            FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                FrameworkModuleAudit.BuildExternalDependencyEvidence(
                    snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();

            Assert.That(evidence.EvidenceIssues.Select(item => item.Code),
                Does.Contain("package-version-inconsistent"));
            Assert.That(evidence.EvidenceIssues.Select(item => item.Code),
                Does.Contain("package-id-inconsistent"));
            Assert.That(evidence.Role,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRole.OptionalRuntime));
            Assert.That(evidence.RemovalState,
                Is.EqualTo(FrameworkModuleAudit.ExternalDependencyRemovalState.ReviewRequired));
        }

        [Test]
        public void PrecompiledAssemblyCapture_ReadsRealDllToDllEdgesAndKeepsPlatformScope()
        {
            string sourceAssembly = typeof(FrameworkModuleAuditTests).Assembly.Location;
            string directory = Path.Combine(Path.GetTempPath(), "SSFrameworkPrecompiledConsumerTests");
            Directory.CreateDirectory(directory);
            string copiedAssembly = Path.Combine(directory, "Precompiled.Consumer.dll");
            try
            {
                File.Copy(sourceAssembly, copiedAssembly, overwrite: true);
                var source = new FrameworkModuleAudit.DependencySource
                {
                    AssemblyName = "Precompiled.Consumer",
                    SourceKind = FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
                    IsExternal = true,
                    IsPrecompiledAssembly = true,
                    Variants = new[]
                    {
                        new FrameworkModuleAudit.DependencySourceVariant
                        {
                            AssetPath = "Assets/Plugins/Editor/Precompiled.Consumer.dll",
                            PhysicalPath = copiedAssembly,
                            HasCompatibilityEvidence = true,
                            IsEditorCompatible = true,
                        },
                    },
                };

                FrameworkModuleAudit.ActualConsumerEvidence[] edges =
                    FrameworkModuleAudit.ReadPrecompiledActualConsumers(source);

                Assert.That(edges, Is.Not.Empty);
                Assert.That(edges.Select(item => item.DependencyAssemblyName),
                    Does.Contain("nunit.framework"));
                Assert.That(edges.All(item => item.ConsumerAssemblyName == source.AssemblyName), Is.True);
                Assert.That(edges.All(item =>
                    item.PlatformScope == FrameworkModuleAudit.ConsumerPlatformScope.Editor), Is.True);

                var snapshot = new FrameworkModuleAudit.Snapshot(
                    new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>(),
                    new Dictionary<string, string>(), Array.Empty<string>(), "test",
                    dependencySources: new Dictionary<string, FrameworkModuleAudit.DependencySource>
                    {
                        [source.AssemblyName] = source,
                    },
                    actualConsumers: new[]
                    {
                        ActualEdge(source.AssemblyName, "Project.Editor.Tool",
                            FrameworkModuleAudit.ConsumerPlatformScope.Editor),
                    });
                FrameworkModuleAudit.ExternalDependencyEvidence evidence =
                    FrameworkModuleAudit.BuildExternalDependencyEvidence(
                        snapshot, Array.Empty<FrameworkModuleAudit.AuditProfile>()).Single();
                Assert.That(evidence.HasProfileMeasurement, Is.False);
                Assert.That(evidence.HasInstalledBinaryMeasurement, Is.True);
                Assert.That(evidence.InstalledBinaryBytes, Is.GreaterThan(0));
            }
            finally
            {
                if (File.Exists(copiedAssembly)) File.Delete(copiedAssembly);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }

        [Test]
        public void PrecompiledAssemblyCapture_ReportsMissingAndCorruptDllsAsEvidenceIssues()
        {
            string directory = Path.Combine(Path.GetTempPath(), "SSFrameworkPrecompiledIssueTests");
            Directory.CreateDirectory(directory);
            string corruptPath = Path.Combine(directory, "Corrupt.dll");
            string missingPath = Path.Combine(directory, "Missing.dll");
            try
            {
                File.WriteAllBytes(corruptPath, new byte[] { 0x53, 0x53, 0x46 });
                FrameworkModuleAudit.DependencySource Source(string physicalPath) => new()
                {
                    AssemblyName = Path.GetFileNameWithoutExtension(physicalPath),
                    IsExternal = true,
                    IsPrecompiledAssembly = true,
                    Variants = new[]
                    {
                        new FrameworkModuleAudit.DependencySourceVariant
                        {
                            AssetPath = "Assets/Plugins/" + Path.GetFileName(physicalPath),
                            PhysicalPath = physicalPath,
                        },
                    },
                };
                var issues = new List<FrameworkModuleAudit.EvidenceIssue>();

                Assert.That(FrameworkModuleAudit.ReadPrecompiledActualConsumers(
                    Source(missingPath), issues.Add), Is.Empty);
                Assert.That(FrameworkModuleAudit.ReadPrecompiledActualConsumers(
                    Source(corruptPath), issues.Add), Is.Empty);

                Assert.That(issues.Select(item => item.Code),
                    Does.Contain("precompiled-assembly-missing"));
                Assert.That(issues.Select(item => item.Code),
                    Does.Contain("precompiled-assembly-metadata-unreadable"));
                Assert.That(issues.Select(item => item.SubjectAssemblyName),
                    Is.EquivalentTo(new[] { "Missing", "Corrupt" }));
            }
            finally
            {
                if (File.Exists(corruptPath)) File.Delete(corruptPath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }

        [Test]
        public void LinkXml_SeparatesUnconditionalAndConditionalRoots()
        {
            var rules = FrameworkModuleAudit.ParseLinkerPreservations(@"
<linker>
  <assembly fullname=""Game.Optional"" preserve=""all"" />
  <assembly fullname=""ThirdParty, Version=1.0.0.0"" ignoreIfUnreferenced=""1"">
    <type fullname=""ThirdParty.Entry"" preserve=""all"" />
  </assembly>
</linker>", "Assets/Optional/link.xml", "Game.Optional");

            Assert.That(rules, Has.Length.EqualTo(2));
            Assert.That(rules[0].AssemblyName, Is.EqualTo("Game.Optional"));
            Assert.That(rules[0].IsUnconditional, Is.True);
            Assert.That(rules[1].AssemblyName, Is.EqualTo("ThirdParty"));
            Assert.That(rules[1].IgnoreIfUnreferenced, Is.True);
            Assert.That(rules[1].Scope, Does.Contain("1 条"));
        }

        [Test]
        public void LinkXml_RequiredZeroTypeRulesDoNotCreateRoots()
        {
            var rules = FrameworkModuleAudit.ParseLinkerPreservations(@"
<linker>
  <assembly fullname=""Game.Optional"">
    <type fullname=""Game.Optional.Entry"" preserve=""all"" required=""0"" />
  </assembly>
</linker>", "Assets/Optional/link.xml", "Game.Optional");

            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].RequiredOnlyIfReferenced, Is.True);
            Assert.That(rules[0].IsUnconditional, Is.False);
        }

        [Test]
        public void ModuleStatus_ExplainsConsumersHotUpdateAndLinkerRoots()
        {
            var optional = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Optional",
                DeclaredReferences = new[] { FrameworkModuleAudit.CoreAssemblyName, "ThirdParty" },
                ActualReferences = new[] { FrameworkModuleAudit.CoreAssemblyName, "ThirdParty" },
            };
            var consumer = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Project.Runtime",
                AsmdefPath = "Assets/Project/Project.Runtime.asmdef",
                ActualReferences = new[] { optional.Name },
            };
            var assemblies = new Dictionary<string, FrameworkModuleAudit.AssemblyInfo>
            {
                [optional.Name] = optional,
                [consumer.Name] = consumer,
                [FrameworkModuleAudit.CoreAssemblyName] = new FrameworkModuleAudit.AssemblyInfo
                {
                    Name = FrameworkModuleAudit.CoreAssemblyName,
                    ActualReferences = Array.Empty<string>(),
                },
            };
            var rule = new FrameworkModuleAudit.LinkerPreservation
            {
                OwnerModuleName = optional.Name,
                Path = "Assets/Game/Framework/Optional/link.xml",
                AssemblyName = "ThirdParty",
                Scope = "preserve=all",
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                assemblies,
                new Dictionary<string, string>(),
                new[] { FrameworkModuleAudit.CoreAssemblyName, optional.Name },
                "test",
                new[] { rule },
                new Dictionary<string, string[]>
                {
                    [optional.Name] = new[] { "Sample.Editor.Consumer", "Project.Runtime" },
                });

            var status = FrameworkModuleAudit.BuildModuleStatuses(snapshot, new[] { optional }).Single();

            Assert.That(status.DirectConsumers, Is.EqualTo(new[] { "Project.Runtime" }));
            Assert.That(status.PredefinedAutoReferenceDisabled, Is.True);
            Assert.That(status.FrameworkConsumers, Is.Empty);
            Assert.That(status.ProjectConsumers, Is.EqualTo(new[] { "Project.Runtime" }));
            Assert.That(status.RemovalBlockers, Is.EquivalentTo(new[] { "Project.Runtime", "Sample.Editor.Consumer" }));
            Assert.That(status.HotUpdateDependencies,
                Is.EqualTo(new[] { FrameworkModuleAudit.CoreAssemblyName }));
            Assert.That(status.IsHotUpdateRoot, Is.True);
            Assert.That(status.HasUnconditionalPreservation, Is.True);
            Assert.That(status.RetentionReasons, Has.Some.Contains("CodePackage"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("Project.Runtime"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("ThirdParty"));
            Assert.That(status.RetentionReasons, Has.Some.Contains("AOT → 热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("FrameworkHotUpdateProfile"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("不要先单独同步取消热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("Sample.Editor.Consumer"));
        }

        [Test]
        public void ModuleStatus_FailsHealthWhenAotModuleReferencesHotAssembly()
        {
            var optional = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Optional",
                DeclaredReferences = new[] { FrameworkModuleAudit.CoreAssemblyName },
                ActualReferences = Array.Empty<string>(),
            };
            var snapshot = new FrameworkModuleAudit.Snapshot(
                new Dictionary<string, FrameworkModuleAudit.AssemblyInfo> { [optional.Name] = optional },
                new Dictionary<string, string>(),
                new[] { FrameworkModuleAudit.CoreAssemblyName },
                "test");

            var status = FrameworkModuleAudit.BuildModuleStatuses(snapshot, new[] { optional }).Single();
            var result = new FrameworkModuleAudit.AuditResult
            {
                ModuleStatuses = new[] { status },
                AllRuntimeModulesHavePredefinedAutoReferenceDisabled = true,
            };

            Assert.That(status.HasHotUpdateViolation, Is.True);
            Assert.That(status.RetentionReasons, Has.Some.Contains("非法的 AOT → 热更"));
            Assert.That(status.RemovalSteps, Has.Some.Contains("非法中间状态"));
            Assert.That(result.IsHealthy, Is.False);
        }

        [Test]
        public void LinkerOwner_UsesUniqueDeepestModuleAndNeverGuessesAmbiguousDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "SSFrameworkLinkOwner");
            string shared = Path.Combine(root, "Shared");
            var sharedA = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.SharedA",
                SourceDirectory = shared,
            };
            var sharedB = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.SharedB",
                SourceDirectory = shared,
            };
            Assert.That(FrameworkModuleAudit.ResolveLinkerOwner(
                Path.Combine(shared, "link.xml"), new[] { sharedA, sharedB }), Is.Empty,
                "同一物理目录的多个 asmdef 必须保持歧义，不能按枚举顺序猜 owner。");

            var parent = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Parent",
                SourceDirectory = root,
            };
            var child = new FrameworkModuleAudit.AssemblyInfo
            {
                Name = "Game.Framework.Child",
                SourceDirectory = Path.Combine(root, "Child"),
            };
            Assert.That(FrameworkModuleAudit.ResolveLinkerOwner(
                    Path.Combine(root, "Child", "link.xml"), new[] { parent, child }),
                Is.EqualTo(child.Name));
            Assert.That(FrameworkModuleAudit.ResolveLinkerOwner(
                    Path.Combine(Path.GetDirectoryName(root)!, "link.xml"), new[] { parent, child }),
                Is.Empty, "位于所有 asmdef 上层的 package 根规则应保留为全局/Package 证据。");
        }

        [Test]
        public void HotUpdateInspection_MapsBuildEvidenceWithoutCompileTimeDependency()
        {
            var target = new FrameworkModuleAudit.HotUpdateDeploymentEvidence
            {
                BuildModuleAvailable = true,
                ProfileAvailable = true,
                ProfileCount = 1,
                ProfilePath = "Assets/Game/Settings/Profile.asset",
                ProfileAssemblies = new[] { "Old" },
            };
            var raw = new FakeHotUpdateEvidence
            {
                ProfileAssemblies = new[] { "Core", "Game.Main" },
                HybridClrSettingsAssemblies = new[] { "Core" },
                HybridClrLegacyAssemblies = new[] { "Legacy" },
                SettingsAvailable = true,
                SettingsMatch = false,
                SettingsMessage = "settings drift",
                GenerationRequired = true,
                GenerationFresh = false,
                GenerationMessage = "stamp stale",
                StagingRequired = true,
                StagedManifestExists = true,
                StagedManifestAvailable = true,
                StagedManifestMatches = false,
                StagedVersion = "42",
                StagedAssemblies = new[] { "Core" },
                ExpectedAotMetadataDlls = new[] { "mscorlib.dll" },
                StagedAotMetadataDlls = Array.Empty<string>(),
                MissingStagedFiles = new[] { "Game.Main.dll" },
                UnexpectedStagedFiles = new[] { "Legacy.dll.bytes" },
                InvalidStagedEntries = new[] { "../Unsafe.dll" },
                StagedMessage = "manifest drift",
            };

            FrameworkModuleAudit.ApplyHotUpdateInspection(target, raw);

            Assert.That(target.InspectionAvailable, Is.True);
            Assert.That(target.ProfileAssemblies, Is.EqualTo(new[] { "Core", "Game.Main" }));
            Assert.That(target.SettingsAssemblies, Is.EqualTo(new[] { "Core" }));
            Assert.That(target.LegacySettingsAssemblies, Is.EqualTo(new[] { "Legacy" }));
            Assert.That(target.StagingRequired, Is.True);
            Assert.That(target.StagedManifestExists, Is.True);
            Assert.That(target.StagedVersion, Is.EqualTo("42"));
            Assert.That(target.ExpectedAotMetadataDlls, Is.EqualTo(new[] { "mscorlib.dll" }));
            Assert.That(target.MissingStagedFiles, Is.EqualTo(new[] { "Game.Main.dll" }));
            Assert.That(target.UnexpectedStagedFiles, Is.EqualTo(new[] { "Legacy.dll.bytes" }));
            Assert.That(target.InvalidStagedEntries, Is.EqualTo(new[] { "../Unsafe.dll" }));
            Assert.That(target.RequiresAttention, Is.True);
        }

        [Test]
        public void HotUpdateAttention_DistinguishesMissingProfileFromOptionalPureAotStage()
        {
            var missingProfile = new FrameworkModuleAudit.HotUpdateDeploymentEvidence
            {
                BuildModuleAvailable = true,
            };
            Assert.That(missingProfile.RequiresAttention, Is.True,
                "缺少热更配置这一唯一真源时不能静默判作纯 AOT；配置必须由工作台明确创建。 ");

            var pureAot = new FrameworkModuleAudit.HotUpdateDeploymentEvidence
            {
                BuildModuleAvailable = true,
                ProfileAvailable = true,
                ProfileCount = 1,
                InspectionAvailable = true,
                SettingsAvailable = true,
                SettingsMatch = true,
                GenerationRequired = false,
                StagingRequired = false,
                StagedManifestExists = false,
                StagedManifestAvailable = false,
                StagedManifestMatches = true,
            };
            Assert.That(pureAot.RequiresAttention, Is.False,
                "空 Profile 且直接 AOT composition root 可以不需要 CodePackage。 ");

            pureAot.StagedManifestExists = true;
            pureAot.StagedManifestAvailable = true;
            pureAot.StagedManifestMatches = false;
            Assert.That(pureAot.RequiresAttention, Is.True,
                "纯 AOT 虽不要求中转，但磁盘上已有漂移清单时应提醒，避免误部署旧代码包。 ");

            pureAot.StagedManifestMatches = true;
            pureAot.ProfileCount = 2;
            Assert.That(pureAot.RequiresAttention, Is.True,
                "多个 Profile 会破坏单一真源，即使首项派生证据全绿也必须提醒。 ");
        }

        [Test]
        public void InstalledModules_ExternalDependenciesAreExplicit_AndDeletionTestsHold()
        {
            var snapshot = FrameworkModuleAudit.Capture();
            FrameworkModuleAudit.AssemblyInfo[] firstPartyAssemblies = snapshot.Assemblies.Values
                .Where(module => module.SourceKind == FrameworkModuleSourceCatalog.SourceKind.ProjectAssets ||
                                 module.Name.Equals(FrameworkModuleAudit.CoreAssemblyName, StringComparison.Ordinal) ||
                                 module.Name.StartsWith(
                                     FrameworkModuleAudit.CoreAssemblyName + ".", StringComparison.Ordinal))
                .ToArray();
            Assert.That(firstPartyAssemblies, Has.Some.Matches<FrameworkModuleAudit.AssemblyInfo>(module =>
                module.Name == FrameworkModuleAudit.CoreAssemblyName),
                "Framework 搬到 Packages 后门禁仍必须覆盖 Core，不能因 Assets 路径过滤变成零迭代假绿。 ");
            foreach (var module in firstPartyAssemblies)
            {
                Assert.That(module.AsmdefPath,
                    Does.StartWith("Assets/").Or.StartWith("Packages/"),
                    module.Name + " 应保留可由 Unity 定位的稳定 Asset Path。");
                Assert.That(Directory.Exists(module.SourceDirectory), Is.True,
                    module.Name + " 应保留可由 System.IO 读取的真实物理源码目录。");
                var hidden = FrameworkModuleAudit.FindUndeclaredExternalReferences(snapshot, module);
                Assert.That(hidden, Is.Empty,
                    $"{module.Name} 的当前 DLL 快照存在 asmdef 不可见的外部依赖；门禁覆盖所有一方 Player 程序集，而不只 Framework Module");
                if (module.DeclaredPrecompiledReferences.Length > 0)
                    Assert.That(module.OverrideReferences, Is.True,
                        $"{module.Name} 的 precompiledReferences 只有在 overrideReferences=true 时才生效");
            }

            var core = FrameworkModuleAudit.ComputeReachableAssemblies(
                snapshot.Assemblies, new[] { FrameworkModuleAudit.CoreAssemblyName });
            Assert.That(core, Does.Not.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
            FrameworkModuleAudit.AssemblyInfo sharedUi = snapshot.Assemblies[FrameworkModuleAudit.SharedUiAssemblyName];
            Assert.That(sharedUi.DeclaredReferences, Does.Not.Contain("Unity.InputSystem"),
                "物理返回输入属于项目 composition layer，UI Core 的 asmdef 不得重新绑定 Input System。");
            var sharedUiActualClosure = FrameworkModuleAudit.ComputeReachableAssemblies(
                snapshot.Assemblies, new[] { FrameworkModuleAudit.SharedUiAssemblyName });
            Assert.That(sharedUiActualClosure, Does.Not.Contain("Unity.InputSystem"),
                "当前 UI Core DLL 也不得经隐藏引用把 Input System 带回托管闭包。");
            if (snapshot.Assemblies.ContainsKey(FrameworkModuleAudit.UGuiAssemblyName))
            {
                var ugui = FrameworkModuleAudit.ComputeReachableAssemblies(
                    snapshot.Assemblies, new[] { FrameworkModuleAudit.UGuiAssemblyName });
                Assert.That(ugui, Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
                Assert.That(ugui, Does.Not.Contain(FrameworkModuleAudit.ToolkitAssemblyName));
                Assert.That(ugui, Does.Not.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            }
            if (snapshot.Assemblies.ContainsKey(FrameworkModuleAudit.ToolkitAssemblyName))
            {
                var toolkit = FrameworkModuleAudit.ComputeReachableAssemblies(
                    snapshot.Assemblies, new[] { FrameworkModuleAudit.ToolkitAssemblyName });
                Assert.That(toolkit, Does.Contain(FrameworkModuleAudit.SharedUiAssemblyName));
                Assert.That(toolkit, Does.Not.Contain(FrameworkModuleAudit.UGuiAssemblyName));
                Assert.That(toolkit, Does.Not.Contain(FrameworkModuleAudit.BridgeAssemblyName));
            }

            var result = FrameworkModuleAudit.Analyze(snapshot);
            Assert.That(result.IsHealthy, Is.True);
            Assert.That(result.AllRuntimeModulesHavePredefinedAutoReferenceDisabled, Is.True,
                "autoReferenced:false 只关闭预定义程序集的隐式引用，字段名称不得继续暗示 Module 已按需部署。 ");
            Assert.That(result.DeletionChecks.Single(check =>
                check.Name.StartsWith("Core ", StringComparison.Ordinal)).Passed, Is.True,
                "Core 门禁必须覆盖全部可选 Runtime Module，而不只 UI 特例。 ");
            Assert.That(result.DeletionChecks.Single(check =>
                check.Name.StartsWith("Boot ", StringComparison.Ordinal)).Passed, Is.True,
                "Boot 必须保持不接触 Framework Runtime 的 AOT 薄壳边界。 ");
            Assert.That(result.DependencyEvidenceIssues, Is.Empty,
                "第三方依赖扫描不能把 asmdef / Editor DLL / PluginImporter 读取失败静默解释为零消费者。 ");
            Assert.That(result.ExternalDependencies, Is.Not.Empty);
            Assert.That(result.ExternalDependencies.SelectMany(item => item.EvidenceIssues
                    .Select(issue => item.DisplayName + " · " + issue)), Is.Empty,
                "同一依赖组的来源、版本、directness 或 DLL 变体证据不应互相冲突。 ");
            Assert.That(result.ExternalDependencies.Where(item => item.HasUnknownSource)
                    .Select(item => item.DisplayName),
                Is.Empty,
                "当前项目的外部依赖都应能还原到 Package 或 Assets DLL，不应长期停留在 Unknown。 ");
            var bridgeStatus = result.ModuleStatuses.FirstOrDefault(status =>
                status.Module.Name == FrameworkModuleAudit.BridgeAssemblyName);
            if (bridgeStatus != null)
                foreach (string blocker in bridgeStatus.RemovalBlockers)
                    Assert.That(bridgeStatus.RemovalSteps, Has.Some.Contains(blocker),
                        "物理删除计划必须列出当前完整 asmdef 图实际发现的每个声明引用。");
            if (result.HotUpdateDeployment.BuildModuleAvailable)
                Assert.That(result.HotUpdateDeployment.InspectionAvailable, Is.True,
                    "安装 Build Editor Module 时，通用审计应经只读反射接缝读取证据，不能建立编译期反向依赖。");
            Assert.That(result.Recommendations, Has.Some.Contains("Player BuildReport"));
            if (result.HasRetentionWarnings)
                Assert.That(result.Recommendations, Has.Some.Contains("link.xml"));

            string report = FrameworkModuleAudit.CreateReport(result);
            Assert.That(report, Does.Not.Contain("⚠ 无法定位程序集文件"),
                "当前轻量档位或热更清单里存在无法解析的程序集时，字节闭包不能算完整。");
            string deletionSection = report.Substring(
                report.IndexOf("删除检查（asmdef 声明 + 当前 DLL 元数据闭包）", StringComparison.Ordinal));
            Assert.That(deletionSection, Does.Not.Contain("✗ "),
                "删除检查的文本结论不得只靠测试代码另算后假绿；本地 Generate / 中转证据可在干净 clone 中独立告警。 ");
            Assert.That(report, Does.Contain("Module 当前保留原因"));
            Assert.That(report, Does.Contain("全局与生成的 link.xml 证据"));
            Assert.That(report, Does.Contain("热更派生证据（只读）"));
            Assert.That(report, Does.Contain("第三方依赖证据目录"));
            if (result.HotUpdateDeployment.BuildModuleAvailable)
                Assert.That(report, Does.Contain("CodePackage"));
        }

        [Test]
        public void AllFirstPartyAsmdefs_DisableGlobalPrecompiledAutoReference()
        {
            var asmdefNames = new HashSet<string>(
                AssetDatabase.GetAllAssetPaths()
                    .Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                    .Select(ReadAsmdefDeclaration)
                    .Select(declaration => declaration?.name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.Ordinal);
            var dllFiles = new HashSet<string>(
                PluginImporter.GetAllImporters()
                    .Select(importer => Path.GetFileName(importer.assetPath))
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var dllNames = new HashSet<string>(
                dllFiles.Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrWhiteSpace(name) && !asmdefNames.Contains(name)),
                StringComparer.OrdinalIgnoreCase);
            dllNames.UnionWith(PluginImporter.GetAllImporters()
                .Select(FrameworkModuleAudit.ReadManagedPluginAssemblyIdentity)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !asmdefNames.Contains(name)));
            var issues = new List<string>();

            foreach (string path in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)))
            {
                var declaration = ReadAsmdefDeclaration(path);
                if (declaration == null) continue;
                bool firstParty = path.StartsWith("Assets/Game/", StringComparison.Ordinal) ||
                                  declaration.name.Equals(
                                      FrameworkModuleAudit.CoreAssemblyName, StringComparison.Ordinal) ||
                                  declaration.name.StartsWith(
                                      FrameworkModuleAudit.CoreAssemblyName + ".", StringComparison.Ordinal);
                if (!firstParty) continue;
                if (!declaration.overrideReferences)
                    issues.Add($"{path}: 一方 asmdef 必须用 overrideReferences=true 关闭预编译 DLL 的全局 Auto Reference");
                string[] misplaced = (declaration.references ?? Array.Empty<string>())
                    .Where(reference => FrameworkModuleAudit.IsPrecompiledAssemblyReference(
                        reference, asmdefNames, dllNames))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                if (misplaced.Length > 0)
                    issues.Add($"{path}: DLL 写进 references（{string.Join(", ", misplaced)}）");
                if ((declaration.precompiledReferences?.Length ?? 0) > 0 &&
                    !declaration.overrideReferences)
                    issues.Add($"{path}: precompiledReferences 非空但 overrideReferences=false");
                foreach (string reference in declaration.precompiledReferences ?? Array.Empty<string>())
                {
                    if (!reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        issues.Add($"{path}: precompiledReferences 必须写带 .dll 后缀的文件名（{reference}）");
                    else if (!dllFiles.Contains(Path.GetFileName(reference)))
                        issues.Add($"{path}: precompiledReferences 找不到对应 PluginImporter（{reference}）");
                }
            }

            Assert.That(issues, Is.Empty,
                "一方 Runtime、Editor 与测试 asmdef 都必须关闭预编译 DLL 的全局 Auto Reference；" +
                "需要的 DLL 放进带后缀的 precompiledReferences。\n" +
                string.Join("\n", issues));
        }

        [Test]
        public void OptionalEditorAsmdefs_DisablePredefinedAssemblyAutoReference()
        {
            var issues = new List<string>();
            foreach (string path in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.StartsWith("Assets/Game/Framework/", StringComparison.Ordinal) &&
                                        path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)))
            {
                var declaration = ReadAsmdefDeclaration(path);
                if (declaration == null ||
                    declaration.name == "Game.Framework.Editor" ||
                    !(declaration.includePlatforms ?? Array.Empty<string>())
                    .Contains("Editor", StringComparer.OrdinalIgnoreCase))
                    continue;
                if (declaration.autoReferenced)
                    issues.Add($"{path}: 可删除 Editor asmdef 必须设置 autoReferenced=false；" +
                               "业务 Editor 程序集需要使用时应显式声明 references");
            }

            Assert.That(issues, Is.Empty,
                "可删除 Editor Module 不应让 Assembly-CSharp-Editor 获得隐式引用；" +
                "InitializeOnLoad 与菜单加载不依赖 autoReferenced=true。\n" + string.Join("\n", issues));
        }

        [Test]
        public void AsmdefDeclaration_MissingAutoReferencedUsesUnityDefaultTrue()
        {
            var omitted = JsonUtility.FromJson<AsmdefDeclaration>("{\"name\":\"MissingField\"}");
            var explicitFalse = JsonUtility.FromJson<AsmdefDeclaration>(
                "{\"name\":\"ExplicitFalse\",\"autoReferenced\":false}");

            Assert.That(omitted.autoReferenced, Is.True,
                "Unity 对省略 autoReferenced 的 asmdef 按 true 处理，门禁 DTO 必须采用同一默认值，不能假绿。 ");
            Assert.That(explicitFalse.autoReferenced, Is.False);
        }

        [Test]
        public void ManagedAssemblyIdentity_ComesFromDllMetadataInsteadOfFileName()
        {
            string source = typeof(FrameworkModuleAuditTests).Assembly.Location;
            string directory = Path.Combine(Path.GetTempPath(), "SSFrameworkAssemblyIdentityTests");
            Directory.CreateDirectory(directory);
            string renamed = Path.Combine(directory, "Renamed.Plugin.dll");
            try
            {
                File.Copy(source, renamed, overwrite: true);
                string identity = FrameworkModuleAudit.ReadManagedAssemblyIdentity(renamed);
                Assert.That(identity, Is.EqualTo("Game.Framework.Editor.Tests"));
                Assert.That(FrameworkModuleAudit.IsPrecompiledAssemblyReference(
                        identity,
                        new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(new[] { identity }, StringComparer.Ordinal)),
                    Is.True,
                    "重命名 DLL 的内部 AssemblyName 若误写进 references，字段门禁也必须识别。 ");
            }
            finally
            {
                if (File.Exists(renamed)) File.Delete(renamed);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }

        [Test]
        public void Window_UsesProgressiveDisclosureAndResponsiveRows()
        {
            var window = ScriptableObject.CreateInstance<FrameworkModuleAuditWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 360f, 520f);
                window.CreateGUI();

                var actions = window.rootVisualElement.Q<VisualElement>("module-audit-actions");
                var content = window.rootVisualElement.Q<ScrollView>("module-audit-content");
                var summary = window.rootVisualElement.Q<VisualElement>("module-audit-summary");
                var coreProfile = window.rootVisualElement.Q<VisualElement>("module-audit-profile-core");
                var raw = window.rootVisualElement.Q<Foldout>("module-audit-raw-details");
                var statuses = window.rootVisualElement.Q<Foldout>("module-audit-module-statuses");
                var attentionStatuses = window.rootVisualElement.Q<Foldout>(
                    "module-audit-attention-statuses");
                var moduleProfiles = window.rootVisualElement.Q<Foldout>("module-audit-module-profiles");
                var globalPreservations = window.rootVisualElement.Q<Foldout>(
                    "module-audit-global-preservations");
                var hotUpdateEvidence = window.rootVisualElement.Q<VisualElement>(
                    "module-audit-hot-update-evidence");
                var hotUpdateMetrics = window.rootVisualElement.Q<VisualElement>(
                    "module-audit-hot-update-metrics");
                var externalSummary = window.rootVisualElement.Q<VisualElement>(
                    "module-audit-external-summary");
                var externalCatalog = window.rootVisualElement.Q<Foldout>(
                    "module-audit-external-catalog");
                var externalMetrics = window.rootVisualElement.Q<VisualElement>(
                    "module-audit-external-metrics");
                Assert.That(actions, Is.Not.Null);
                Assert.That(content, Is.Not.Null);
                Assert.That(content.horizontalScrollerVisibility, Is.EqualTo(ScrollerVisibility.Hidden));
                Assert.That(summary, Is.Not.Null);
                Assert.That(coreProfile, Is.Not.Null);
                Assert.That(raw, Is.Not.Null);
                Assert.That(statuses, Is.Not.Null);
                Assert.That(statuses.value, Is.False, "全部 Module 默认折叠，避免把常用组合推到数屏以后。 ");
                Assert.That(attentionStatuses, Is.Not.Null);
                Assert.That(attentionStatuses.value, Is.True,
                    "无条件根或热更违规应优先显示，健康 Module 再按需展开。 ");
                Assert.That(moduleProfiles, Is.Not.Null);
                Assert.That(moduleProfiles.value, Is.False, "任意 Module 的完整闭包默认折叠，避免淹没主结论。 ");
                Assert.That(globalPreservations, Is.Not.Null);
                Assert.That(globalPreservations.value, Is.False,
                    "全局和生成规则用于追踪，不应抢占新手的首屏结论。 ");
                Assert.That(hotUpdateEvidence, Is.Not.Null);
                Assert.That(hotUpdateMetrics, Is.Not.Null);
                Assert.That(externalSummary, Is.Not.Null);
                Assert.That(externalCatalog, Is.Not.Null);
                Assert.That(externalCatalog.value, Is.False,
                    "第三方依赖详情默认折叠，先给来源/用途/证据完整度摘要。 ");
                Assert.That(externalMetrics, Is.Not.Null);
                Assert.That(raw.value, Is.False, "程序集明细和原始报告默认折叠，先展示结论与建议。");
                Assert.That(window.rootVisualElement.Q<TextField>(), Is.Null,
                    "主体不再使用会整片选中、产生横向长行的多行 TextField。");

                window.ApplyResponsiveLayoutForTests(360f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                var summaryMetrics = window.rootVisualElement
                    .Q<VisualElement>("module-audit-summary-metrics");
                Assert.That(summaryMetrics.style.flexDirection.value,
                    Is.EqualTo(FlexDirection.Column));
                Assert.That(actions[0].style.flexBasis.keyword, Is.EqualTo(StyleKeyword.Auto));
                Assert.That(summaryMetrics[0].style.flexGrow.value, Is.EqualTo(0f),
                    "窄窗纵排时使用内容高度，避免 flexBasis:0 把按钮或指标压扁。 ");
                Assert.That(hotUpdateMetrics.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));
                Assert.That(externalMetrics.style.flexDirection.value, Is.EqualTo(FlexDirection.Column));

                window.ApplyResponsiveLayoutForTests(900f);
                Assert.That(actions.style.flexDirection.value, Is.EqualTo(FlexDirection.Row));
                Assert.That(actions[0].style.flexGrow.value, Is.EqualTo(1f));
                Assert.That(window.minSize.x, Is.LessThanOrEqualTo(360f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LocateProjectAsset_SelectsCommonEditorAssetsWithoutOpeningAnExternalFileHandler()
        {
            string coreAsmdefPath = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    FrameworkModuleAudit.CoreAssemblyName,
                    StringComparison.Ordinal));
            string auditWindowScriptPath = AssetDatabase.FindAssets(
                    nameof(FrameworkModuleAuditWindow) + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => AssetDatabase.LoadAssetAtPath<MonoScript>(path)?.GetClass() ==
                               typeof(FrameworkModuleAuditWindow));
            string[] projectPaths =
            {
                coreAsmdefPath,
                auditWindowScriptPath,
            };

            var previousSelection = Selection.activeObject;
            try
            {
                foreach (string path in projectPaths)
                {
                    var expected = AssetDatabase.LoadMainAssetAtPath(path);
                    Assert.That(expected, Is.Not.Null, path + " 必须能被 Unity AssetDatabase 定位。");
                    Selection.activeObject = null;
                    Assert.That(FrameworkModuleAuditWindow.TryLocateProjectAsset(path), Is.True);
                    Assert.That(Selection.activeObject, Is.SameAs(expected),
                        "“定位”必须把 Unity Project 的选择切到目标，而不是直接唤起外部编辑器：" + path);
                }
                Assert.That(FrameworkModuleAuditWindow.TryLocateProjectAsset("docs/framework-module-map.md"),
                    Is.False, "Assets/Packages 之外的文档不是 Unity Asset，应交给打开文件或文件浏览器语义。");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        private static FrameworkModuleAudit.DependencySource PackageSource(
            string assemblyName,
            string packageName) => new()
        {
            AssemblyName = assemblyName,
            AssetPath = $"Packages/{packageName}/{assemblyName}.asmdef",
            PackageName = packageName,
            PackageVersion = "1.0.0",
            PackageId = packageName + "@1.0.0",
            SourceKind = FrameworkModuleSourceCatalog.SourceKind.GitPackage,
            HasPackageDirectness = true,
            IsDirectPackageDependency = true,
            IsExternal = true,
        };

        private static FrameworkModuleAudit.DeclaredConsumerEvidence DeclaredEdge(
            string dependency,
            string consumer,
            FrameworkModuleAudit.ConsumerPlatformScope scope) => new()
        {
            DependencyAssemblyName = dependency,
            ConsumerAssemblyName = consumer,
            ConsumerAsmdefPath = "Assets/Game/" + consumer + ".asmdef",
            ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
            PlatformScope = scope,
        };

        private static FrameworkModuleAudit.ActualConsumerEvidence ActualEdge(
            string dependency,
            string consumer,
            FrameworkModuleAudit.ConsumerPlatformScope scope) => new()
        {
            DependencyAssemblyName = dependency,
            ConsumerAssemblyName = consumer,
            ConsumerSourceKind = FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
            PlatformScope = scope,
        };

        [Serializable]
        private sealed class AsmdefDeclaration
        {
            public string name;
            public string[] references;
            public string[] precompiledReferences;
            public bool overrideReferences;
            public string[] includePlatforms;
            public bool autoReferenced = true;
        }

        private static AsmdefDeclaration ReadAsmdefDeclaration(string assetPath)
        {
            if (!FrameworkModuleSourceCatalog.TryResolve(
                    assetPath,
                    out FrameworkModuleSourceCatalog.SourceLocation source,
                    out string reason))
                throw new InvalidDataException($"无法读取 {assetPath}：{reason}");
            return JsonUtility.FromJson<AsmdefDeclaration>(File.ReadAllText(source.PhysicalPath));
        }

        private sealed class FakeHotUpdateEvidence
        {
            public string[] ProfileAssemblies;
            public string[] HybridClrSettingsAssemblies;
            public string[] HybridClrLegacyAssemblies;
            public bool SettingsAvailable;
            public bool SettingsMatch;
            public string SettingsMessage;
            public bool GenerationRequired;
            public bool GenerationFresh;
            public string GenerationMessage;
            public bool StagingRequired;
            public bool StagedManifestExists;
            public bool StagedManifestAvailable;
            public bool StagedManifestMatches;
            public string StagedVersion;
            public string[] StagedAssemblies;
            public string[] ExpectedAotMetadataDlls;
            public string[] StagedAotMetadataDlls;
            public string[] MissingStagedFiles;
            public string[] UnexpectedStagedFiles;
            public string[] InvalidStagedEntries;
            public string StagedMessage;
        }
    }
}
