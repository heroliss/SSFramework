using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Config.Editor.Tests
{
    /// <summary>锁定 Luban 暂存快照、双目录差量提交与失败回滚，不启动真实 CLI。</summary>
    public sealed class LubanGenerationTransactionTests
    {
        private string _testRoot;

        [SetUp]
        public void SetUp()
        {
            _testRoot = ProjectAbsolute("Temp/SSFrameworkLubanTransaction_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true);
        }

        [Test]
        public void ValidateAndPublish_DiffsBothTreesAndPreservesRetainedMeta()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            Write(outputCode, "Keep.cs", "same");
            Write(outputCode, "Keep.cs.meta", "keep-guid");
            Write(outputCode, "Change.cs", "old");
            Write(outputCode, "Change.cs.meta", "change-guid");
            Write(outputCode, "Stale.cs", "stale");
            Write(outputCode, "Stale.cs.meta", "stale-guid");
            Write(outputData, "alpha.bytes", "old-alpha");
            Write(outputData, "alpha.bytes.meta", "alpha-guid");
            Write(outputData, "stale.bytes", "stale");
            Write(outputData, "stale.bytes.meta", "stale-guid");

            DateTime keepWriteTime = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(outputCode, "Keep.cs"), keepWriteTime);
            keepWriteTime = File.GetLastWriteTimeUtc(Path.Combine(outputCode, "Keep.cs"));

            LubanGenerationTransaction.PublishReport report;
            using (var transaction = CreateTransaction("transaction", outputCode, outputData))
            {
                Write(transaction.StagingCodeDirectory, "Keep.cs", "same");
                Write(transaction.StagingCodeDirectory, "Change.cs", "new");
                Write(transaction.StagingCodeDirectory, "Nested/Added.cs", "added");
                Write(transaction.StagingDataDirectory, "alpha.bytes", "new-alpha");
                Write(transaction.StagingDataDirectory, "beta.bytes", "new-beta");

                report = transaction.ValidateAndPublish("Demo.Generated");
            }

            Assert.That(report.Code.Added, Is.EqualTo(2), "Added.cs + manifest");
            Assert.That(report.Code.Updated, Is.EqualTo(1));
            Assert.That(report.Code.Unchanged, Is.EqualTo(1));
            Assert.That(report.Code.Removed, Is.EqualTo(1));
            Assert.That(report.Data.Added, Is.EqualTo(1));
            Assert.That(report.Data.Updated, Is.EqualTo(1));
            Assert.That(report.Data.Removed, Is.EqualTo(1));
            Assert.That(report.HasChanges, Is.True);
            Assert.That(report.TableNames, Is.EqualTo(new[] { "alpha", "beta" }));

            Assert.That(Read(outputCode, "Keep.cs"), Is.EqualTo("same"));
            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(outputCode, "Keep.cs")), Is.EqualTo(keepWriteTime));
            Assert.That(Read(outputCode, "Keep.cs.meta"), Is.EqualTo("keep-guid"));
            Assert.That(Read(outputCode, "Change.cs"), Is.EqualTo("new"));
            Assert.That(Read(outputCode, "Change.cs.meta"), Is.EqualTo("change-guid"));
            Assert.That(File.Exists(Path.Combine(outputCode, "Stale.cs")), Is.False);
            Assert.That(File.Exists(Path.Combine(outputCode, "Stale.cs.meta")), Is.False);
            Assert.That(File.Exists(Path.Combine(outputCode, "Nested/Added.cs.meta")), Is.False);

            Assert.That(Read(outputData, "alpha.bytes"), Is.EqualTo("new-alpha"));
            Assert.That(Read(outputData, "alpha.bytes.meta"), Is.EqualTo("alpha-guid"));
            Assert.That(Read(outputData, "beta.bytes"), Is.EqualTo("new-beta"));
            Assert.That(File.Exists(Path.Combine(outputData, "stale.bytes")), Is.False);
            Assert.That(File.Exists(Path.Combine(outputData, "stale.bytes.meta")), Is.False);

            string manifest = Read(outputCode, LubanGenerationTransaction.ManifestFileName);
            Assert.That(manifest, Does.Contain("namespace Demo.Generated"));
            Assert.That(manifest.IndexOf("\"alpha\"", StringComparison.Ordinal),
                Is.LessThan(manifest.IndexOf("\"beta\"", StringComparison.Ordinal)));
        }

        [Test]
        public void ValidateAndPublish_WhenEverythingIsUnchanged_DoesNotRewriteFiles()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");

            using (var first = CreateTransaction("first", outputCode, outputData))
            {
                Write(first.StagingCodeDirectory, "Tables.cs", "line1\nline2\n");
                Write(first.StagingDataDirectory, "table.bytes", "data");
                Assert.That(first.ValidateAndPublish("DemoCfg").HasChanges, Is.True);
            }

            string codePath = Path.Combine(outputCode, "Tables.cs");
            string manifestPath = Path.Combine(outputCode, LubanGenerationTransaction.ManifestFileName);
            string dataPath = Path.Combine(outputData, "table.bytes");
            DateTime stableWriteTime = new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(codePath, stableWriteTime);
            File.SetLastWriteTimeUtc(manifestPath, stableWriteTime);
            File.SetLastWriteTimeUtc(dataPath, stableWriteTime);
            stableWriteTime = File.GetLastWriteTimeUtc(codePath);

            LubanGenerationTransaction.PublishReport report;
            using (var second = CreateTransaction("second", outputCode, outputData))
            {
                Write(second.StagingCodeDirectory, "Tables.cs", "line1\r\nline2\r\n");
                Write(second.StagingDataDirectory, "table.bytes", "data");
                report = second.ValidateAndPublish("DemoCfg");
            }

            Assert.That(report.HasChanges, Is.False);
            Assert.That(report.Code.Unchanged, Is.EqualTo(2));
            Assert.That(report.Data.Unchanged, Is.EqualTo(1));
            Assert.That(File.GetLastWriteTimeUtc(codePath), Is.EqualTo(stableWriteTime));
            Assert.That(File.GetLastWriteTimeUtc(manifestPath), Is.EqualTo(stableWriteTime));
            Assert.That(File.GetLastWriteTimeUtc(dataPath), Is.EqualTo(stableWriteTime));
            Assert.That(File.ReadAllText(codePath), Does.Not.Contain("\r"));
            Assert.That(File.ReadAllText(manifestPath), Does.Not.Contain("\r"));
        }

        [Test]
        public void ValidateAndPublish_CleansOrphanMetaAndEmptyDirectoriesButRetainsOwnedMeta()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            using (var first = CreateTransaction("first", outputCode, outputData))
            {
                Write(first.StagingCodeDirectory, "Kept/Tables.cs", "code");
                Write(first.StagingDataDirectory, "table.bytes", "data");
                first.ValidateAndPublish("DemoCfg");
            }

            Write(outputCode, "Kept.meta", "kept-directory-guid");
            Write(outputCode, "Kept/Tables.cs.meta", "kept-file-guid");
            Write(outputCode, "Ghost.cs.meta", "orphan-file-guid");
            Directory.CreateDirectory(Path.Combine(outputCode, "Stale"));
            Write(outputCode, "Stale.meta", "stale-directory-guid");
            Write(outputCode, "Stale/Orphan.cs.meta", "nested-orphan-guid");

            LubanGenerationTransaction.PublishReport report;
            using (var second = CreateTransaction("second", outputCode, outputData))
            {
                Write(second.StagingCodeDirectory, "Kept/Tables.cs", "code");
                Write(second.StagingDataDirectory, "table.bytes", "data");
                report = second.ValidateAndPublish("DemoCfg");
            }

            Assert.That(report.HasChanges, Is.True);
            Assert.That(report.Code.Removed, Is.EqualTo(3));
            Assert.That(Read(outputCode, "Kept.meta"), Is.EqualTo("kept-directory-guid"));
            Assert.That(Read(outputCode, "Kept/Tables.cs.meta"), Is.EqualTo("kept-file-guid"));
            Assert.That(File.Exists(Path.Combine(outputCode, "Ghost.cs.meta")), Is.False);
            Assert.That(File.Exists(Path.Combine(outputCode, "Stale.meta")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(outputCode, "Stale")), Is.False);
        }

        [Test]
        public void ValidateAndPublish_AppliesCaseOnlyDirectoryRenameBeforeWritingNewFiles()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            using (var first = CreateTransaction("first", outputCode, outputData))
            {
                Write(first.StagingCodeDirectory, "nested/Tables.cs", "code");
                Write(first.StagingDataDirectory, "table.bytes", "data");
                first.ValidateAndPublish("DemoCfg");
            }

            using (var second = CreateTransaction("second", outputCode, outputData))
            {
                Write(second.StagingCodeDirectory, "Nested/Tables.cs", "code");
                Write(second.StagingDataDirectory, "table.bytes", "data");
                Assert.That(second.ValidateAndPublish("DemoCfg").HasChanges, Is.True);
            }

            string actualDirectory = Directory.GetDirectories(outputCode)
                .Single(path => !Path.GetFileName(path).EndsWith(".meta", StringComparison.OrdinalIgnoreCase));
            Assert.That(Path.GetFileName(actualDirectory), Is.EqualTo("Nested"));
            Assert.That(Read(outputCode, "Nested/Tables.cs"), Is.EqualTo("code"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ValidateAndPublish_ReplacesFileAndDirectoryTopology(bool directoryFirst)
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            string firstPath = directoryFirst ? "Node.cs/Part.cs" : "Node.cs";
            string secondPath = directoryFirst ? "Node.cs" : "Node.cs/Part.cs";
            using (var first = CreateTransaction("first", outputCode, outputData))
            {
                Write(first.StagingCodeDirectory, firstPath, "first");
                Write(first.StagingDataDirectory, "table.bytes", "data");
                first.ValidateAndPublish("DemoCfg");
            }

            using (var second = CreateTransaction("second", outputCode, outputData))
            {
                Write(second.StagingCodeDirectory, secondPath, "second");
                Write(second.StagingDataDirectory, "table.bytes", "data");
                second.ValidateAndPublish("DemoCfg");
            }

            string nodePath = Path.Combine(outputCode, "Node.cs");
            Assert.That(File.Exists(nodePath), Is.EqualTo(directoryFirst));
            Assert.That(Directory.Exists(nodePath), Is.EqualTo(!directoryFirst));
            Assert.That(Read(outputCode, secondPath), Is.EqualTo("second"));
        }

        [Test]
        public void ValidateAndPublish_WhenStagingIsIncomplete_LeavesFinalTreesUntouched()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            Write(outputCode, "Old.cs", "old-code");
            Write(outputCode, "Old.cs.meta", "old-code-guid");
            Write(outputData, "old.bytes", "old-data");
            Write(outputData, "old.bytes.meta", "old-data-guid");
            IReadOnlyDictionary<string, byte[]> before = Snapshot(outputCode, outputData);

            using var transaction = CreateTransaction("transaction", outputCode, outputData);
            Write(transaction.StagingCodeDirectory, "New.cs", "new-code");

            var exception = Assert.Throws<InvalidDataException>(() =>
                transaction.ValidateAndPublish("DemoCfg"));

            Assert.That(exception!.Message, Does.Contain("没有生成任何 .bytes"));
            AssertSnapshotsEqual(before, Snapshot(outputCode, outputData));
        }

        [TestCase("code")]
        [TestCase("data")]
        [TestCase("bom-code")]
        public void ValidateAndPublish_RejectsEmptyGeneratedFiles(string emptyTree)
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            using var transaction = CreateTransaction("transaction", outputCode, outputData);
            Write(transaction.StagingCodeDirectory, "Tables.cs", emptyTree == "code" ? string.Empty : "code");
            if (emptyTree == "bom-code")
                File.WriteAllBytes(
                    Path.Combine(transaction.StagingCodeDirectory, "Tables.cs"),
                    new byte[] { 0xEF, 0xBB, 0xBF });
            Write(transaction.StagingDataDirectory, "table.bytes", emptyTree == "data" ? string.Empty : "data");

            var exception = Assert.Throws<InvalidDataException>(() =>
                transaction.ValidateAndPublish("DemoCfg"));

            Assert.That(exception!.Message, Does.Contain("空文件"));
            Assert.That(Directory.Exists(outputCode), Is.False);
            Assert.That(Directory.Exists(outputData), Is.False);
        }

        [Test]
        public void ValidateAndPublish_RejectsNonUtf8GeneratedCode()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            using var transaction = CreateTransaction("transaction", outputCode, outputData);
            string codePath = Path.Combine(transaction.StagingCodeDirectory, "Tables.cs");
            File.WriteAllText(codePath, "code", Encoding.Unicode);
            Write(transaction.StagingDataDirectory, "table.bytes", "data");

            var exception = Assert.Throws<InvalidDataException>(() =>
                transaction.ValidateAndPublish("DemoCfg"));

            Assert.That(exception!.Message, Does.Contain("UTF-8"));
            Assert.That(Directory.Exists(outputCode), Is.False);
            Assert.That(Directory.Exists(outputData), Is.False);
        }

        [Test]
        public void Constructor_RejectsOutputOutsideTrustedBoundary()
        {
            string outside = Path.Combine(
                Path.GetDirectoryName(_testRoot)!,
                "SSFrameworkLubanOutside_" + Guid.NewGuid().ToString("N"));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                new LubanGenerationTransaction(
                    Path.Combine(_testRoot, "transaction"),
                    outside,
                    Path.Combine(_testRoot, "final-data"),
                    _testRoot));

            Assert.That(exception!.Message, Does.Contain("受信边界"));
            Assert.That(Directory.Exists(outside), Is.False);
        }

        [Test]
        public void ValidateAndPublish_RejectsCliOwnedManifestNameAtAnyDepth()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            using var transaction = CreateTransaction("transaction", outputCode, outputData);
            Write(
                transaction.StagingCodeDirectory,
                "Nested/" + LubanGenerationTransaction.ManifestFileName,
                "collision");
            Write(transaction.StagingDataDirectory, "table.bytes", "data");

            var exception = Assert.Throws<InvalidDataException>(() =>
                transaction.ValidateAndPublish("DemoCfg"));

            Assert.That(exception!.Message, Does.Contain("清单文件重名"));
            Assert.That(Directory.Exists(outputCode), Is.False);
            Assert.That(Directory.Exists(outputData), Is.False);
        }

        [Test]
        public void ValidateAndPublish_WhenFailureOccursAfterDataPublish_RollsBackBothTreesAndMeta()
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            Write(outputCode, "Old.cs", "old-code");
            Write(outputCode, "Old.cs.meta", "old-code-guid");
            Write(outputCode, LubanGenerationTransaction.ManifestFileName, "old-manifest");
            Write(outputCode, LubanGenerationTransaction.ManifestFileName + ".meta", "manifest-guid");
            Write(outputData, "old.bytes", "old-data");
            Write(outputData, "old.bytes.meta", "old-data-guid");
            IReadOnlyDictionary<string, byte[]> before = Snapshot(outputCode, outputData);

            using var transaction = CreateTransaction("transaction", outputCode, outputData);
            Write(transaction.StagingCodeDirectory, "New.cs", "new-code");
            Write(transaction.StagingDataDirectory, "new.bytes", "new-data");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                transaction.ValidateAndPublish(
                    "DemoCfg",
                    checkpoint =>
                    {
                        if (checkpoint == LubanGenerationTransaction.PublishCheckpoint.DataTreePublished)
                            throw new IOException("injected publication failure");
                    }));

            Assert.That(exception!.Message, Does.Contain("均已恢复"));
            Assert.That(exception.Message, Does.Contain("injected publication failure"));
            AssertSnapshotsEqual(before, Snapshot(outputCode, outputData));
        }

        [TestCase("Unexpected.txt", "code", "非 C#")]
        [TestCase("Nested/table.bytes", "data", "根目录")]
        [TestCase("table.json", "data", "非 .bytes")]
        public void ValidateAndPublish_RejectsUnexpectedStagedArtifacts(
            string relativePath,
            string tree,
            string expectedMessage)
        {
            string outputCode = Path.Combine(_testRoot, "final-code");
            string outputData = Path.Combine(_testRoot, "final-data");
            using var transaction = CreateTransaction("transaction", outputCode, outputData);
            Write(transaction.StagingCodeDirectory, "Tables.cs", "code");
            Write(transaction.StagingDataDirectory, "table.bytes", "data");
            Write(
                tree == "code" ? transaction.StagingCodeDirectory : transaction.StagingDataDirectory,
                relativePath,
                "unexpected");

            var exception = Assert.Throws<InvalidDataException>(() =>
                transaction.ValidateAndPublish("DemoCfg"));

            Assert.That(exception!.Message, Does.Contain(expectedMessage));
            Assert.That(Directory.Exists(outputCode), Is.False);
            Assert.That(Directory.Exists(outputData), Is.False);
        }

        [Test]
        public void Generate_WhenCliFailsAfterWritingStaging_DoesNotTouchFinalTrees()
        {
            string id = Guid.NewGuid().ToString("N");
            string relativeInputRoot = "Temp/SSFrameworkLubanCli_" + id;
            string codeAssetPath =
                "Assets/Game/Framework/Config/Editor/Tests/GeneratedLubanCode_" + id;
            string dataAssetPath =
                "Assets/Game/Framework/Config/Editor/Tests/GeneratedLubanData_" + id;
            string codeAbsolutePath = ProjectAbsolute(codeAssetPath);
            string dataAbsolutePath = ProjectAbsolute(dataAssetPath);
            var profile = CreateConfiguredProfile(relativeInputRoot, codeAssetPath, dataAssetPath);
            try
            {
                Write(ProjectAbsolute(relativeInputRoot), "Luban/Luban.exe", string.Empty);
                Write(ProjectAbsolute(relativeInputRoot), "Config~/luban.conf", "{}");
                Write(codeAbsolutePath, "Old.cs", "old-code");
                Write(dataAbsolutePath, "old.bytes", "old-data");
                var runner = new PartialFailureRunner();

                var result = LubanCodeGenerator.Generate(profile, runner);

                Assert.That(result.ok, Is.False);
                Assert.That(result.message, Does.Contain("暂存产物已丢弃").And.Contain("未修改"));
                Assert.That(runner.ReceivedInvocation, Is.True);
                Assert.That(Read(codeAbsolutePath, "Old.cs"), Is.EqualTo("old-code"));
                Assert.That(Read(dataAbsolutePath, "old.bytes"), Is.EqualTo("old-data"));
                Assert.That(File.Exists(Path.Combine(codeAbsolutePath, "Partial.cs")), Is.False);
                Assert.That(File.Exists(Path.Combine(dataAbsolutePath, "partial.bytes")), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(profile);
                DeleteAbsoluteDirectory(ProjectAbsolute(relativeInputRoot));
                DeleteAbsoluteDirectory(codeAbsolutePath);
                DeleteAbsoluteDirectory(dataAbsolutePath);
                DeleteFileIfExists(codeAbsolutePath + ".meta");
                DeleteFileIfExists(dataAbsolutePath + ".meta");
            }
        }

        [Test]
        public void TryParseExtraArguments_PreservesQuotedValues()
        {
            bool ok = LubanCodeGenerator.TryParseExtraArguments(
                "-x l10n.provider=default -xfeature.flag=true --custom \"two words\" 'three words'",
                out IReadOnlyList<string> arguments,
                out string error);

            Assert.That(ok, Is.True, error);
            Assert.That(arguments, Is.EqualTo(new[]
            {
                "-x", "l10n.provider=default", "-xfeature.flag=true", "--custom", "two words", "three words",
            }));
        }

        [TestCase("-x outputCodeDir=Assets/Escape")]
        [TestCase("-x outputDataDir=Assets/Escape")]
        [TestCase("--x=outputCodeDir=Assets/Escape")]
        [TestCase("--xargs outputCodeDir=Assets/Escape")]
        [TestCase("--xargs=outputDataDir=Assets/Escape")]
        [TestCase("-xoutputCodeDir=Assets/Escape")]
        [TestCase("-xoutputDataDir=Assets/Escape")]
        [TestCase("outputDataDir=Assets/Escape")]
        [TestCase("-t another")]
        [TestCase("-tanother")]
        [TestCase("--target=another")]
        [TestCase("-c cs-simple-json")]
        [TestCase("-ccs-simple-json")]
        [TestCase("--codeTarget=cs-simple-json")]
        [TestCase("-d json")]
        [TestCase("-djson")]
        [TestCase("--dataTarget=json")]
        [TestCase("--conf Other/luban.conf")]
        [TestCase("--validationFailAsError=false")]
        [TestCase("-w Config~")]
        [TestCase("-wConfig~")]
        [TestCase("--watchDir=Config~")]
        [TestCase("-vxoutputCodeDir=Assets/Escape")]
        [TestCase("-vtanother")]
        [TestCase("-vwConfig~")]
        [TestCase("-x")]
        [TestCase("-x=missingAssignment")]
        [TestCase("--xargs=missingAssignment")]
        [TestCase("-x key=one --xargs=key=two")]
        [TestCase("--custom \"unterminated")]
        public void TryParseExtraArguments_RejectsReservedOrMalformedValues(string commandLine)
        {
            bool ok = LubanCodeGenerator.TryParseExtraArguments(commandLine, out _, out string error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void Profile_DoesNotSerializeFixedCodeAndDataTargets()
        {
            var profile = CreateConfiguredProfile(
                "Temp/SSFrameworkLubanTarget_" + Guid.NewGuid().ToString("N"),
                "Assets/Generated/Luban/Code_" + Guid.NewGuid().ToString("N"),
                "Assets/Generated/Luban/Data_" + Guid.NewGuid().ToString("N"));
            try
            {
                var serialized = new SerializedObject(profile);

                Assert.That(serialized.FindProperty("_codeTarget"), Is.Null);
                Assert.That(serialized.FindProperty("_dataTarget"), Is.Null);
                Assert.That(LubanCodeGenerator.CodeTarget, Is.EqualTo("cs-bin"));
                Assert.That(LubanCodeGenerator.DataTarget, Is.EqualTo("bin"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private LubanGenerationTransaction CreateTransaction(
            string name,
            string outputCode,
            string outputData) =>
            new(Path.Combine(_testRoot, name), outputCode, outputData, _testRoot);

        private static LubanConfigProfile CreateConfiguredProfile(
            string inputRoot,
            string codeAssetPath,
            string dataAssetPath)
        {
            var profile = ScriptableObject.CreateInstance<LubanConfigProfile>();
            profile.name = "TransactionTest";
            SetString(profile, "_lubanToolPath", inputRoot + "/Luban/Luban.exe");
            SetString(profile, "_confPath", inputRoot + "/Config~/luban.conf");
            SetString(profile, "_outputCodeDir", codeAssetPath);
            SetString(profile, "_outputDataDir", dataAssetPath);
            SetString(profile, "_manifestNamespace", "TransactionTestCfg");
            return profile;
        }

        private static void SetString(LubanConfigProfile profile, string propertyName, string value)
        {
            var serialized = new SerializedObject(profile);
            serialized.FindProperty(propertyName).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Write(string root, string relativePath, string contents)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        private static string Read(string root, string relativePath) =>
            File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        private static IReadOnlyDictionary<string, byte[]> Snapshot(params string[] roots)
        {
            var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int index = 0; index < roots.Length; index++)
            {
                string root = roots[index];
                if (!Directory.Exists(root)) continue;
                foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    snapshot[$"{index}:{relative}"] = File.ReadAllBytes(path);
                }
            }
            return snapshot;
        }

        private static void AssertSnapshotsEqual(
            IReadOnlyDictionary<string, byte[]> expected,
            IReadOnlyDictionary<string, byte[]> actual)
        {
            Assert.That(actual.Keys.OrderBy(key => key), Is.EqualTo(expected.Keys.OrderBy(key => key)));
            foreach (string key in expected.Keys)
                Assert.That(actual[key], Is.EqualTo(expected[key]), key);
        }

        private static string ProjectAbsolute(string relativePath) =>
            Path.GetFullPath(Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        private static void DeleteAbsoluteDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private sealed class PartialFailureRunner : LubanCodeGenerator.ILubanCliRunner
        {
            internal bool ReceivedInvocation { get; private set; }

            public (int exitCode, string log) Run(LubanCodeGenerator.LubanCliInvocation invocation)
            {
                ReceivedInvocation = true;
                Write(invocation.OutputCodeDirectory, "Partial.cs", "partial-code");
                Write(invocation.OutputDataDirectory, "partial.bytes", "partial-data");
                return (7, "injected CLI failure");
            }
        }
    }
}
