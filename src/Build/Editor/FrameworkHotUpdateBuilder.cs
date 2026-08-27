using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Game.Framework.Boot;
using Game.Framework.Editor;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.Installer;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;
using YooAsset;        // EBundleType / EFileNameStyle / EBundledCopyOption
using YooAsset.Editor; // RawFileBuildParameters / RawFileBuildPipeline / 收集器配置

namespace Game.Framework.Build
{
    /// <summary>
    /// 热更 Profile 到 HybridCLR 设置、Generate 环境记录和当前 DLL 中转清单的只读证据。
    /// 它不代表 CDN 已部署版本，只回答本地构建输入与最近一次中转产物是否一致。
    /// </summary>
    [Serializable]
    internal sealed class FrameworkHotUpdateEvidence
    {
        internal string[] ProfileAssemblies = Array.Empty<string>();
        internal string[] HybridClrSettingsAssemblies = Array.Empty<string>();
        internal string[] HybridClrLegacyAssemblies = Array.Empty<string>();
        internal bool SettingsAvailable;
        internal bool SettingsMatch;
        internal string SettingsMessage;
        internal bool GenerationRequired;
        internal bool GenerationFresh;
        internal string GenerationMessage;
        internal bool StagingRequired;
        internal bool StagedManifestExists;
        internal bool StagedManifestAvailable;
        internal bool StagedManifestMatches;
        internal string StagedVersion;
        internal string[] StagedAssemblies = Array.Empty<string>();
        internal string[] ExpectedAotMetadataDlls = Array.Empty<string>();
        internal string[] StagedAotMetadataDlls = Array.Empty<string>();
        internal string[] MissingStagedFiles = Array.Empty<string>();
        internal string[] UnexpectedStagedFiles = Array.Empty<string>();
        internal string[] InvalidStagedEntries = Array.Empty<string>();
        internal string StagedMessage;

        internal bool RequiresAttention => !SettingsAvailable || !SettingsMatch ||
                                           (GenerationRequired && !GenerationFresh) ||
                                           (StagingRequired
                                               ? !StagedManifestAvailable || !StagedManifestMatches
                                               : StagedManifestExists && !StagedManifestMatches);
    }

    /// <summary>
    /// 热更代码构建实现——把「C# 源码改动」变成「CDN 上可下发的代码包」的全部编辑器侧逻辑，
    /// 代码热更新工作台与 CI 都只调这里。两个入口对应两种频率的工作：
    /// <list type="bullet">
    ///   <item><see cref="Generate"/>：包装 HybridCLR 的 <c>Generate/All</c>（Il2CppDef / link.xml / 裁剪 AOT DLL /
    ///         桥接函数 / AOT 泛型引用）。**慢**（内部跑一次迷你构建产裁剪 DLL）。首次接入、Unity/HybridCLR 升级、
    ///         AOT 程序集集合变化（增删第三方库、改热更列表档位）后必须重跑；日常只改热更代码不用。</item>
    ///   <item><see cref="BuildCodePackage"/>：日常热更迭代的一条龙——同步校验 → CompileDll →
    ///         拷热更 DLL + AOT 补元数据 DLL 进中转目录（.bytes）→ 生成 <see cref="HotUpdateManifest"/> →
    ///         确保收集器 → <see cref="RawFileBuildPipeline"/> 打包。产物落 <c>AssetBuild/Bundles</c>，
    ///         部署复用 <see cref="FrameworkAssetBuilder.Deploy"/>（与资源包同一套 Deploy/CDN 流程）。</item>
    /// </list>
    ///
    /// <para><b>中转目录 <see cref="DllAssetDir"/></b>：YooAsset 收集器只认 Assets 内资产，DLL 须先拷进来改名
    /// <c>.bytes</c>（地址规则 AddressByFileName 去扩展名 → location 形如 <c>Game.Framework.dll</c>）。
    /// 整个目录是构建产物（已 gitignore），每次构建全量重建，不要手放东西。</para>
    ///
    /// <para><b>AOT 补元数据清单</b>来自 Generate 产出的 <c>AOTGenericReferences.cs</c>（解析其中
    /// <c>PatchedAOTAssemblyList</c>），对应裁剪 DLL 取自 <c>AssembliesPostIl2CppStrip</c>——
    /// 没跑过 Generate，或生成时的 Unity / HybridCLR / 平台 / Development / 热更列表任一项变化，构建会在产出代码包前停止；
    /// 编辑器联调会旁路 DLL 加载，不能作为生成物新鲜度的依据。</para>
    /// </summary>
    public static class FrameworkHotUpdateBuilder
    {
        /// <summary>DLL/清单中转目录（构建产物，gitignore，收集器自动指向这里）。</summary>
        public const string DllAssetDir = "Assets/HotUpdateDlls";

        // 自动创建的收集器组名——只接管这个组：项目在代码包里手工加的其他组/收集器一概不动。
        private const string CodeGroupName = "HotUpdateCode";

        // YooAsset 构建管线的临时输出子目录名（同 FrameworkAssetBuilder 内联常量，清理版本时跳过）。
        private const string OutputCacheFolderName = "OutputCache";

        // Generate 的结果同时依赖这些编辑器状态。stamp 放在 HybridCLRData（本地生成物目录）里，
        // BuildCodePackage 据此拒绝消费旧版本/旧平台的桥接和裁剪产物。
        private static string GenerationStampPath =>
            Path.Combine(SettingsUtil.HybridCLRDataDir, "SSFramework", "generation-stamp.json");

        /// <summary>
        /// 只读检查 Profile 的三层派生状态。不会同步设置、Generate、CompileDll 或写文件，
        /// 供 Module Audit、CI 与问题排查在执行昂贵构建前解释“期望配置是否已经落到产物”。
        /// </summary>
        internal static FrameworkHotUpdateEvidence InspectEvidence(FrameworkHotUpdateProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var evidence = new FrameworkHotUpdateEvidence
            {
                ProfileAssemblies = GetSortedHotUpdateAssemblyNames(profile).ToArray(),
            };
            evidence.StagingRequired = IsCodePackageRequired(
                evidence.ProfileAssemblies,
                HasHotUpdateLauncherInEnabledScenes());

            InspectHybridClrSettings(evidence);
            InspectGenerationStamp(profile, evidence);
            InspectStagedManifest(evidence);
            return evidence;
        }

        internal static bool IsCodePackageRequired(
            IReadOnlyCollection<string> hotUpdateAssemblies,
            bool hasLauncherInEnabledScene)
            => (hotUpdateAssemblies?.Count ?? 0) > 0 || hasLauncherInEnabledScene;

        /// <summary>
        /// 空热更列表只代表“无需加载热更 DLL”，不代表当前 Player 启动架构不读 CodePackage。
        /// 只要任一启用场景依赖 <see cref="HotUpdateLauncher"/>，Player 分支就仍会初始化包并读取空清单。
        /// </summary>
        private static bool HasHotUpdateLauncherInEnabledScenes()
        {
            string[] launcherScripts = AssetDatabase.FindAssets("HotUpdateLauncher t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => AssetDatabase.LoadAssetAtPath<MonoScript>(path)?.GetClass() ==
                               typeof(HotUpdateLauncher))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (launcherScripts.Length != 1)
                throw new InvalidOperationException(
                    $"无法唯一定位 {typeof(HotUpdateLauncher).FullName} 脚本资产（找到 {launcherScripts.Length} 个）。");

            string launcherScript = launcherScripts[0];
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled || string.IsNullOrWhiteSpace(scene.path)) continue;
                if (AssetDatabase.GetDependencies(scene.path, recursive: true)
                    .Contains(launcherScript, StringComparer.Ordinal))
                    return true;
            }
            return false;
        }

        private static void InspectHybridClrSettings(FrameworkHotUpdateEvidence evidence)
        {
            try
            {
                var settings = HybridCLRSettings.Instance;
                string[] definitions = (settings.hotUpdateAssemblyDefinitions ?? Array.Empty<AssemblyDefinitionAsset>())
                    .Where(asset => asset != null)
                    .Select(ReadAssemblyDefinitionName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                string[] legacy = (settings.hotUpdateAssemblies ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                ApplyHybridClrSettingsEvidence(evidence, definitions, legacy);
            }
            catch (Exception ex)
            {
                evidence.SettingsAvailable = false;
                evidence.SettingsMatch = false;
                evidence.SettingsMessage = "✗ 无法读取 HybridCLRSettings：" + ex.Message;
            }
        }

        internal static void ApplyHybridClrSettingsEvidence(
            FrameworkHotUpdateEvidence evidence,
            IEnumerable<string> definitionAssemblies,
            IEnumerable<string> legacyAssemblies)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            string[] definitions = (definitionAssemblies ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] legacy = (legacyAssemblies ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] effective = definitions.Concat(legacy)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
            evidence.SettingsAvailable = true;
            evidence.HybridClrSettingsAssemblies = effective;
            evidence.HybridClrLegacyAssemblies = legacy;
            bool definitionsMatch = evidence.ProfileAssemblies.SequenceEqual(definitions);
            evidence.SettingsMatch = definitionsMatch && legacy.Length == 0;
            evidence.SettingsMessage = evidence.SettingsMatch
                ? $"✓ HybridCLRSettings 与 Profile 一致（{definitions.Length} 个，字符串名第二来源为空）"
                : "✗ HybridCLRSettings 与 Profile 漂移：" +
                  (definitionsMatch
                      ? "asmdef 列表一致"
                      : DescribeSetDifference(evidence.ProfileAssemblies, definitions,
                          "asmdef 设置缺少", "asmdef 设置多出")) +
                  (legacy.Length > 0
                      ? "；字符串名第二来源仍含 " + string.Join("、", legacy)
                      : string.Empty) +
                  (legacy.Length > 0
                      ? "。确认这些名称并非刻意配置后，在 HybridCLR Settings 清空 hotUpdateAssemblies 字符串列表；" +
                        (definitionsMatch ? "无需重复同步 asmdef 列表。" : "再执行“1. 同步热更设置”。")
                      : "。执行“1. 同步热更设置”。");
        }

        private static void InspectGenerationStamp(
            FrameworkHotUpdateProfile profile,
            FrameworkHotUpdateEvidence evidence)
        {
            evidence.GenerationRequired = evidence.ProfileAssemblies.Length > 0;
            if (!evidence.GenerationRequired)
            {
                evidence.GenerationFresh = true;
                evidence.GenerationMessage = "✓ Profile 为纯 AOT，代码包构建不要求 HybridCLR Generate stamp。";
                return;
            }

            try
            {
                (evidence.GenerationFresh, evidence.GenerationMessage) = ValidateGenerationStamp(profile);
            }
            catch (Exception ex)
            {
                evidence.GenerationFresh = false;
                evidence.GenerationMessage = "✗ 无法校验 Generate 环境记录：" + ex.Message;
            }
        }

        private static void InspectStagedManifest(FrameworkHotUpdateEvidence evidence)
        {
            string path = Path.Combine(DllAssetDir, HotUpdateManifest.Location + ".bytes");
            if (!File.Exists(path))
            {
                ApplyMissingStagedManifestEvidence(evidence);
                return;
            }
            evidence.StagedManifestExists = true;

            try
            {
                HotUpdateManifest manifest = HotUpdateManifest.FromJson(File.ReadAllText(path));
                if (manifest == null)
                    throw new InvalidDataException("JSON 解析结果为空");

                bool aotEvidenceAvailable = TryReadExpectedAotMetadata(
                    evidence.ProfileAssemblies.Length > 0,
                    out string[] expectedAot,
                    out string aotEvidenceError);
                string[] actualDllFiles = Directory.Exists(DllAssetDir)
                    ? Directory.GetFiles(DllAssetDir, "*.dll.bytes", SearchOption.AllDirectories)
                        .Select(file => Path.GetRelativePath(DllAssetDir, file).Replace('\\', '/'))
                        .Where(file => !string.IsNullOrWhiteSpace(file))
                        .ToArray()
                    : Array.Empty<string>();

                string[] expectedHotOrder = HotUpdateAssemblyGraph
                    .SortByDependency(evidence.ProfileAssemblies)
                    .ToArray();
                ApplyStagedManifestEvidence(evidence, manifest, expectedHotOrder, expectedAot,
                    aotEvidenceAvailable, aotEvidenceError, actualDllFiles);
            }
            catch (Exception ex)
            {
                evidence.StagedManifestAvailable = false;
                evidence.StagedManifestMatches = false;
                evidence.StagedMessage = "✗ DLL 中转清单无法解析：" + ex.Message +
                                         "。删除损坏中转产物后重新执行“3. 构建代码包”。";
            }
        }

        internal static void ApplyMissingStagedManifestEvidence(FrameworkHotUpdateEvidence evidence)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            evidence.StagedManifestExists = false;
            evidence.StagedManifestAvailable = false;
            evidence.StagedManifestMatches = !evidence.StagingRequired;
            evidence.StagedMessage = evidence.StagingRequired
                ? evidence.ProfileAssemblies.Length == 0
                    ? "✗ Profile 虽为空，但启用的 Player 场景仍使用 HotUpdateLauncher；它会读取空清单。请执行“3. 构建代码包”，或移除 Boot 引导并改用直接 AOT 启动。"
                    : "✗ 未找到当前 DLL 中转清单；执行“3. 构建代码包”后才会生成。"
                : "○ Profile 为纯 AOT，且启用场景不使用 HotUpdateLauncher：DLL 中转 / CodePackage 可选。";
        }

        internal static void ApplyStagedManifestEvidence(
            FrameworkHotUpdateEvidence evidence,
            HotUpdateManifest manifest,
            IReadOnlyCollection<string> expectedHotOrder,
            IReadOnlyCollection<string> expectedAotMetadata,
            bool aotEvidenceAvailable,
            string aotEvidenceError,
            IEnumerable<string> actualRelativeDllFiles)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            evidence.StagedManifestExists = true;
            evidence.StagedManifestAvailable = true;
            evidence.StagedVersion = manifest.Version ?? string.Empty;
            string[] rawHotEntries = (manifest.HotUpdateDlls ?? new List<string>()).ToArray();
            string[] rawHotAssemblies = rawHotEntries
                .Select(RemoveDllExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            evidence.StagedAssemblies = rawHotAssemblies
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            evidence.StagedAotMetadataDlls = (manifest.AotMetadataDlls ?? new List<string>())
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            evidence.ExpectedAotMetadataDlls = (expectedAotMetadata ?? Array.Empty<string>())
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();

            string[] manifestEntries = (manifest.HotUpdateDlls ?? new List<string>())
                .Concat(manifest.AotMetadataDlls ?? new List<string>())
                .ToArray();
            evidence.InvalidStagedEntries = manifestEntries
                .Where(file => !IsPlainDllFileName(file))
                .Select(file => string.IsNullOrWhiteSpace(file) ? "<空条目>" : file)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            string[] manifestFiles = manifestEntries
                .Where(IsPlainDllFileName)
                .Select(file => file + ".bytes")
                .ToArray();
            var manifestFileSet = new HashSet<string>(manifestFiles, StringComparer.OrdinalIgnoreCase);
            var actualFileSet = new HashSet<string>(
                (actualRelativeDllFiles ?? Array.Empty<string>())
                    .Where(file => !string.IsNullOrWhiteSpace(file))
                    .Select(file => file.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
            evidence.MissingStagedFiles = manifestFileSet
                .Where(file => !actualFileSet.Contains(file))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            evidence.UnexpectedStagedFiles = actualFileSet
                .Where(file => !manifestFileSet.Contains(file))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();

            string[] hotOrder = (expectedHotOrder ?? Array.Empty<string>()).ToArray();
            bool hotOrderMatches = hotOrder.SequenceEqual(evidence.StagedAssemblies);
            bool hotEntriesUnique = rawHotEntries.Length ==
                                    new HashSet<string>(rawHotEntries,
                                        StringComparer.OrdinalIgnoreCase).Count;
            bool aotListMatches = aotEvidenceAvailable &&
                                  evidence.ExpectedAotMetadataDlls.SequenceEqual(evidence.StagedAotMetadataDlls);
            bool manifestEntriesUnique = manifestEntries.Length ==
                                         new HashSet<string>(manifestEntries,
                                             StringComparer.OrdinalIgnoreCase).Count;
            evidence.StagedManifestMatches = hotOrderMatches && hotEntriesUnique &&
                                             aotListMatches && manifestEntriesUnique &&
                                             evidence.InvalidStagedEntries.Length == 0 &&
                                             evidence.MissingStagedFiles.Length == 0 &&
                                             evidence.UnexpectedStagedFiles.Length == 0;
            evidence.StagedMessage = evidence.StagedManifestMatches
                ? $"✓ DLL 中转清单结构与当前派生输入一致（版本 {DisplayVersion(evidence.StagedVersion)}，" +
                  $"热更 DLL {evidence.StagedAssemblies.Length} 个，AOT 补元数据 {evidence.ExpectedAotMetadataDlls.Length} 个，所列文件齐全）"
                : BuildStagedDriftMessage(
                    evidence,
                    hotOrder,
                    hotEntriesUnique,
                    aotEvidenceAvailable,
                    aotEvidenceError,
                    aotListMatches,
                    manifestEntriesUnique);
        }

        private static string ReadAssemblyDefinitionName(AssemblyDefinitionAsset asset)
        {
            try
            {
                return JsonUtility.FromJson<AssemblyDefinitionName>(asset.text)?.name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string RemoveDllExtension(string fileName) =>
            fileName != null && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 4)
                : fileName;

        private static bool IsPlainDllFileName(string fileName) =>
            !string.IsNullOrWhiteSpace(fileName) &&
            !Path.IsPathRooted(fileName) &&
            string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            fileName.Length > 4;

        private static string DisplayVersion(string version) =>
            string.IsNullOrWhiteSpace(version) ? "未标记" : version;

        private static bool TryReadExpectedAotMetadata(
            bool required,
            out string[] expected,
            out string error)
        {
            if (!required)
            {
                expected = Array.Empty<string>();
                error = string.Empty;
                return true;
            }

            try
            {
                List<string> patched = ReadPatchedAotList();
                if (patched == null)
                {
                    expected = Array.Empty<string>();
                    error = "未找到 AOTGenericReferences.cs";
                    return false;
                }

                expected = patched
                    .Where(file => !string.IsNullOrWhiteSpace(file))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(file => file, StringComparer.Ordinal)
                    .ToArray();
                if (expected.Length > 0)
                {
                    error = string.Empty;
                    return true;
                }

                error = "PatchedAOTAssemblyList 为空";
                return false;
            }
            catch (Exception ex)
            {
                expected = Array.Empty<string>();
                error = "无法读取 PatchedAOTAssemblyList：" + ex.Message;
                return false;
            }
        }

        private static string BuildStagedDriftMessage(
            FrameworkHotUpdateEvidence evidence,
            IReadOnlyCollection<string> expectedHotOrder,
            bool hotEntriesUnique,
            bool aotEvidenceAvailable,
            string aotEvidenceError,
            bool aotListMatches,
            bool manifestEntriesUnique)
        {
            var reasons = new List<string>();
            if (!expectedHotOrder.SequenceEqual(evidence.StagedAssemblies))
            {
                var expectedSet = new HashSet<string>(expectedHotOrder, StringComparer.Ordinal);
                if (expectedSet.SetEquals(evidence.StagedAssemblies))
                    reasons.Add("热更加载顺序漂移：期望 " + string.Join(" → ", expectedHotOrder) +
                                "；清单 " + string.Join(" → ", evidence.StagedAssemblies));
                else
                    reasons.Add(DescribeSetDifference(expectedHotOrder, evidence.StagedAssemblies,
                        "热更清单缺少", "热更清单多出"));
            }
            if (!hotEntriesUnique || !manifestEntriesUnique)
                reasons.Add("清单含重复 DLL 条目");
            if (evidence.InvalidStagedEntries.Length > 0)
                reasons.Add("清单含非法文件名 " + string.Join("、", evidence.InvalidStagedEntries));
            if (!aotEvidenceAvailable)
                reasons.Add(aotEvidenceError);
            else if (!aotListMatches)
                reasons.Add(DescribeSetDifference(evidence.ExpectedAotMetadataDlls,
                    evidence.StagedAotMetadataDlls, "AOT 清单缺少", "AOT 清单多出"));
            if (evidence.MissingStagedFiles.Length > 0)
                reasons.Add("清单所列文件缺失 " + string.Join("、", evidence.MissingStagedFiles));
            if (evidence.UnexpectedStagedFiles.Length > 0)
                reasons.Add("目录残留未入清单文件 " + string.Join("、", evidence.UnexpectedStagedFiles));
            if (reasons.Count == 0)
                reasons.Add("清单证据不完整");
            return "✗ DLL 中转清单与当前派生输入 / 中转目录漂移：" + string.Join("；", reasons) +
                   "。执行“3. 构建代码包”重建；这不证明 DLL 内容相对源码新鲜，也不代表 CDN 已部署。";
        }

        private static string DescribeSetDifference(
            IEnumerable<string> expected,
            IEnumerable<string> actual,
            string missingLabel,
            string extraLabel)
        {
            var expectedSet = new HashSet<string>(expected ?? Array.Empty<string>(), StringComparer.Ordinal);
            var actualSet = new HashSet<string>(actual ?? Array.Empty<string>(), StringComparer.Ordinal);
            string[] missing = expectedSet.Except(actualSet).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            string[] extra = actualSet.Except(expectedSet).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var parts = new List<string>();
            if (missing.Length > 0) parts.Add(missingLabel + " " + string.Join("、", missing));
            if (extra.Length > 0) parts.Add(extraLabel + " " + string.Join("、", extra));
            return parts.Count == 0 ? "列表相同，但文件证据不完整" : string.Join("；", parts);
        }

        [Serializable]
        private sealed class AssemblyDefinitionName
        {
            public string name;
        }

        /// <summary>包装 HybridCLR <c>Generate/All</c>（先同步校验热更列表，违规即停）。耗时分钟级，见类型注释的触发时机。</summary>
        public static (bool ok, string message) Generate(FrameworkHotUpdateProfile profile)
        {
            try
            {
                string sync = profile.SyncToHybridCLRSettings();
                if (sync.Contains("✗")) return (false, sync);

                var (installerOk, installerMessage) = ValidateHybridClrInstaller();
                if (!installerOk) return (false, sync + "\n" + installerMessage);

                // Generate 失败时不能继续沿用旧 stamp，否则下一次日常构建会把旧产物误判为新鲜。
                InvalidateGenerationStamp();

                // TMP / TextCore 会在 Player 构建预处理阶段把启用了 Clear Dynamic Data On Build 的
                // 动态 atlas 直接写回源 .asset。Generate 只需要裁剪构建产物，不应让这项 Player 优化污染源码。
                AssetDatabase.SaveAssets();
                BuildMutableAssetSnapshot mutableAssets = BuildMutableAssetSnapshot.Capture();
                int restoredAssets = 0;
                Exception generateFailure = null;
                try
                {
                    PrebuildCommand.GenerateAll();
                }
                catch (Exception exception)
                {
                    generateFailure = exception;
                }

                Exception restoreFailure = null;
                try
                {
                    restoredAssets = mutableAssets.RestoreChangedFiles();
                }
                catch (Exception exception)
                {
                    restoreFailure = exception;
                }
                if (generateFailure != null && restoreFailure != null)
                    throw new AggregateException(
                        "Generate/All 与构建预处理资产恢复均失败；两个异常都必须处理，不能让恢复错误遮蔽原始构建错误。",
                        generateFailure, restoreFailure);
                if (generateFailure != null) ExceptionDispatchInfo.Capture(generateFailure).Throw();
                if (restoreFailure != null) ExceptionDispatchInfo.Capture(restoreFailure).Throw();

                AssetDatabase.Refresh(); // link.xml / AOTGenericReferences.cs 落在 Assets 下，刷新入库
                WriteGenerationStamp(profile);

                return (true, sync + "\n" + installerMessage +
                    "\n✓ Generate/All 完成并记录生成环境（Il2CppDef / link.xml / 裁剪 AOT DLL / 桥接函数 / AOT 泛型引用）。" +
                    (restoredAssets > 0
                        ? $"\n✓ 已恢复 {restoredAssets} 个被构建预处理临时清空的动态字体源资产。"
                        : string.Empty));
            }
            catch (Exception e)
            {
                return (false, "Generate/All 失败；旧生成戳已失效，修复后必须重新执行本步骤。\n" + e);
            }
        }

        /// <summary>
        /// 构建代码包：同步校验 → CompileDll → 重建中转目录 → 生成清单 → RawFile 打包 → 清理旧版本。
        /// 热更列表为空也合法（产出只含空清单的包，引导器读到后直接走入口）。
        /// ⚠ 仅 Edit 模式（构建管线限制，调用方先过 <see cref="FrameworkAssetBuilder.EnsureReadyToBuild"/>）。
        /// </summary>
        public static (bool ok, string message) BuildCodePackage(FrameworkHotUpdateProfile profile, string version)
        {
            try
            {
                var sb = new StringBuilder();
                var target = EditorUserBuildSettings.activeBuildTarget;
                if (!FrameworkBuildArtifactPath.TryNormalizeSegment(
                        profile.CodePackageName, "热更代码包名", out string packageName, out string packageError))
                    return (false, packageError);
                if (!FrameworkBuildArtifactPath.TryNormalizeSegment(
                        version, "热更代码版本号", out version, out string versionError))
                    return (false, versionError);

                // 1. 同步 + 校验：失败即停，不产出与配置不一致的包。
                string sync = profile.SyncToHybridCLRSettings();
                sb.AppendLine(sync);
                if (sync.Contains("✗")) return (false, sb.ToString().TrimEnd());

                var (installerOk, installerMessage) = ValidateHybridClrInstaller();
                sb.AppendLine(installerMessage);
                if (!installerOk) return (false, sb.ToString().TrimEnd());

                // 2. 编译热更 DLL（读刚同步过的 HybridCLRSettings）。
                var hotNames = profile.HotUpdateAssemblyNames;
                if (hotNames.Count > 0)
                {
                    // stamp 的热更侧必须比较当前目标平台 DLL，而不是 Editor ScriptAssemblies；先做一次快速
                    // CompileDll，既刷新证据也是本次代码包本来就需要的产物。
                    CompileDllCommand.CompileDll(target, EditorUserBuildSettings.development);
                    var (fresh, freshnessMessage) = ValidateGenerationStamp(profile);
                    sb.AppendLine(freshnessMessage);
                    if (!fresh) return (false, sb.ToString().TrimEnd());
                }

                // 3. 重建中转目录：热更 DLL（拓扑序）+ AOT 补元数据 DLL + 清单。
                RebuildDllAssetDir();
                var manifest = new HotUpdateManifest { Version = version };

                string hotDllDir = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target);
                foreach (var name in HotUpdateAssemblyGraph.SortByDependency(hotNames))
                {
                    string src = Path.Combine(hotDllDir, name + ".dll");
                    if (!File.Exists(src))
                        return (false, sb.AppendLine($"✗ 热更 DLL 缺失：{src}（CompileDll 未产出该程序集？）").ToString().TrimEnd());
                    File.Copy(src, Path.Combine(DllAssetDir, name + ".dll.bytes"), true);
                    manifest.HotUpdateDlls.Add(name + ".dll");
                }

                // 纯 AOT 档位不需要补元数据 DLL；即使磁盘上还留有旧 Generate 产物，也不应把它们带回空代码包。
                var (aotCopied, aotWarning) = hotNames.Count > 0
                    ? CopyAotMetadataDlls(target, manifest, requireComplete: true)
                    : (0, null);
                if (aotWarning != null) sb.AppendLine(aotWarning);

                File.WriteAllText(Path.Combine(DllAssetDir, HotUpdateManifest.Location + ".bytes"), manifest.ToJson(prettyPrint: true));
                AssetDatabase.Refresh();

                // 4. 确保收集器后走 RawFile 管线打包。
                EnsureCollector(packageName);
                var buildParameters = new RawFileBuildParameters
                {
                    BuildOutputRoot = AssetBuildLayout.BundlesRoot,
                    BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(),
                    BuildPipeline = nameof(RawFileBuildPipeline),
                    BuildBundleType = (int)EBundleType.RawBundle,
                    BuildTarget = target,
                    PackageName = packageName,
                    PackageVersion = version,
                    VerifyBuildingResult = true,
                    FileNameStyle = EFileNameStyle.HashName,
                    // 代码包整包内置进 StreamingAssets：DLL 体量小，换来首启零下载（Host 只拉版本号+清单即可起）。
                    BundledCopyOption = EBundledCopyOption.ClearAndCopyAll,
                };
                var result = new RawFileBuildPipeline().Run(buildParameters, true);
                if (!result.Success)
                    return (false, sb.AppendLine($"✗ 代码包打包失败：[{result.FailedTask}] {result.ErrorInfo}").ToString().TrimEnd());

                CleanupOldVersions(packageName, target);

                sb.AppendLine($"✓ 代码包 '{packageName}' 构建完成 · 平台 {target} · 版本 {version}");
                sb.AppendLine($"    热更 DLL {manifest.HotUpdateDlls.Count} 个（拓扑序）：{string.Join(" → ", manifest.HotUpdateDlls)}");
                sb.AppendLine($"    AOT 补元数据 DLL {aotCopied} 个");
                return (true, sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                return (false, "热更代码包构建抛出未处理异常；不要部署本次中间产物。\n" + e);
            }
        }

        // 中转目录全量重建：整目录都是生成物（含 .meta），残留旧 DLL 会被收集器一并打进包。
        private static void RebuildDllAssetDir()
        {
            if (Directory.Exists(DllAssetDir))
            {
                foreach (var file in Directory.GetFiles(DllAssetDir))
                    File.Delete(file);
                foreach (var directory in Directory.GetDirectories(DllAssetDir))
                    Directory.Delete(directory, recursive: true);
            }
            else
            {
                Directory.CreateDirectory(DllAssetDir);
            }
        }

        // 按 AOTGenericReferences.PatchedAOTAssemblyList 拷裁剪后的 AOT DLL；返回 (拷贝数, 警告或 null)。
        // 有热更程序集时缺生成清单/裁剪 DLL 必须失败：继续打包只会把错误推迟到 IL2CPP 真机启动。
        private static (int copied, string warning) CopyAotMetadataDlls(
            BuildTarget target,
            HotUpdateManifest manifest,
            bool requireComplete)
        {
            List<string> patched;
            try
            {
                patched = ReadPatchedAotList();
            }
            catch (InvalidDataException e)
            {
                string message = "AOTGenericReferences.cs 格式异常，无法确认补元数据程序集清单：" + e.Message +
                    "。请重新执行「2. 生成桥接与裁剪文件」；若仍失败，请检查 HybridCLR 新版本的生成格式。";
                if (requireComplete) throw new InvalidOperationException(message, e);
                return (0, "⚠ " + message);
            }

            if (patched == null)
            {
                const string message = "未找到 AOTGenericReferences.cs（没跑过 Generate/All？）——" +
                    "IL2CPP 真机运行热更代码会缺泛型元数据。先执行「2. 生成桥接与裁剪文件」。";
                if (requireComplete) throw new InvalidOperationException(message);
                return (0, "⚠ " + message);
            }

            if (patched.Count == 0 && requireComplete)
                throw new InvalidOperationException(
                    "AOTGenericReferences.cs 的 PatchedAOTAssemblyList 合法但为空；当前存在热更程序集，" +
                    "无法确认 IL2CPP 真机所需泛型元数据。请重新执行「2. 生成桥接与裁剪文件」。");

            string stripDir = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
            var missing = new List<string>();
            int copied = 0;
            foreach (var dllName in patched) // 形如 "mscorlib.dll"
            {
                string src = Path.Combine(stripDir, dllName);
                if (!File.Exists(src))
                {
                    missing.Add(dllName);
                    continue;
                }
                File.Copy(src, Path.Combine(DllAssetDir, dllName + ".bytes"), true);
                manifest.AotMetadataDlls.Add(dllName);
                copied++;
            }

            if (missing.Count > 0 && requireComplete)
                throw new InvalidOperationException(
                    $"裁剪 AOT DLL 缺失 {missing.Count} 个（{string.Join(", ", missing)}）。" +
                    $"重跑「2. 生成桥接与裁剪文件」后再构建（来源：{stripDir}）。");

            string warning = missing.Count == 0
                ? null
                : $"⚠ 裁剪 AOT DLL 缺失 {missing.Count} 个（{string.Join(", ", missing)}）；当前没有热更程序集，已跳过。";
            return (copied, warning);
        }

        /// <summary>
        /// HybridCLR Package 与 Installer 写入的本地 libil2cpp 必须同版；只升级 UPM 包不会自动替换 C++ Runtime。
        /// 把官方 Player 构建期检查前移到 Generate/代码包入口，错误能在耗时工作开始前暴露。
        /// </summary>
        private static (bool ok, string message) ValidateHybridClrInstaller()
        {
            var installer = new InstallerController();
            if (!installer.HasInstalledHybridCLR())
                return (false, "✗ HybridCLR Runtime 尚未安装。请先执行 HybridCLR/Installer...");
            if (!string.Equals(installer.PackageVersion, installer.InstalledLibil2cppVersion, StringComparison.Ordinal))
                return (false, $"✗ HybridCLR Package {installer.PackageVersion} 与已安装 Runtime " +
                    $"{installer.InstalledLibil2cppVersion ?? "<无>"} 不一致。请重新执行 HybridCLR/Installer...");
            return (true, $"✓ HybridCLR Package / Runtime 均为 {installer.PackageVersion}");
        }

        private static void InvalidateGenerationStamp()
        {
            if (File.Exists(GenerationStampPath))
                File.Delete(GenerationStampPath);
        }

        private static void WriteGenerationStamp(FrameworkHotUpdateProfile profile)
        {
            var installer = new InstallerController();
            var stamp = new GenerationStamp
            {
                FormatVersion = 4,
                UnityVersion = Application.unityVersion,
                HybridClrVersion = installer.PackageVersion,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                Development = EditorUserBuildSettings.development,
                HotUpdateAssemblies = GetSortedHotUpdateAssemblyNames(profile),
                PackageLockSha256 = HashRequiredProjectFile("Packages/packages-lock.json"),
                NuGetPackagesSha256 = HashRequiredProjectFile("Packages/nuget-packages/packages.config"),
                HybridClrSettingsSha256 = HashRequiredProjectFile("ProjectSettings/HybridCLRSettings.asset"),
                PlayerBuildSettings = GetPlayerBuildSettingsFingerprint(),
                HotUpdateTargetMetadataTopologySha256 = GetHotUpdateTargetMetadataTopologyFingerprint(profile),
                AotSourceInputsSha256 = GetAotSourceInputsFingerprint(profile.HotUpdateAssemblyNames),
                PlayerLinkerRootsSha256 = GetPlayerLinkerRootsFingerprint(),
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            };
            string dir = Path.GetDirectoryName(GenerationStampPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(GenerationStampPath, JsonUtility.ToJson(stamp, prettyPrint: true), Encoding.UTF8);
        }

        private static (bool ok, string message) ValidateGenerationStamp(FrameworkHotUpdateProfile profile)
        {
            if (!File.Exists(GenerationStampPath))
                return (false, "✗ 未找到 Generate/All 环境记录。请先执行「2. 生成桥接与裁剪文件」。");

            GenerationStamp stamp;
            try
            {
                stamp = JsonUtility.FromJson<GenerationStamp>(File.ReadAllText(GenerationStampPath, Encoding.UTF8));
            }
            catch (Exception e)
            {
                return (false, "✗ Generate/All 环境记录损坏：" + e.Message + "。请重新执行生成。");
            }

            if (stamp == null || stamp.FormatVersion != 4)
                return (false, "✗ Generate/All 环境记录版本不兼容。请重新执行生成。");

            var installer = new InstallerController();
            var mismatches = new List<string>();
            if (!string.Equals(stamp.UnityVersion, Application.unityVersion, StringComparison.Ordinal))
                mismatches.Add($"Unity {stamp.UnityVersion} → {Application.unityVersion}");
            if (!string.Equals(stamp.HybridClrVersion, installer.PackageVersion, StringComparison.Ordinal))
                mismatches.Add($"HybridCLR {stamp.HybridClrVersion} → {installer.PackageVersion}");
            string target = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!string.Equals(stamp.BuildTarget, target, StringComparison.Ordinal))
                mismatches.Add($"平台 {stamp.BuildTarget} → {target}");
            if (stamp.Development != EditorUserBuildSettings.development)
                mismatches.Add($"Development {stamp.Development} → {EditorUserBuildSettings.development}");
            if (!(stamp.HotUpdateAssemblies ?? new List<string>()).SequenceEqual(GetSortedHotUpdateAssemblyNames(profile)))
                mismatches.Add("热更程序集列表已变化");
            if (!string.Equals(
                    stamp.HotUpdateTargetMetadataTopologySha256,
                    GetHotUpdateTargetMetadataTopologyFingerprint(profile),
                    StringComparison.Ordinal))
                mismatches.Add("目标平台热更 DLL 元数据拓扑已变化");
            if (!string.Equals(
                    stamp.AotSourceInputsSha256,
                    GetAotSourceInputsFingerprint(profile.HotUpdateAssemblyNames),
                    StringComparison.Ordinal))
                mismatches.Add("AOT 程序集源码或预编译输入已变化");
            if (!string.Equals(
                    stamp.PlayerLinkerRootsSha256,
                    GetPlayerLinkerRootsFingerprint(),
                    StringComparison.Ordinal))
                mismatches.Add("Player linker 根（link.xml / 场景 / Resources / Preloaded）已变化");
            if (!string.Equals(stamp.PackageLockSha256, HashRequiredProjectFile("Packages/packages-lock.json"), StringComparison.Ordinal))
                mismatches.Add("Packages/packages-lock.json 已变化");
            if (!string.Equals(stamp.NuGetPackagesSha256, HashRequiredProjectFile("Packages/nuget-packages/packages.config"), StringComparison.Ordinal))
                mismatches.Add("NuGet packages.config 已变化");
            if (!string.Equals(stamp.HybridClrSettingsSha256, HashRequiredProjectFile("ProjectSettings/HybridCLRSettings.asset"), StringComparison.Ordinal))
                mismatches.Add("HybridCLRSettings 已变化");
            string playerSettings = GetPlayerBuildSettingsFingerprint();
            if (!string.Equals(stamp.PlayerBuildSettings, playerSettings, StringComparison.Ordinal))
                mismatches.Add($"AOT PlayerSettings {stamp.PlayerBuildSettings} → {playerSettings}");

            return mismatches.Count == 0
                ? (true, $"✓ Generate/All 产物与当前环境一致（{stamp.GeneratedAtUtc}）")
                : (false, "✗ Generate/All 产物已过期：" + string.Join("；", mismatches) +
                    "。请重新执行「2. 生成桥接与裁剪文件」。");
        }

        private static List<string> GetSortedHotUpdateAssemblyNames(FrameworkHotUpdateProfile profile)
            => profile.HotUpdateAssemblyNames.OrderBy(name => name, StringComparer.Ordinal).ToList();

        /// <summary>
        /// 读取 HybridCLR 针对当前目标平台编译出的热更 DLL，记录结构、签名、泛型、Attribute、P/Invoke 与
        /// 元数据调用点。不能读取 <c>CompilationPipeline.GetAssemblies(Player).outputPath</c>：Unity 6000 返回的
        /// 仍可能是 <c>Library/ScriptAssemblies</c> Editor DLL，即使 defines 表面上没有 UNITY_EDITOR。
        /// </summary>
        private static string GetHotUpdateTargetMetadataTopologyFingerprint(FrameworkHotUpdateProfile profile)
        {
            var topology = new Dictionary<string, string[]>(StringComparer.Ordinal);
            string directory = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(
                EditorUserBuildSettings.activeBuildTarget);
            foreach (string assemblyName in GetSortedHotUpdateAssemblyNames(profile))
            {
                string path = Path.Combine(directory, assemblyName + ".dll");
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"目标平台热更 DLL 尚未产出：{assemblyName}。请先完成 Generate/All 或 CompileDll。", path);
                topology[assemblyName] = FrameworkPlayerMetadataTopology.ReadEntries(path);
            }
            return ComputeDependencyTopologySha256(topology);
        }

        /// <summary>
        /// AOT 目标 DLL 只有在真正 Player Build 后才可靠；日常校验改为哈希所有非热更 Player 源文件、asmdef、
        /// Player defines 与非 Unity 内置预编译 DLL。它对 AOT 普通逻辑变化也保守失效，但不会把热更算法改动
        /// 误判成必须 Generate，同时覆盖 #if !UNITY_EDITOR / 平台分支。
        /// </summary>
        internal static string GetAotSourceInputsFingerprint(IEnumerable<string> hotUpdateAssemblies)
        {
            var hot = new HashSet<string>(hotUpdateAssemblies ?? Array.Empty<string>(), StringComparer.Ordinal);
            UnityEditor.Compilation.Assembly[] playerAssemblies = CompilationPipeline
                .GetAssemblies(AssembliesType.Player)
                .Where(assembly => !FrameworkModuleAudit.IsEditorConstrained(assembly.name))
                .ToArray();
            var playerNames = new HashSet<string>(
                playerAssemblies.Select(assembly => assembly.name), StringComparer.OrdinalIgnoreCase);
            var inputs = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (UnityEditor.Compilation.Assembly assembly in playerAssemblies)
            {
                if ((assembly.defines ?? Array.Empty<string>()).Contains("UNITY_EDITOR", StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"AOT 源输入 {assembly.name} 意外包含 UNITY_EDITOR define，无法建立 Player 新鲜度证据。");
                if (hot.Contains(assembly.name)) continue;

                var entries = new List<string>();
                entries.AddRange(GetCompilerOptionsFingerprintEntries(assembly));
                entries.AddRange((assembly.defines ?? Array.Empty<string>())
                    .OrderBy(define => define, StringComparer.Ordinal)
                    .Select(define => "D|" + define));
                foreach (string sourceFile in (assembly.sourceFiles ?? Array.Empty<string>())
                             .OrderBy(path => path, StringComparer.Ordinal))
                    entries.Add("S|" + HashRequiredFile(sourceFile, assembly.name + " 源文件"));

                string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                if (!string.IsNullOrWhiteSpace(asmdefPath) &&
                    FrameworkModuleSourceCatalog.TryResolve(asmdefPath, out var source, out _))
                    entries.Add("ASMDEF|" + HashRequiredFile(source.PhysicalPath, assembly.name + " asmdef"));
                inputs[assembly.name] = entries.ToArray();
            }

            var precompiledByName = new Dictionary<string, (string path, string hash)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (UnityEditor.Compilation.Assembly assembly in playerAssemblies)
            foreach (string reference in assembly.compiledAssemblyReferences ?? Array.Empty<string>())
            {
                string fullPath = Path.GetFullPath(reference);
                string name = FrameworkModuleAudit.ReadManagedAssemblyIdentity(fullPath);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsPathInside(fullPath, EditorApplication.applicationContentsPath) ||
                    playerNames.Contains(name))
                    continue;
                string hash = HashRequiredFile(fullPath, "Player 预编译依赖");
                if (precompiledByName.TryGetValue(name, out var existing) &&
                    !string.Equals(existing.hash, hash, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Player 编译图中程序集名 {name} 对应多个不同预编译 DLL：{existing.path}；{fullPath}");
                if (!precompiledByName.ContainsKey(name) ||
                    string.Compare(fullPath, existing.path, StringComparison.OrdinalIgnoreCase) < 0)
                    precompiledByName[name] = (fullPath, hash);
            }
            inputs["$precompiled"] = precompiledByName
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "|" + pair.Value.hash)
                .ToArray();
            return ComputeDependencyTopologySha256(inputs);
        }

        /// <summary>
        /// 编译器选项、响应文件和 Roslyn Analyzer / Source Generator 输入同样可以改变 AOT 元数据；
        /// 只哈希源码与 defines 会让 <c>csc.rsp</c> 或生成器配置变化后错误复用旧 Generate 产物。
        /// </summary>
        internal static string[] GetCompilerOptionsFingerprintEntries(
            UnityEditor.Compilation.Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            ScriptCompilerOptions options = assembly.compilerOptions;
            if (options == null)
                throw new InvalidOperationException($"Player 程序集 {assembly.name} 缺少 ScriptCompilerOptions。 ");

            var entries = new List<string>
            {
                $"COMPILER|language={options.LanguageVersion}|api={options.ApiCompatibilityLevel}|" +
                $"unsafe={options.AllowUnsafeCode}|optimization={options.CodeOptimization}|" +
                $"editorCompatibility={options.EditorAssembliesCompatibilityLevel}",
            };
            AddOrderedValues(entries, "ARG", options.AdditionalCompilerArguments);
            AddOrderedCompilerFiles(entries, "RSP", options.ResponseFiles, assembly.name);
            AddOrderedCompilerFiles(entries, "ANALYZER", options.RoslynAnalyzerDllPaths, assembly.name);
            AddOrderedCompilerFiles(entries, "ADDITIONAL", options.RoslynAdditionalFilePaths, assembly.name);
            AddOptionalCompilerFile(entries, "ANALYZER_CONFIG", options.AnalyzerConfigPath, assembly.name);
            AddOptionalCompilerFile(entries, "RULESET", options.RoslynAnalyzerRulesetPath, assembly.name);
            return entries.ToArray();
        }

        private static void AddOrderedValues(ICollection<string> entries, string kind, string[] values)
        {
            string[] source = values ?? Array.Empty<string>();
            for (int index = 0; index < source.Length; index++)
                entries.Add($"{kind}|{index}|{source[index] ?? string.Empty}");
        }

        private static void AddOrderedCompilerFiles(
            ICollection<string> entries,
            string kind,
            string[] paths,
            string assemblyName)
        {
            string[] source = paths ?? Array.Empty<string>();
            for (int index = 0; index < source.Length; index++)
                entries.Add($"{kind}|{index}|{GetCompilerInputFileEvidence(source[index], assemblyName)}");
        }

        private static void AddOptionalCompilerFile(
            ICollection<string> entries,
            string kind,
            string path,
            string assemblyName)
        {
            entries.Add(string.IsNullOrWhiteSpace(path)
                ? kind + "|<none>"
                : kind + "|" + GetCompilerInputFileEvidence(path, assemblyName));
        }

        private static string GetCompilerInputFileEvidence(string path, string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException($"Player 程序集 {assemblyName} 包含空编译器输入路径。 ");
            string fullPath = Path.GetFullPath(path);
            string identity;
            if (IsPathInside(fullPath, AssetBuildLayout.ProjectRoot))
                identity = "$project/" + Path.GetRelativePath(AssetBuildLayout.ProjectRoot, fullPath)
                    .Replace('\\', '/');
            else if (IsPathInside(fullPath, EditorApplication.applicationContentsPath))
                identity = "$unity/" + Path.GetRelativePath(
                        EditorApplication.applicationContentsPath, fullPath)
                    .Replace('\\', '/');
            else
                identity = "$external/" + Path.GetFileName(fullPath);
            return identity + "|" + HashRequiredFile(fullPath, assemblyName + " 编译器输入");
        }

        /// <summary>
        /// 记录 HybridCLR 裁剪 AOT DLL 的非代码根：有效 source link.xml、启用场景、Resources、Preloaded
        /// 资产及其依赖图。序列化资产还记录内容哈希，以发现“依赖集合没变但组件/字段根变化”的情况。
        /// </summary>
        internal static string GetPlayerLinkerRootsFingerprint()
        {
            var inputs = new Dictionary<string, string[]>(StringComparer.Ordinal);
            string generatedLinkXml = NormalizeAssetPath(
                "Assets/" + HybridCLRSettings.Instance.outputLinkFile.TrimStart('/', '\\'));
            var linkEntries = new List<string>();
            foreach (string assetPath in AssetDatabase.GetAllAssetPaths()
                         .Where(path => Path.GetFileName(path).Equals(
                             "link.xml", StringComparison.OrdinalIgnoreCase))
                         .Select(NormalizeAssetPath)
                         .Where(path => !path.Equals(generatedLinkXml, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!FrameworkModuleSourceCatalog.TryResolve(assetPath, out var source, out string reason))
                    throw new InvalidDataException($"无法解析 UnityLinker 输入 {assetPath}：{reason}");
                linkEntries.Add(assetPath + "|" + HashRequiredFile(source.PhysicalPath, assetPath));
            }
            inputs["$link.xml"] = linkEntries.ToArray();

            // Unity Package 与项目 Editor 代码都能在构建期动态生成额外 link.xml。记录 processor 类型和
            // 当前实现程序集，至少让实现变更主动失效；processor 读取的任意外部配置仍属于扩展边界。
            inputs["$dynamic-processors"] = TypeCache.GetTypesDerivedFrom<IUnityLinkerProcessor>()
                .Where(type => type != null && type.Assembly != null)
                .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal)
                .Select(type =>
                {
                    string assemblyPath = type.Assembly.Location;
                    string implementation = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath)
                        ? HashRequiredFile(assemblyPath, type.FullName + " linker processor")
                        : "<dynamic-assembly>";
                    return type.AssemblyQualifiedName + "|" + implementation;
                })
                .ToArray();

            var rootEntries = new List<string>();
            var roots = new SortedSet<string>(StringComparer.Ordinal);
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int index = 0; index < scenes.Length; index++)
            {
                EditorBuildSettingsScene scene = scenes[index];
                string path = NormalizeAssetPath(scene.path);
                rootEntries.Add($"SCENE|{index}|enabled={scene.enabled}|{path}");
                if (scene.enabled && !string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            foreach (string path in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0)
                         .Where(path => !AssetDatabase.IsValidFolder(path)))
                roots.Add(NormalizeAssetPath(path));
            UnityEngine.Object[] preloaded = PlayerSettings.GetPreloadedAssets() ?? Array.Empty<UnityEngine.Object>();
            for (int index = 0; index < preloaded.Length; index++)
            {
                string path = preloaded[index] == null
                    ? "<null>"
                    : NormalizeAssetPath(AssetDatabase.GetAssetPath(preloaded[index]));
                rootEntries.Add($"PRELOADED|{index}|{path}");
                if (!string.IsNullOrWhiteSpace(path) && path != "<null>") roots.Add(path);
            }

            foreach (string root in roots)
            {
                rootEntries.Add("ROOT|" + root);
                string[] dependencies = AssetDatabase.GetDependencies(root, recursive: true)
                    .Select(NormalizeAssetPath)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                foreach (string dependency in dependencies)
                {
                    rootEntries.Add($"DEP|{root}|{dependency}");
                    if (!IsSerializedLinkerRootAsset(dependency)) continue;
                    if (!FrameworkModuleSourceCatalog.TryResolve(
                            dependency, out var source, out string reason))
                        throw new InvalidDataException(
                            $"无法解析 UnityLinker 序列化依赖 {dependency}（根：{root}）：{reason}");
                    rootEntries.Add($"SERIALIZED|{dependency}|" +
                                    HashRequiredFile(source.PhysicalPath, dependency));
                }
            }
            inputs["$roots"] = rootEntries.ToArray();
            return ComputeDependencyTopologySha256(inputs);
        }

        internal static bool IsSerializedLinkerRootAsset(string assetPath)
        {
            string extension = Path.GetExtension(assetPath);
            return extension.Equals(".unity", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".asset", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".controller", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".overrideController", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".playable", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".anim", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mat", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".uxml", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".guiskin", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAssetPath(string path) =>
            (path ?? string.Empty).Replace('\\', '/');

        private static bool IsPathInside(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>对程序集名和完整依赖条目分别排序；保留重复数量，同时避免元数据表返回顺序造成伪失效。</summary>
        internal static string ComputeDependencyTopologySha256(
            IReadOnlyDictionary<string, string[]> topology)
        {
            if (topology == null) throw new ArgumentNullException(nameof(topology));
            using var canonical = new MemoryStream();
            using (var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true))
            {
                var assemblies = topology.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                writer.Write(assemblies.Length);
                foreach (var pair in assemblies)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        throw new ArgumentException("程序集依赖拓扑不能包含空程序集名。", nameof(topology));
                    WriteLengthPrefixedUtf8(writer, pair.Key);
                    string[] entries = (pair.Value ?? Array.Empty<string>())
                        .Where(entry => entry != null)
                        .OrderBy(entry => entry, StringComparer.Ordinal)
                        .ToArray();
                    writer.Write(entries.Length);
                    foreach (string entry in entries) WriteLengthPrefixedUtf8(writer, entry);
                }
            }

            using var sha256 = SHA256.Create();
            canonical.Position = 0;
            byte[] hash = sha256.ComputeHash(canonical);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteLengthPrefixedUtf8(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        // UPM 包锁和 NuGet 清单决定实际进入 AOT 世界的第三方程序集版本；HybridCLRSettings 决定桥接、裁剪与生成策略。
        // 只看“版本显示值”不够，直接记内容哈希能覆盖 lock 重解析、显式 DLL 依赖与设置新增字段等变化。
        private static string HashRequiredProjectFile(string relativePath)
        {
            string path = Path.Combine(AssetBuildLayout.ProjectRoot, relativePath);
            return HashRequiredFile(path, relativePath);
        }

        private static string HashRequiredFile(string path, string description)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"生成环境输入文件不存在：{description}", fullPath);
            using var stream = File.OpenRead(fullPath);
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // 只记录确实影响 AOT 裁剪/代码生成的 PlayerSettings，避免产品名、图标等无关改动让日常热更被迫重跑 Generate。
        private static string GetPlayerBuildSettingsFingerprint()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);
            return string.Join("|", new[]
            {
                PlayerSettings.GetScriptingBackend(namedTarget).ToString(),
                PlayerSettings.GetApiCompatibilityLevel(namedTarget).ToString(),
                PlayerSettings.GetManagedStrippingLevel(namedTarget).ToString(),
                PlayerSettings.GetIl2CppCompilerConfiguration(namedTarget).ToString(),
                $"StripEngineCode={PlayerSettings.stripEngineCode}",
            });
        }

        /// <summary>
        /// 保护 Unity 构建预处理会原地修改的项目资产。只选择序列化字段明确启用了
        /// <c>m_ClearDynamicDataOnBuild</c> 的 FontAsset，避免把普通构建输出或用户在构建期间的其它修改一起回滚。
        /// </summary>
        private sealed class BuildMutableAssetSnapshot
        {
            private readonly Entry[] _entries;

            private BuildMutableAssetSnapshot(Entry[] entries) => _entries = entries;

            internal static BuildMutableAssetSnapshot Capture()
            {
                var paths = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string filter in new[] { "t:FontAsset", "t:TMP_FontAsset" })
                foreach (string guid in AssetDatabase.FindAssets(filter, new[] { "Assets" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) continue;
                    UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null) continue;
                    using var serialized = new SerializedObject(asset);
                    SerializedProperty clearOnBuild = serialized.FindProperty("m_ClearDynamicDataOnBuild");
                    if (clearOnBuild?.boolValue == true) paths.Add(path);
                }

                return new BuildMutableAssetSnapshot(paths.Select(path => new Entry(
                        path,
                        File.ReadAllBytes(Path.Combine(AssetBuildLayout.ProjectRoot, path))))
                    .ToArray());
            }

            internal int RestoreChangedFiles()
            {
                int restored = 0;
                var failures = new List<Exception>();
                foreach (Entry entry in _entries)
                {
                    try
                    {
                        string physicalPath = Path.Combine(AssetBuildLayout.ProjectRoot, entry.AssetPath);
                        if (File.Exists(physicalPath) && File.ReadAllBytes(physicalPath).SequenceEqual(entry.Bytes))
                            continue;
                        File.WriteAllBytes(physicalPath, entry.Bytes);
                        AssetDatabase.ImportAsset(entry.AssetPath, ImportAssetOptions.ForceUpdate);
                        restored++;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new IOException("恢复被构建预处理修改的资产失败：" + entry.AssetPath, exception));
                    }
                }
                if (failures.Count > 0)
                    throw new AggregateException(
                        $"{failures.Count} 个构建期可变资产恢复失败；已继续尝试其余资产。", failures);
                return restored;
            }

            private readonly struct Entry
            {
                internal Entry(string assetPath, byte[] bytes)
                {
                    AssetPath = assetPath;
                    Bytes = bytes;
                }

                internal string AssetPath { get; }
                internal byte[] Bytes { get; }
            }
        }

        [Serializable]
        private sealed class GenerationStamp
        {
            public int FormatVersion;
            public string UnityVersion;
            public string HybridClrVersion;
            public string BuildTarget;
            public bool Development;
            public List<string> HotUpdateAssemblies;
            public string PackageLockSha256;
            public string NuGetPackagesSha256;
            public string HybridClrSettingsSha256;
            public string PlayerBuildSettings;
            public string HotUpdateTargetMetadataTopologySha256;
            public string AotSourceInputsSha256;
            public string PlayerLinkerRootsSha256;
            public string GeneratedAtUtc;
        }

        // 解析 Generate 产物 AOTGenericReferences.cs 里的 PatchedAOTAssemblyList。
        // 走文本解析而非反射读编译产物：不依赖 Assembly-CSharp 的编译状态（生成文件刚落盘还没编译时也能读）。
        private static List<string> ReadPatchedAotList()
        {
            string path = "Assets/" + HybridCLRSettings.Instance.outputAOTGenericReferenceFile;
            if (!File.Exists(path)) return null;

            string text = File.ReadAllText(path);
            int start = text.IndexOf("PatchedAOTAssemblyList", StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidDataException("找不到 PatchedAOTAssemblyList 标记");
            int blockStart = text.IndexOf('{', start);
            if (blockStart < 0)
                throw new InvalidDataException("PatchedAOTAssemblyList 缺少列表起始符 '{'");
            int end = text.IndexOf("};", blockStart, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidDataException("PatchedAOTAssemblyList 缺少列表结束符 '};'");
            string block = text.Substring(blockStart, end - blockStart);
            return Regex.Matches(block, "\"([^\"]+\\.dll)\"")
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToList();
        }

        // 确保收集器里有「代码包 + 自动组 + 指向中转目录的 RawFile 收集器」。幂等；只接管自动组，不动项目手工配置。
        private static void EnsureCollector(string packageName)
        {
            var setting = BundleCollectorSettingData.Setting;
            bool changed = false;
            var pkg = setting.Packages.FirstOrDefault(p => p.PackageName == packageName);
            if (pkg == null)
            {
                pkg = new BundleCollectorPackage
                {
                    PackageName = packageName,
                    PackageDesc = "热更代码包（框架热更构建管线自动创建）",
                    AutoCollectShaders = false, // 纯 DLL 包，无 shader 可收
                };
                setting.Packages.Add(pkg);
                changed = true;
            }

            // EnableAddressable 必须为 true：AddressByFileName 生成的短地址（"hotupdate_manifest"、"Game.Framework.dll" 等）
            // 只有在此选项开启时才会写入 YooAsset location 字典；关闭时只能用完整 AssetPath 查询，
            // 运行时 LoadAssetAsync("hotupdate_manifest") 就会报 "Location is invalid"。
            // 对已有 package 也强制更新（幂等），避免历史存档未开启时遗留旧包失效。
            if (!pkg.EnableAddressable)
            {
                pkg.EnableAddressable = true;
                changed = true;
            }

            var group = pkg.Groups.FirstOrDefault(g => g.GroupName == CodeGroupName);
            if (group == null)
            {
                group = new BundleCollectorGroup { GroupName = CodeGroupName, GroupDesc = "热更 DLL + 清单（自动维护，勿手动改）" };
                pkg.Groups.Add(group);
                changed = true;
            }

            changed |= ApplyCodeCollectorGroupContract(
                group, DllAssetDir, AssetDatabase.AssetPathToGUID(DllAssetDir));
            // YooAsset 的 SaveFile 会重写整份 YAML；配置未变时跳过，避免一次日常代码包构建制造无关 diff。
            if (changed) BundleCollectorSettingData.SaveFile();
        }

        internal static bool ApplyCodeCollectorContract(
            BundleCollector collector,
            string collectPath,
            string collectorGuid)
        {
            if (collector == null) throw new ArgumentNullException(nameof(collector));
            bool changed = false;
            changed |= SetIfDifferent(collector.CollectPath, collectPath,
                value => collector.CollectPath = value);
            changed |= SetIfDifferent(collector.CollectorGUID, collectorGuid,
                value => collector.CollectorGUID = value);
            if (collector.CollectorType != ECollectorType.MainAssetCollector)
            {
                collector.CollectorType = ECollectorType.MainAssetCollector;
                changed = true;
            }
            changed |= SetIfDifferent(collector.AddressRuleName, nameof(AddressByFileName),
                value => collector.AddressRuleName = value);
            changed |= SetIfDifferent(collector.PackRuleName, nameof(PackRawFile),
                value => collector.PackRuleName = value);
            changed |= SetIfDifferent(collector.FilterRuleName, nameof(CollectAll),
                value => collector.FilterRuleName = value);
            return changed;
        }

        internal static bool ApplyCodeCollectorGroupContract(
            BundleCollectorGroup group,
            string collectPath,
            string collectorGuid)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            bool changed = false;
            // CodeGroupName 是框架声明的自动维护组，不允许旧路径或重复 Collector 残留并越界收集。
            BundleCollector collector = group.Collectors.FirstOrDefault();
            if (collector == null)
            {
                collector = new BundleCollector();
                group.Collectors.Add(collector);
                changed = true;
            }
            foreach (BundleCollector duplicate in group.Collectors.Skip(1).ToArray())
            {
                group.Collectors.Remove(duplicate);
                changed = true;
            }
            return ApplyCodeCollectorContract(collector, collectPath, collectorGuid) || changed;
        }

        private static bool SetIfDifferent(string current, string expected, Action<string> setter)
        {
            if (string.Equals(current, expected, StringComparison.Ordinal)) return false;
            setter(expected);
            return true;
        }

        // 只保留最近 2 个版本目录（与 FrameworkAssetBuilder.CleanupOldVersions 同思路；代码包不读资源 profile 的保留数）。
        private static void CleanupOldVersions(string packageName, BuildTarget target)
        {
            string platformRoot = Path.Combine(AssetBuildLayout.BundlesRoot, target.ToString());
            if (!FrameworkBuildArtifactPath.TryResolveChildDirectory(
                    platformRoot, packageName, "热更代码包名", out string root, out string error))
            {
                Debug.LogError("[热更构建] 拒绝清理旧版本：" + error);
                return;
            }
            if (!Directory.Exists(root)) return;

            var versionDirs = Directory.GetDirectories(root)
                .Where(d => !string.Equals(Path.GetFileName(d), OutputCacheFolderName, StringComparison.Ordinal))
                .OrderByDescending(Directory.GetLastWriteTime)
                .ToList();
            for (int i = 2; i < versionDirs.Count; i++)
            {
                try { Directory.Delete(versionDirs[i], true); }
                catch (Exception e) { Debug.LogWarning($"[热更构建] 清理旧版本失败 {versionDirs[i]}：{e.Message}"); }
            }
        }
    }
}
