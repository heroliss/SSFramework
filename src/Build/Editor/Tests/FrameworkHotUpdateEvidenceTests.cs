using System;
using Game.Framework.Boot;
using NUnit.Framework;

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
