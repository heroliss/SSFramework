using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Game.Framework.Boot;
using NUnit.Framework;
using YooAsset.Editor;

namespace Game.Framework.Build.Tests
{
    /// <summary>
    /// 锁定热更证据的纯判定语义；不读取本机 HybridCLRData 或被 gitignore 的 DLL 中转产物。
    /// </summary>
    public sealed class FrameworkHotUpdateEvidenceTests
    {
        [Test]
        public void SettingsEvidence_RejectsLegacyStringSourceEvenWhenEffectiveSetMatches()
        {
            var evidence = HealthyEvidence("Game.Core");

            FrameworkHotUpdateBuilder.ApplyHybridClrSettingsEvidence(
                evidence,
                new[] { "Game.Core" },
                new[] { "Game.Core" });

            Assert.That(evidence.SettingsAvailable, Is.True);
            Assert.That(evidence.SettingsMatch, Is.False);
            Assert.That(evidence.HybridClrSettingsAssemblies, Is.EqualTo(new[] { "Game.Core" }));
            Assert.That(evidence.HybridClrLegacyAssemblies, Is.EqualTo(new[] { "Game.Core" }));
            Assert.That(evidence.SettingsMessage, Does.Contain("清空 hotUpdateAssemblies"));
            Assert.That(evidence.SettingsMessage, Does.Contain("无需重复同步"));
        }

        [Test]
        public void StagedManifest_MatchesOnlyWhenHotOrderAotListAndFilesAgree()
        {
            var evidence = HealthyEvidence("Game.Core", "Game.Feature");
            var manifest = new HotUpdateManifest { Version = "42" };
            manifest.HotUpdateDlls.AddRange(new[] { "Game.Core.dll", "Game.Feature.dll" });
            manifest.AotMetadataDlls.Add("mscorlib.dll");

            FrameworkHotUpdateBuilder.ApplyStagedManifestEvidence(
                evidence,
                manifest,
                new[] { "Game.Core", "Game.Feature" },
                new[] { "mscorlib.dll" },
                aotEvidenceAvailable: true,
                aotEvidenceError: string.Empty,
                actualRelativeDllFiles: new[]
                {
                    "Game.Core.dll.bytes",
                    "Game.Feature.dll.bytes",
                    "mscorlib.dll.bytes",
                });

            Assert.That(evidence.StagedManifestMatches, Is.True);
            Assert.That(evidence.StagedMessage, Does.StartWith("✓"));
            Assert.That(evidence.StagedMessage, Does.Contain("AOT 补元数据 1 个"));
        }

        [Test]
        public void StagedManifest_ReportsAotDriftEvenWhenHotDllsMatch()
        {
            var evidence = HealthyEvidence("Game.Core");
            var manifest = new HotUpdateManifest { Version = "42" };
            manifest.HotUpdateDlls.Add("Game.Core.dll");
            manifest.AotMetadataDlls.Add("mscorlib.dll");

            FrameworkHotUpdateBuilder.ApplyStagedManifestEvidence(
                evidence,
                manifest,
                new[] { "Game.Core" },
                new[] { "mscorlib.dll", "System.dll" },
                aotEvidenceAvailable: true,
                aotEvidenceError: string.Empty,
                actualRelativeDllFiles: new[] { "Game.Core.dll.bytes", "mscorlib.dll.bytes" });

            Assert.That(evidence.StagedManifestMatches, Is.False);
            Assert.That(evidence.StagedMessage, Does.Contain("AOT 清单缺少 System.dll"));
        }

        [Test]
        public void StagedManifest_ExplainsLoadOrderDriftWithoutCallingItMissingFiles()
        {
            var evidence = HealthyEvidence("Game.Core", "Game.Feature");
            var manifest = new HotUpdateManifest { Version = "42" };
            manifest.HotUpdateDlls.AddRange(new[] { "Game.Feature.dll", "Game.Core.dll" });

            FrameworkHotUpdateBuilder.ApplyStagedManifestEvidence(
                evidence,
                manifest,
                new[] { "Game.Core", "Game.Feature" },
                Array.Empty<string>(),
                aotEvidenceAvailable: true,
                aotEvidenceError: string.Empty,
                actualRelativeDllFiles: new[] { "Game.Core.dll.bytes", "Game.Feature.dll.bytes" });

            Assert.That(evidence.StagedManifestMatches, Is.False);
            Assert.That(evidence.StagedMessage,
                Does.Contain("加载顺序漂移：期望 Game.Core → Game.Feature；清单 Game.Feature → Game.Core"));
            Assert.That(evidence.StagedMessage, Does.Not.Contain("文件证据不完整"));
        }

        [Test]
        public void StagedManifest_ReportsDuplicateInvalidMissingAndNestedResidualFiles()
        {
            var evidence = HealthyEvidence("Game.Core");
            var manifest = new HotUpdateManifest { Version = "42" };
            manifest.HotUpdateDlls.AddRange(new[] { "Game.Core.dll", "Game.Core.dll", "../Unsafe.dll", "" });
            manifest.AotMetadataDlls.Add("mscorlib.dll");

            FrameworkHotUpdateBuilder.ApplyStagedManifestEvidence(
                evidence,
                manifest,
                new[] { "Game.Core" },
                new[] { "mscorlib.dll" },
                aotEvidenceAvailable: true,
                aotEvidenceError: string.Empty,
                actualRelativeDllFiles: new[] { "Game.Core.dll.bytes", "nested/Residual.dll.bytes" });

            Assert.That(evidence.StagedManifestMatches, Is.False);
            Assert.That(evidence.InvalidStagedEntries, Does.Contain("../Unsafe.dll"));
            Assert.That(evidence.InvalidStagedEntries, Does.Contain("<空条目>"));
            Assert.That(evidence.MissingStagedFiles, Does.Contain("mscorlib.dll.bytes"));
            Assert.That(evidence.UnexpectedStagedFiles, Does.Contain("nested/Residual.dll.bytes"));
            Assert.That(evidence.StagedMessage, Does.Contain("重复 DLL"));
            Assert.That(evidence.StagedMessage, Does.Contain("非法文件名"));
            Assert.That(evidence.StagedMessage, Does.Contain("目录残留"));
        }

        [Test]
        public void MissingStage_IsOptionalOnlyForExplicitPureAotProfile()
        {
            var pureAot = HealthyEvidence();
            pureAot.StagingRequired = false;

            FrameworkHotUpdateBuilder.ApplyMissingStagedManifestEvidence(pureAot);

            Assert.That(pureAot.StagedManifestMatches, Is.True);
            Assert.That(pureAot.RequiresAttention, Is.False);
            Assert.That(pureAot.StagedMessage, Does.Contain("可选"));

            var hot = HealthyEvidence("Game.Core");
            FrameworkHotUpdateBuilder.ApplyMissingStagedManifestEvidence(hot);

            Assert.That(hot.StagedManifestMatches, Is.False);
            Assert.That(hot.RequiresAttention, Is.True);
            Assert.That(hot.StagedMessage, Does.StartWith("✗"));
        }

        [Test]
        public void CodePackageRequirement_DistinguishesDirectAotFromBootLauncherComposition()
        {
            Assert.That(FrameworkHotUpdateBuilder.IsCodePackageRequired(
                Array.Empty<string>(), hasLauncherInEnabledScene: false), Is.False,
                "直接 AOT composition root 不需要空代码包。 ");
            Assert.That(FrameworkHotUpdateBuilder.IsCodePackageRequired(
                Array.Empty<string>(), hasLauncherInEnabledScene: true), Is.True,
                "HotUpdateLauncher 的 Player 分支仍读取 manifest，空 Profile 也必须构建空包。 ");
            Assert.That(FrameworkHotUpdateBuilder.IsCodePackageRequired(
                new[] { "Game.Main" }, hasLauncherInEnabledScene: false), Is.True);

            var bootAot = HealthyEvidence();
            bootAot.StagingRequired = true;
            FrameworkHotUpdateBuilder.ApplyMissingStagedManifestEvidence(bootAot);
            Assert.That(bootAot.StagedManifestMatches, Is.False);
            Assert.That(bootAot.StagedMessage, Does.Contain("HotUpdateLauncher"));
            Assert.That(bootAot.StagedMessage, Does.Contain("空清单"));
        }

        [Test]
        public void DependencyTopologyFingerprint_IsStableAcrossDictionaryAndEntryOrder()
        {
            var first = new Dictionary<string, string[]>
            {
                ["Game.Feature"] = new[] { "R3", "Game.Core" },
                ["Game.Core"] = new[] { "UniTask", "R3.Unity" },
            };
            var reordered = new Dictionary<string, string[]>
            {
                ["Game.Core"] = new[] { "R3.Unity", "UniTask" },
                ["Game.Feature"] = new[] { "Game.Core", "R3" },
            };

            Assert.That(
                FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(first),
                Is.EqualTo(FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(reordered)));
        }

        [Test]
        public void DependencyTopologyFingerprint_ChangesWhenTypeOrMemberReferenceChanges()
        {
            var before = new Dictionary<string, string[]>
            {
                ["Game.Core"] = new[] { "A|UniTask", "T|R3|R3.Observable" },
            };
            var after = new Dictionary<string, string[]>
            {
                ["Game.Core"] = new[]
                {
                    "A|UniTask",
                    "T|R3|R3.Observable",
                    "M|R3|System.IDisposable R3.Observable::Subscribe()",
                },
            };

            Assert.That(
                FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(before),
                Is.Not.EqualTo(FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(after)));
        }

        [Test]
        public void DependencyTopologyFingerprint_PreservesMultiplicityAndEntryBoundaries()
        {
            var oneEntry = new Dictionary<string, string[]> { ["Game.Core"] = new[] { "a,b" } };
            var twoEntries = new Dictionary<string, string[]> { ["Game.Core"] = new[] { "a", "b" } };
            var duplicate = new Dictionary<string, string[]> { ["Game.Core"] = new[] { "a,b", "a,b" } };

            Assert.That(
                FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(oneEntry),
                Is.Not.EqualTo(FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(twoEntries)),
                "长度前缀必须避免逗号、换行或 => 等元数据文本造成规范化碰撞。 ");
            Assert.That(
                FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(oneEntry),
                Is.Not.EqualTo(FrameworkHotUpdateBuilder.ComputeDependencyTopologySha256(duplicate)),
                "MethodSpec、callback 或同签名结构的数量也是生成输入，不能被去重。 ");
        }

        [Test]
        public void PlayerMetadataTopology_CapturesDefinitionsLayoutPInvokeAndMetadataCalls()
        {
            string[] entries = FrameworkPlayerMetadataTopology.ReadEntries(
                typeof(FrameworkHotUpdateEvidenceTests).Assembly.Location);

            Assert.That(entries.Any(entry => entry.StartsWith("MD|", StringComparison.Ordinal) &&
                                                  entry.Contains(nameof(PrimitiveMethodA))), Is.True);
            Assert.That(entries.Any(entry => entry.StartsWith("MD|", StringComparison.Ordinal) &&
                                                  entry.Contains(nameof(PrimitiveMethodB))), Is.True,
                "同 ABI 签名的方法仍必须逐一定义，Reverse P/Invoke wrapper 数量依赖方法数。 ");
            Assert.That(entries.Any(entry => entry.StartsWith("TD|", StringComparison.Ordinal) &&
                                                  entry.Contains(nameof(LayoutFixture)) &&
                                                  entry.Contains("size=16")), Is.True);
            Assert.That(entries.Any(entry => entry.StartsWith("FD|", StringComparison.Ordinal) &&
                                                  entry.Contains(nameof(LayoutFixture)) &&
                                                  entry.Contains("offset=8")), Is.True);
            Assert.That(entries.Any(entry => entry.StartsWith("PI|", StringComparison.Ordinal) &&
                                                  entry.Contains("GetTickCount") &&
                                                  entry.Contains("Stdcall")), Is.True);
            Assert.That(entries.Any(entry => entry.StartsWith("IL|", StringComparison.Ordinal) &&
                                                  entry.Contains(nameof(PrimitiveMethodA))), Is.True);

            string firstAttribute = entries.Single(entry => entry.StartsWith("CA|", StringComparison.Ordinal) &&
                                                              entry.Contains(nameof(AttributeFixtureA)));
            string secondAttribute = entries.Single(entry => entry.StartsWith("CA|", StringComparison.Ordinal) &&
                                                               entry.Contains(nameof(AttributeFixtureB)));
            Assert.That(firstAttribute[(firstAttribute.LastIndexOf('|') + 1)..],
                Is.Not.EqualTo(secondAttribute[(secondAttribute.LastIndexOf('|') + 1)..]),
                "构造参数、数组和 named argument 都必须进入特性拓扑，不能只记录 Attribute 类型名。 ");
        }

        [Test]
        public void AotSourceFingerprint_DoesNotDependOnCompilationOutputDllVariant()
        {
            string allAot = FrameworkHotUpdateBuilder.GetAotSourceInputsFingerprint(Array.Empty<string>());
            string coreAsHot = FrameworkHotUpdateBuilder.GetAotSourceInputsFingerprint(
                new[] { "Game.Framework" });
            string linkerRoots = FrameworkHotUpdateBuilder.GetPlayerLinkerRootsFingerprint();

            Assert.That(allAot, Has.Length.EqualTo(64));
            Assert.That(coreAsHot, Has.Length.EqualTo(64));
            Assert.That(linkerRoots, Has.Length.EqualTo(64));
            Assert.That(coreAsHot, Is.Not.EqualTo(allAot),
                "热更源码由目标平台 CompileDll 元数据负责；AOT 源输入必须排除它，不能读取 Editor outputPath。 ");
        }

        [Test]
        public void GenerationFingerprintCapture_ReusesOnlyWithinOneStableCapture()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "SSFramework-HotUpdateFingerprint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "input.txt");
            try
            {
                File.WriteAllText(path, "first");
                var capture = new FrameworkHotUpdateBuilder.GenerationFingerprintCapture();

                string first = capture.HashRequiredFile(path, "测试输入");
                string repeated = capture.HashRequiredFile(path, "测试输入");

                Assert.That(repeated, Is.EqualTo(first));
                Assert.That(capture.UniqueFileReadCount, Is.EqualTo(1),
                    "同一轮内共享的 rsp、Analyzer、预编译 DLL 与序列化根只能读取一次。 ");

                File.AppendAllText(path, "-changed");
                Assert.Throws<IOException>(() => capture.HashRequiredFile(path, "测试输入"),
                    "单轮复用不能把采集期间发生的输入漂移隐藏起来。 ");

                var nextCapture = new FrameworkHotUpdateBuilder.GenerationFingerprintCapture();
                Assert.That(nextCapture.HashRequiredFile(path, "测试输入"), Is.Not.EqualTo(first),
                    "缓存不得跨显式证据采集复用。 ");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void EvidenceInspection_KeepsLegacyNameUnambiguousForReflectionCallers()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            MethodInfo legacy = null;
            Assert.DoesNotThrow(() => legacy = typeof(FrameworkHotUpdateBuilder)
                .GetMethod("InspectEvidence", flags),
                "旧版 Module Audit 使用 name-only 反射，同名重载会导致 AmbiguousMatchException。 ");
            Assert.That(legacy, Is.Not.Null);
            Assert.That(legacy.GetParameters().Select(parameter => parameter.ParameterType),
                Is.EqualTo(new[] { typeof(FrameworkHotUpdateProfile) }));

            MethodInfo snapshot = typeof(FrameworkHotUpdateBuilder).GetMethod(
                "InspectEvidenceFromSnapshot",
                flags,
                binder: null,
                new[]
                {
                    typeof(FrameworkHotUpdateProfile),
                    typeof(string[]),
                    typeof(UnityEditor.Compilation.Assembly[]),
                },
                modifiers: null);
            Assert.That(snapshot, Is.Not.Null,
                "新审计应通过独立快照入口复用冻结输入，不破坏旧反射 Seam。 ");
        }

        [Test]
        public void GenerationStampFormat_RejectsV4WithActionableMigrationMessage()
        {
            var (legacyOk, legacyMessage) =
                FrameworkHotUpdateBuilder.ValidateGenerationStampFormatVersion(4);
            var (currentOk, currentMessage) =
                FrameworkHotUpdateBuilder.ValidateGenerationStampFormatVersion(5);

            Assert.That(legacyOk, Is.False);
            Assert.That(legacyMessage, Does.Contain("v4").And.Contain("v5"));
            Assert.That(currentOk, Is.True);
            Assert.That(currentMessage, Is.Empty);
        }

        [Test]
        public void LinkerDependencyUnion_IsStableAndDoesNotRepeatSharedClosures()
        {
            string[] first = FrameworkHotUpdateBuilder.CanonicalizeLinkerDependencyUnion(
                new[] { "Assets/Scenes/Main.unity", "Packages/com.example/Resources/Config.asset" },
                new[]
                {
                    "Assets\\Shared\\Theme.asset",
                    "Assets/Scenes/Main.unity",
                    "Assets/Shared/Theme.asset",
                });
            string[] reordered = FrameworkHotUpdateBuilder.CanonicalizeLinkerDependencyUnion(
                new[] { "Packages/com.example/Resources/Config.asset", "Assets/Scenes/Main.unity" },
                new[] { "Assets/Shared/Theme.asset" });

            Assert.That(first, Is.EqualTo(reordered));
            Assert.That(first, Is.EqualTo(new[]
            {
                "Assets/Scenes/Main.unity",
                "Assets/Shared/Theme.asset",
                "Packages/com.example/Resources/Config.asset",
            }));
        }

        [Test]
        public void AotCompilerEvidence_IncludesResponseFilesAndStableCompilerOptions()
        {
            UnityEditor.Compilation.Assembly assembly = UnityEditor.Compilation.CompilationPipeline
                .GetAssemblies(UnityEditor.Compilation.AssembliesType.Player)
                .Single(item => item.name == "Game.Framework");

            string[] entries = FrameworkHotUpdateBuilder.GetCompilerOptionsFingerprintEntries(assembly);

            Assert.That(entries, Has.Some.StartsWith("COMPILER|language="));
            Assert.That(entries, Has.Some.StartsWith("RSP|").And.Contains("csc.rsp"),
                "项目 csc.rsp 的内容会改变编译元数据，必须进入 AOT Generate 新鲜度证据。 ");
            Assert.That(entries, Has.Some.StartsWith("ANALYZER_CONFIG|"));
            Assert.That(entries, Has.Some.StartsWith("RULESET|"));
        }

        [Test]
        public void LinkerRootContent_TracksUxmlAndSerializedGuiAssets()
        {
            Assert.That(FrameworkHotUpdateBuilder.IsSerializedLinkerRootAsset(
                "Assets/UI/Screen.uxml"), Is.True,
                "UXML 中的自定义 VisualElement 类型变化可改变 linker 根。 ");
            Assert.That(FrameworkHotUpdateBuilder.IsSerializedLinkerRootAsset(
                "Assets/UI/Skin.guiskin"), Is.True);
            Assert.That(FrameworkHotUpdateBuilder.IsSerializedLinkerRootAsset(
                "Assets/UI/Styles.uss"), Is.False,
                "纯样式文件不应让元数据 stamp 因颜色等普通视觉调整失效。 ");
        }

        [Test]
        public void CodeCollectorContract_RepairsRuleDriftAndIsIdempotent()
        {
            var collector = new BundleCollector
            {
                CollectPath = "Assets/Wrong",
                CollectorGUID = "wrong-guid",
                CollectorType = ECollectorType.StaticAssetCollector,
                AddressRuleName = "WrongAddress",
                PackRuleName = "WrongPack",
                FilterRuleName = "WrongFilter",
            };

            Assert.That(FrameworkHotUpdateBuilder.ApplyCodeCollectorContract(
                collector, "Assets/Generated/Code", "expected-guid"), Is.True);
            Assert.That(collector.CollectPath, Is.EqualTo("Assets/Generated/Code"));
            Assert.That(collector.CollectorGUID, Is.EqualTo("expected-guid"));
            Assert.That(collector.CollectorType, Is.EqualTo(ECollectorType.MainAssetCollector));
            Assert.That(collector.AddressRuleName, Is.EqualTo(nameof(AddressByFileName)));
            Assert.That(collector.PackRuleName, Is.EqualTo(nameof(PackRawFile)));
            Assert.That(collector.FilterRuleName, Is.EqualTo(nameof(CollectAll)));
            Assert.That(FrameworkHotUpdateBuilder.ApplyCodeCollectorContract(
                collector, "Assets/Generated/Code", "expected-guid"), Is.False,
                "契约已正确时不能再次保存整份 YooAsset YAML。 ");
        }

        [Test]
        public void CodeCollectorGroupContract_RemovesWrongPathAndDuplicateCollectors()
        {
            var group = new BundleCollectorGroup();
            group.Collectors.Add(new BundleCollector { CollectPath = "Assets/OldGeneratedCode" });
            group.Collectors.Add(new BundleCollector { CollectPath = "Assets/Generated/Code/" });

            Assert.That(FrameworkHotUpdateBuilder.ApplyCodeCollectorGroupContract(
                group, "Assets/Generated/Code", "expected-guid"), Is.True);
            Assert.That(group.Collectors, Has.Count.EqualTo(1));
            Assert.That(group.Collectors[0].CollectPath, Is.EqualTo("Assets/Generated/Code"));
            Assert.That(FrameworkHotUpdateBuilder.ApplyCodeCollectorGroupContract(
                group, "Assets/Generated/Code", "expected-guid"), Is.False);
        }

        private static int PrimitiveMethodA(int value) => Math.Abs(value);

        private static int PrimitiveMethodB(int value) => Math.Max(value, 0);

        [MetadataTopologyFixture(1, typeof(int), "a", Mode = CallingConvention.Cdecl, Enabled = false)]
        private static void AttributeFixtureA()
        {
        }

        [MetadataTopologyFixture(10, typeof(long), "a", "b", Mode = CallingConvention.StdCall, Enabled = true)]
        private static void AttributeFixtureB()
        {
        }

        [DllImport("kernel32", EntryPoint = "GetTickCount", CallingConvention = CallingConvention.StdCall)]
        private static extern uint ProbeNative();

        [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 16)]
        private struct LayoutFixture
        {
            [FieldOffset(0)] public int First;
            [FieldOffset(8)] public int Second;
        }

        [AttributeUsage(AttributeTargets.Method)]
        private sealed class MetadataTopologyFixtureAttribute : Attribute
        {
            public MetadataTopologyFixtureAttribute(int count, Type valueType, params string[] labels)
            {
            }

            public CallingConvention Mode { get; set; }
            public bool Enabled;
        }

        private static FrameworkHotUpdateEvidence HealthyEvidence(params string[] profileAssemblies) => new()
        {
            ProfileAssemblies = profileAssemblies ?? Array.Empty<string>(),
            SettingsAvailable = true,
            SettingsMatch = true,
            GenerationRequired = profileAssemblies?.Length > 0,
            GenerationFresh = true,
            StagingRequired = profileAssemblies?.Length > 0,
            StagedManifestMatches = true,
        };
    }
}
