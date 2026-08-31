using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Process = System.Diagnostics.Process;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 在 <c>Library</c> 下的隔离 Unity 工程中执行 Framework Module 删除构建，并汇总真实 Player BuildReport。
    /// </summary>
    /// <remarks>
    /// 主工程里的 HybridCLR 清单、业务场景和未选 Module 的 <c>link.xml</c> 都会污染“Core-only”结果，
    /// 所以本探针不在当前工程里伪装裁剪。它只把所选 Module 及真实第三方依赖复制进一次性工程，
    /// 再用当前目标平台、脚本后端与裁剪级别构建空场景。所选程序集以 <c>preserve="all"</c> 保留，
    /// 数字表示确定性的体积上界，而不是某个具体游戏实际使用部分的精确增量。
    /// </remarks>
    internal static class FrameworkBuildSizeProbe
    {
        internal const string RunsRoot = "Library/SSFramework/BuildSizeProbe";
        internal const string ChildTemplateFileName = "FrameworkBuildSizeProbeChild.cs.txt";
        internal const string FrozenInputsDirectoryName = "Inputs";
        internal const string UnityIl2CppPathEnvironmentVariable = "UNITY_IL2CPP_PATH";

        private const string LatestRunPreferencePrefix = "SSFramework.BuildSizeProbe.LatestRun.";
        private const string PreviousLatestRunPreferencePrefix =
            "SSFramework.BuildSizeProbe.PreviousLatestRun.";
        internal const int CurrentReportFormatVersion = 9;
        private static readonly Regex DependencyEntryRegex = new(
            "\\\"(?<id>[^\\\"]+)\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
            RegexOptions.Compiled);

        private static ActiveRun _activeRun;
        private static Process _childProcess;
        private static ProfilePlan _activeProfile;

        internal static event Action Changed;

        private sealed class FrozenInputDriftException : Exception
        {
            internal FrozenInputDriftException(string message) : base(message) { }
        }

        internal enum ChildProcessAttachResult
        {
            Attached,
            ConfirmedNotOwned,
            UnknownInspectionFailure,
        }

        [Serializable]
        internal sealed class ModuleSourcePlan
        {
            public string AssemblyName;
            public string AssetDirectory;
            [NonSerialized]
            public string PhysicalDirectory;
            public string PackageName;
            public string PackageVersion;
            public string PackageId;
            public string SourceFingerprint;
        }

        [Serializable]
        internal sealed class PackageSourcePlan
        {
            public string PackageName;
            public string AssetDirectory;
            [NonSerialized]
            public string PhysicalDirectory;
            public string PackageVersion;
            public string PackageId;
            public string SourceFingerprint;
        }

        internal sealed class PackageDependencyPlan
        {
            internal string[] ManifestPackages = Array.Empty<string>();
            internal PackageSourcePlan[] CopiedPackages = Array.Empty<PackageSourcePlan>();
        }

        [Serializable]
        internal sealed class ProfilePlan
        {
            public string Key;
            public string Title;
            public string Description;
            public string[] RootAssemblies = Array.Empty<string>();
            public string[] Assemblies = Array.Empty<string>();
            public ModuleSourcePlan[] Sources = Array.Empty<ModuleSourcePlan>();
            public string[] ManifestPackages = Array.Empty<string>();
            public string ManifestFingerprint;
            [NonSerialized]
            public string MinimalManifest;
            public PackageSourcePlan[] CopiedPackages = Array.Empty<PackageSourcePlan>();
            public bool IsAdvanced;
        }

        [Serializable]
        internal sealed class OutputFileRecord
        {
            public string Path;
            public string Role;
            public long Bytes;
        }

        [Serializable]
        internal sealed class ProfileRecord
        {
            public string Key;
            public string Title;
            public string Status;
            public string Message;
            public string[] Assemblies = Array.Empty<string>();
            public ModuleSourcePlan[] Sources = Array.Empty<ModuleSourcePlan>();
            public string[] ManifestPackages = Array.Empty<string>();
            public string ManifestFingerprint;
            public PackageSourcePlan[] CopiedPackages = Array.Empty<PackageSourcePlan>();
            [NonSerialized]
            public string OutputPath;
            [NonSerialized]
            public string ResultPath;
            [NonSerialized]
            public string LogPath;
            public long BuildReportBytes;
            public long RawOutputBytes;
            public long OutputBytes;
            public double DurationSeconds;
            public int Errors;
            public int Warnings;
            public int ExitCode;
            public int ChildProcessId;
            public long ChildProcessStartUtcTicks;
            public OutputFileRecord[] LargestFiles = Array.Empty<OutputFileRecord>();
        }

        [Serializable]
        internal sealed class RunReport
        {
            public int FormatVersion = CurrentReportFormatVersion;
            public string EvidenceImplementationFingerprint;
            public string ChildTemplateFingerprint;
            public string CreatedUtc;
            public string CompletedUtc;
            public string UnityVersion;
            public string Target;
            public string ScriptingBackend;
            public string StrippingLevel;
            public bool DevelopmentBuild;
            public string EvidenceScope;
            public string StopAfterCurrentReason;
            [NonSerialized]
            public string RunDirectory;
            public ProfileRecord[] Profiles = Array.Empty<ProfileRecord>();
        }

        private sealed class ActiveRun
        {
            internal string ProjectDirectory;
            internal string RunDirectory;
            internal string PreviousLatestRunDirectory;
            internal Queue<ProfilePlan> Pending;
            internal RunReport Report;
        }

        [InitializeOnLoadMethod]
        private static void ScheduleRecovery()
        {
            EditorApplication.update -= RecoverWhenEditorReady;
            EditorApplication.update += RecoverWhenEditorReady;
        }

        private static void RecoverWhenEditorReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= RecoverWhenEditorReady;
            RecoverInterruptedRun();
        }

        internal static bool IsRunning => _activeRun != null;
        internal static bool StopAfterCurrentRequested =>
            !string.IsNullOrWhiteSpace(_activeRun?.Report?.StopAfterCurrentReason);
        internal static RunReport CurrentReport => _activeRun?.Report;

        /// <summary>
        /// 无窗口状态的 Core 删除测试入口，供 CI / AI 自动化复用与人工快速回归。
        /// 完整组合选择仍使用“真实构建体积证据”窗口。
        /// </summary>
        [MenuItem(FrameworkMenuPaths.CoreBuildSizeProbe, priority = 31)]
        private static void StartCoreOnlyFromMenu() => StartFromAutomationMenu(
            "Core 隔离构建",
            "已在 Library/SSFramework/BuildSizeProbe 启动独立 Core Player Build；结果完成后可在“真实构建体积证据”窗口查看。",
            "core");

        /// <summary>
        /// 无窗口状态的常用 UI 删除测试入口；把 Core 基线与两个单后端档位置于同一报告，
        /// 既验证可独立编译，也能直接计算相对 Core 的发布输出差值。
        /// </summary>
        [MenuItem(FrameworkMenuPaths.CommonBuildSizeProbe, priority = 32)]
        private static void StartCommonProfilesFromMenu() => StartFromAutomationMenu(
            "常用档位隔离构建",
            "已在 Library/SSFramework/BuildSizeProbe 顺序启动 Core、UGUI、Toolkit 独立 Player Build；主 Unity 可留在后台。",
            "core", "ugui", "toolkit");

        private static void StartFromAutomationMenu(string title, string summary, params string[] profileKeys)
        {
            try
            {
                Start(profileKeys);
                FrameworkEditorFeedback.ReportSummary(title, summary);
            }
            catch (Exception exception)
            {
                FrameworkEditorFeedback.ReportResult(title, false, exception.Message);
                throw;
            }
        }

        internal static string LatestRunDirectory =>
            EditorPrefs.GetString(LatestRunPreferencePrefix + HashProjectPath(), string.Empty);

        internal static RunReport LoadLatestReport()
        {
            string directory = LatestRunDirectory;
            string path = string.IsNullOrWhiteSpace(directory) ? string.Empty : Path.Combine(directory, "report.json");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var report = JsonUtility.FromJson<RunReport>(File.ReadAllText(path, Encoding.UTF8));
                RestoreOperationalPaths(report, directory);
                NormalizeShippingEvidence(report);
                return report;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 运行目录由本机 EditorPrefs 定位，不写进可分享报告；恢复时按稳定 Profile key 重建。
        /// </summary>
        internal static void RestoreOperationalPaths(RunReport report, string runDirectory)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(runDirectory))
                throw new ArgumentException("体积探针运行目录为空。", nameof(runDirectory));
            report.RunDirectory = Path.GetFullPath(runDirectory);
            foreach (ProfileRecord record in report.Profiles ?? Array.Empty<ProfileRecord>())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Key)) continue;
                record.OutputPath = Path.Combine(report.RunDirectory, "Output", record.Key);
                record.ResultPath = Path.Combine(report.RunDirectory, "Results", record.Key + ".json");
                record.LogPath = Path.Combine(report.RunDirectory, "Logs", record.Key + ".log");
            }
        }

        internal static void RevealLatestRun()
        {
            string directory = LatestRunDirectory;
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                EditorUtility.RevealInFinder(directory);
        }

        internal static void RebuildLatestReportsFromOutputs()
        {
            RunReport report = LoadLatestReport();
            if (report == null) return;
            EnsureReportCanBeRebuilt(report);
            WriteReports(report);
            NotifyChanged();
        }

        internal static void EnsureReportCanBeRebuilt(RunReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (report.FormatVersion > CurrentReportFormatVersion)
                throw new InvalidDataException(
                    $"报告格式 v{report.FormatVersion} 新于当前工具支持的 v{CurrentReportFormatVersion}；" +
                    "拒绝用旧代码重写并丢失未知字段。请切回生成该报告的版本。 ");
        }

        internal static ProfilePlan[] CreatePlans() => CreatePlansForKeys(requestedKeys: null);

        private static ProfilePlan[] CreatePlansForKeys(IEnumerable<string> requestedKeys)
        {
            // 执行计划必须来自当前证据，不能把窗口 Preview 缓存冒充冻结输入；刷新结果会顺带供窗口复用。
            FrameworkModuleAuditCache.Entry evidence = FrameworkModuleAuditCache.Refresh();
            return CreatePlans(evidence.Snapshot, evidence.Result, requestedKeys);
        }

        private static ProfilePlan[] CreatePlans(
            FrameworkModuleAudit.Snapshot snapshot,
            FrameworkModuleAudit.AuditResult result,
            IEnumerable<string> requestedKeys)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (result == null) throw new ArgumentNullException(nameof(result));
            var copiedSourceCache = new Dictionary<string, PackageSourcePlan>(StringComparer.Ordinal);
            string sourceManifest = File.ReadAllText(FullPath("Packages/manifest.json"), Encoding.UTF8);
            var allProfiles = result.CommonProfiles
                .Select(profile => (profile, advanced: false))
                .Concat(new[] { (profile: result.FullProfile, advanced: false) })
                .Concat(result.ModuleProfiles.Select(profile => (profile, advanced: true)))
                .Where(item => item.profile != null)
                .ToArray();
            string[] requested = requestedKeys == null
                ? null
                : requestedKeys
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            if (requested != null && requested.Length == 0)
                throw new InvalidOperationException("至少选择一个构建组合。");
            if (requested != null)
            {
                var availableKeys = new HashSet<string>(
                    allProfiles.Select(item => item.profile.Key), StringComparer.Ordinal);
                string[] missing = requested
                    .Where(key => !availableKeys.Contains(key))
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException(
                        "请求的构建组合当前不存在：" + string.Join("、", missing) + "。" +
                        "可能已物理删除对应 Module；请改用真实构建体积窗口重新选择。 ");
            }
            var profiles = requested == null
                ? allProfiles
                : allProfiles.Where(item => requested.Contains(item.profile.Key, StringComparer.Ordinal)).ToArray();
            var runtimeByName = result.RuntimeModules.ToDictionary(module => module.Name, StringComparer.Ordinal);
            var prepared = profiles.Select(item =>
            {
                string[] assemblies = BuildFrameworkCompileClosure(
                    snapshot, item.profile.Footprint.FrameworkAssemblies, runtimeByName.Keys);
                PackageDependencyPlan dependencies = BuildPackageDependencyPlan(
                    snapshot, assemblies, copiedSourceCache);
                string minimalManifest = CreateMinimalManifest(
                    sourceManifest, dependencies.ManifestPackages);
                return (item.profile, item.advanced, assemblies, dependencies, minimalManifest);
            }).ToArray();
            var requiredAssemblies = new HashSet<string>(
                prepared.SelectMany(item => item.assemblies), StringComparer.Ordinal);
            var sourceByName = runtimeByName.Values
                .Where(module => requiredAssemblies.Contains(module.Name))
                .Select(module => new ModuleSourcePlan
                {
                    AssemblyName = module.Name,
                    AssetDirectory = NormalizeAssetPath(Path.GetDirectoryName(module.AsmdefPath)),
                    PhysicalDirectory = module.SourceDirectory,
                    PackageName = module.PackageName,
                    PackageVersion = module.PackageVersion,
                    PackageId = StablePackageIdForReport(
                        module.SourceKind, module.PackageName, module.PackageVersion, module.PackageId),
                    SourceFingerprint = ComputeModuleSourceFingerprint(module.SourceDirectory),
                })
                .ToDictionary(source => source.AssemblyName, StringComparer.Ordinal);
            ValidateDisjointSourceDirectories(sourceByName.Values);
            return prepared.Select(item =>
            {
                FrameworkModuleAudit.AuditProfile profile = item.profile;
                return new ProfilePlan
                {
                    Key = profile.Key,
                    Title = profile.Title,
                    Description = profile.Description,
                    RootAssemblies = profile.Roots,
                    Assemblies = item.assemblies,
                    Sources = item.assemblies.Select(name => sourceByName[name]).ToArray(),
                    ManifestPackages = item.dependencies.ManifestPackages,
                    ManifestFingerprint = ComputeTextFingerprint(item.minimalManifest),
                    MinimalManifest = item.minimalManifest,
                    CopiedPackages = item.dependencies.CopiedPackages,
                    IsAdvanced = item.advanced,
                };
            }).ToArray();
        }

        /// <summary>
        /// 实际 DLL 闭包决定“当前用了什么”，asmdef 声明闭包决定“隔离工程至少要安装什么才能编译”。
        /// 探针复制两者并完整保留，避免 declared-only Module 因当前 IL 未发出 AssemblyRef 而缺席。
        /// </summary>
        internal static string[] BuildFrameworkCompileClosure(
            FrameworkModuleAudit.Snapshot snapshot,
            IEnumerable<string> actualFrameworkAssemblies,
            IEnumerable<string> availableRuntimeAssemblies)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (actualFrameworkAssemblies == null)
                throw new ArgumentNullException(nameof(actualFrameworkAssemblies));
            if (availableRuntimeAssemblies == null)
                throw new ArgumentNullException(nameof(availableRuntimeAssemblies));

            var available = new HashSet<string>(availableRuntimeAssemblies, StringComparer.Ordinal);
            var selected = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            foreach (string root in actualFrameworkAssemblies
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                if (!available.Contains(root))
                    throw new InvalidDataException($"体积档位包含不可用的 Framework Runtime Module：{root}。");
                if (selected.Add(root)) pending.Enqueue(root);
            }

            while (pending.Count > 0)
            {
                string assemblyName = pending.Dequeue();
                if (!snapshot.Assemblies.TryGetValue(
                        assemblyName, out FrameworkModuleAudit.AssemblyInfo assembly))
                    throw new InvalidDataException($"找不到 Framework Runtime Module {assemblyName} 的编译快照。");
                foreach (string reference in assembly.DeclaredReferences
                             .Where(IsFrameworkAssemblyName)
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(name => name, StringComparer.Ordinal))
                {
                    if (!available.Contains(reference))
                        throw new InvalidDataException(
                            $"{assemblyName} 显式声明了不可用或非 Runtime 的 Framework Module {reference}；" +
                            "隔离构建不能把声明边静默当成未使用。");
                    if (selected.Add(reference)) pending.Enqueue(reference);
                }
            }

            return selected.ToArray();
        }

        private static bool IsFrameworkAssemblyName(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            (name.Equals(FrameworkModuleAudit.CoreAssemblyName, StringComparison.Ordinal) ||
             name.StartsWith(FrameworkModuleAudit.CoreAssemblyName + ".", StringComparison.Ordinal));

        /// <summary>
        /// 从所选 Framework asmdef 的声明闭包派生隔离工程依赖。Package 名与安装形态只由
        /// <see cref="FrameworkModuleSourceCatalog"/> 证据决定，不再按 Module 名维护映射表。
        /// </summary>
        internal static PackageDependencyPlan BuildPackageDependencyPlan(
            FrameworkModuleAudit.Snapshot snapshot,
            IEnumerable<string> selectedAssemblies)
            => BuildPackageDependencyPlan(
                snapshot, selectedAssemblies,
                new Dictionary<string, PackageSourcePlan>(StringComparer.Ordinal));

        private static PackageDependencyPlan BuildPackageDependencyPlan(
            FrameworkModuleAudit.Snapshot snapshot,
            IEnumerable<string> selectedAssemblies,
            IDictionary<string, PackageSourcePlan> copiedSourceCache)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (selectedAssemblies == null) throw new ArgumentNullException(nameof(selectedAssemblies));
            if (copiedSourceCache == null) throw new ArgumentNullException(nameof(copiedSourceCache));

            var selected = new HashSet<string>(
                selectedAssemblies.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.Ordinal);
            var manifestPackages = new SortedSet<string>(StringComparer.Ordinal);
            var copiedPackages = new SortedDictionary<string, PackageSourcePlan>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(selected.OrderBy(name => name, StringComparer.Ordinal));

            while (pending.Count > 0)
            {
                string assemblyName = pending.Dequeue();
                if (!visited.Add(assemblyName) ||
                    !snapshot.Assemblies.TryGetValue(assemblyName, out FrameworkModuleAudit.AssemblyInfo assembly))
                    continue;

                var declared = new HashSet<string>(assembly.DeclaredReferences
                    .Concat(assembly.DeclaredPrecompiledReferences), StringComparer.Ordinal);
                foreach (string reference in declared
                             .Concat(assembly.ActualReferences)
                             .Where(name => !string.IsNullOrWhiteSpace(name))
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(name => name, StringComparer.Ordinal))
                {
                    if (selected.Contains(reference))
                    {
                        pending.Enqueue(reference);
                        continue;
                    }

                    if (!snapshot.DependencySources.TryGetValue(
                            reference, out FrameworkModuleAudit.DependencySource source) ||
                        !source.IsKnown)
                    {
                        // 当前 DLL 元数据还会包含 BCL / Unity 平台程序集；它们没有可安装 Package 来源。
                        // 普通未知外部 DLL（含显式声明）不能静默跳过，否则隔离工程会在子进程里才暴露缺包。
                        if (declared.Contains(reference) ||
                            !FrameworkModuleAudit.IsPlatformReference(snapshot, reference))
                            throw new InvalidDataException(
                                $"无法从 Source Catalog 解析 {assemblyName} → {reference} 的外部依赖来源；" +
                                "隔离构建拒绝按程序集名猜 Package。");
                        continue;
                    }

                    if (IsCopiedPackageSource(source.SourceKind))
                    {
                        if (string.IsNullOrWhiteSpace(source.PackageName))
                            throw new InvalidDataException($"需复制的 Package 依赖 {reference} 缺少 Package 名称。");
                        if (!copiedPackages.ContainsKey(source.PackageName))
                        {
                            if (!copiedSourceCache.TryGetValue(
                                    source.PackageName, out PackageSourcePlan packagePlan))
                            {
                                packagePlan = CreateCopiedPackageSourcePlan(source);
                                copiedSourceCache.Add(source.PackageName, packagePlan);
                            }
                            copiedPackages.Add(source.PackageName, packagePlan);
                        }
                        continue;
                    }

                    switch (source.SourceKind)
                    {
                        case FrameworkModuleSourceCatalog.SourceKind.BuiltInPackage:
                            // com.unity.modules.* 是隔离工程固定引擎背景；UGUI 等随 Editor 分发但仍需
                            // manifest 选择的 built-in Package，继续按 Source Catalog 给出的 Package 名记录。
                            if (!string.IsNullOrWhiteSpace(source.PackageName) &&
                                !source.PackageName.StartsWith("com.unity.modules.", StringComparison.Ordinal))
                                manifestPackages.Add(source.PackageName);
                            break;
                        case FrameworkModuleSourceCatalog.SourceKind.RegistryPackage:
                            if (string.IsNullOrWhiteSpace(source.PackageName))
                                throw new InvalidDataException($"Package 依赖 {reference} 缺少 Package 名称。");
                            manifestPackages.Add(source.PackageName);
                            break;
                        case FrameworkModuleSourceCatalog.SourceKind.ProjectAssets:
                            throw new InvalidDataException(
                                $"{assemblyName} 依赖项目 Assets 中的 {reference}；当前隔离探针只复制所选 Framework Module " +
                                "与 Package 来源，不能把项目代码 / DLL 静默夹进框架体积证据。请先将该依赖归入 Module 或 Package。");
                        default:
                            if (!declared.Contains(reference) &&
                                FrameworkModuleAudit.IsPlatformReference(snapshot, reference))
                                break;
                            throw new InvalidDataException(
                                $"{assemblyName} → {reference} 的来源类型为 {source.SourceKind}，无法生成可恢复的隔离依赖计划。");
                    }

                    // 外部 Package 的传递依赖由其 package.json 负责；这里仅记录 Framework Module
                    // 直接接触的 Package，避免把主工程 packages-lock 中的偶然传递版本升级成根 manifest 依赖。
                }
            }

            return new PackageDependencyPlan
            {
                ManifestPackages = manifestPackages.ToArray(),
                CopiedPackages = copiedPackages.Values.ToArray(),
            };
        }

        internal static bool IsCopiedPackageSource(FrameworkModuleSourceCatalog.SourceKind sourceKind) =>
            sourceKind is FrameworkModuleSourceCatalog.SourceKind.EmbeddedPackage or
                FrameworkModuleSourceCatalog.SourceKind.LocalPackage or
                FrameworkModuleSourceCatalog.SourceKind.LocalTarballPackage or
                FrameworkModuleSourceCatalog.SourceKind.GitPackage;

        private static PackageSourcePlan CreateCopiedPackageSourcePlan(
            FrameworkModuleAudit.DependencySource source)
        {
            FrameworkModuleSourceCatalog.SourceLocation location =
                FrameworkModuleSourceCatalog.Resolve("Packages/" + source.PackageName);
            if (!Directory.Exists(location.PhysicalPath))
                throw new DirectoryNotFoundException(
                    $"找不到需复制 Package {source.PackageName} 的物理目录：{location.PhysicalPath}");
            ValidateCopiedPackageDependencies(location);
            return new PackageSourcePlan
            {
                PackageName = source.PackageName,
                AssetDirectory = location.AssetPath,
                PhysicalDirectory = location.PhysicalPath,
                PackageVersion = location.PackageVersion,
                PackageId = StablePackageIdForReport(
                    location.Kind, location.PackageName, location.PackageVersion, location.PackageId),
                SourceFingerprint = ComputeCopiedPackageSourceFingerprint(location.PhysicalPath),
            };
        }

        internal static void ValidateCopiedPackageDependencies(
            FrameworkModuleSourceCatalog.SourceLocation location)
        {
            string packageJson = Path.Combine(location.PhysicalPath, "package.json");
            if (!File.Exists(packageJson))
                throw new FileNotFoundException(
                    $"需复制 Package {location.PackageName} 缺少 package.json。", packageJson);
            Dictionary<string, string> dependencies = ReadDependencyEntries(
                File.ReadAllText(packageJson, Encoding.UTF8), requireDependencies: false);
            string[] localDependencies = dependencies
                .Where(pair => pair.Value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key + " = " + pair.Value)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (localDependencies.Length > 0)
                throw new InvalidDataException(
                    $"需复制 Package {location.PackageName} 的 package.json 含相对工作区的本地传递依赖：" +
                    string.Join(", ", localDependencies) + "。当前探针不重写第三方 package.json；" +
                    "请先把该依赖改为 registry 版本或独立 embedded Package，再生成可恢复体积证据。");
        }

        /// <summary>
        /// copied Package 的 Unity packageId 可能含本机路径、Git URL userinfo 或 token，不适合作为
        /// 可分享身份；报告只保留包名与版本，精确内容另由 SHA-256 负责。Registry 继续使用 manifest 指纹。
        /// </summary>
        internal static string StablePackageIdForReport(
            FrameworkModuleSourceCatalog.SourceKind sourceKind,
            string packageName,
            string packageVersion,
            string packageId)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return string.Empty;
            if (sourceKind is FrameworkModuleSourceCatalog.SourceKind.EmbeddedPackage or
                FrameworkModuleSourceCatalog.SourceKind.LocalPackage or
                FrameworkModuleSourceCatalog.SourceKind.LocalTarballPackage or
                FrameworkModuleSourceCatalog.SourceKind.GitPackage)
                return packageName + (string.IsNullOrWhiteSpace(packageVersion)
                    ? string.Empty
                    : "@" + packageVersion);
            return string.IsNullOrWhiteSpace(packageId)
                ? packageName + (string.IsNullOrWhiteSpace(packageVersion)
                    ? string.Empty
                    : "@" + packageVersion)
                : packageId;
        }

        internal static void Start(IEnumerable<string> selectedKeys)
        {
            if (selectedKeys == null) throw new ArgumentNullException(nameof(selectedKeys));
            if (IsRunning) throw new InvalidOperationException("已有一轮真实构建体积探针正在运行。");
            if (!FrameworkEditorOperationGate.CanStart(requireEditMode: true, out string blockedReason))
                throw new InvalidOperationException(blockedReason);
            string[] requested = selectedKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requested.Length == 0)
                throw new InvalidOperationException("至少选择一个构建组合。");
            ProfilePlan[] plans = SelectRequestedPlans(requested, CreatePlansForKeys(requested));

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + target;
            string runDirectory = FullPath(Path.Combine(RunsRoot, runId));
            string projectDirectory = Path.Combine(runDirectory, "Project");
            string childTemplateContent = ReadCurrentChildTemplate();
            string previousLatestRunDirectory = LatestRunDirectory;

            var report = new RunReport
            {
                EvidenceImplementationFingerprint =
                    ComputeEvidenceImplementationFingerprint(childTemplateContent),
                ChildTemplateFingerprint = ComputeTextFingerprint(childTemplateContent),
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                UnityVersion = Application.unityVersion,
                Target = target.ToString(),
                ScriptingBackend = PlayerSettings.GetScriptingBackend(namedTarget).ToString(),
                StrippingLevel = PlayerSettings.GetManagedStrippingLevel(namedTarget).ToString(),
                DevelopmentBuild = EditorUserBuildSettings.development,
                EvidenceScope =
                    "隔离空工程只包含所选 Framework Module；程序集完整保留，表示链接、AOT 与平台压缩后的体积上界。",
                RunDirectory = runDirectory,
                Profiles = plans.Select(plan => new ProfileRecord
                {
                    Key = plan.Key,
                    Title = plan.Title,
                    Status = "等待",
                    Message = "等待前序组合完成。",
                    Assemblies = plan.Assemblies,
                    Sources = plan.Sources,
                    ManifestPackages = plan.ManifestPackages,
                    ManifestFingerprint = plan.ManifestFingerprint,
                    CopiedPackages = plan.CopiedPackages,
                }).ToArray(),
            };

            PrepareWorkspace(projectDirectory, childTemplateContent);
            _activeRun = new ActiveRun
            {
                ProjectDirectory = projectDirectory,
                RunDirectory = runDirectory,
                PreviousLatestRunDirectory = previousLatestRunDirectory,
                Pending = new Queue<ProfilePlan>(plans),
                Report = report,
            };
            try
            {
                // previous latest 属于本机 owner journal，不进入可分享 JSON。report.json 是恢复提交标记，
                // 因而必须先完整发布首代报告、最后才切 latest：任意中断点都至少保留一份可读取的 latest。
                EditorPrefs.SetString(
                    PreviousLatestRunPreferencePrefix + HashProjectPath(),
                    previousLatestRunDirectory ?? string.Empty);
                WriteReports(_activeRun.Report);
                EditorPrefs.SetString(LatestRunPreferencePrefix + HashProjectPath(), runDirectory);
            }
            catch (Exception exception)
            {
                AbortActiveRunAfterPersistenceFailure(exception, "写入初始报告");
                throw new IOException(
                    "隔离构建尚未启动：无法安全写入可恢复报告。请检查 Library 目录权限和磁盘状态。",
                    exception);
            }
            EditorApplication.update += PollChildProcess;
            StartNextProfile();
            if (_activeRun == null)
            {
                ProfileRecord failure = report.Profiles.FirstOrDefault(record => record.Status == "失败");
                throw new InvalidOperationException(
                    "隔离构建任务未能启动首个 Unity 子进程：" +
                    (failure?.Message ?? "请打开最近报告查看准备阶段错误。"));
            }
        }

        /// <summary>
        /// 将自动化请求解析为当前真实存在的 Profile。请求多档时必须全部可用；物理删除某个 Module 后
        /// 静默缩成较小矩阵会让 CI / AI 把不完整证据误报为成功。
        /// </summary>
        internal static ProfilePlan[] SelectRequestedPlans(
            IEnumerable<string> selectedKeys,
            IEnumerable<ProfilePlan> availablePlans)
        {
            if (selectedKeys == null) throw new ArgumentNullException(nameof(selectedKeys));
            if (availablePlans == null) throw new ArgumentNullException(nameof(availablePlans));

            string[] requested = selectedKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requested.Length == 0)
                throw new InvalidOperationException("至少选择一个构建组合。");

            ProfilePlan[] available = availablePlans.Where(plan => plan != null).ToArray();
            var availableKeys = new HashSet<string>(
                available.Where(plan => !string.IsNullOrWhiteSpace(plan.Key)).Select(plan => plan.Key),
                StringComparer.Ordinal);
            string[] missing = requested
                .Where(key => !availableKeys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    "请求的隔离构建档位当前不可用：" + string.Join(", ", missing) +
                    "。对应 Module 可能已物理删除；请改用仍存在的档位或在窗口中重新选择。");

            var selected = new HashSet<string>(requested, StringComparer.Ordinal);
            return available.Where(plan => selected.Contains(plan.Key)).ToArray();
        }

        internal static void RequestStopAfterCurrent()
        {
            if (!IsRunning) return;
            _activeRun.Report.StopAfterCurrentReason =
                "用户请求：当前组合完成后停止，不再启动后续档位。";
            foreach (var record in _activeRun.Report.Profiles.Where(record => record.Status == "等待"))
                record.Message = _activeRun.Report.StopAfterCurrentReason;
            try
            {
                WriteReports(_activeRun.Report);
            }
            catch (Exception exception)
            {
                // 停止意图仍保留在内存；当前 child 继续由 Poll 拥有。磁盘恢复后最终报告会再次尝试落盘。
                LogPersistenceFailure(exception, "保存‘当前完成后停止’状态");
            }
            NotifyChanged();
        }

        internal static string CreateLinkXml(IEnumerable<string> assemblyNames)
        {
            if (assemblyNames == null) throw new ArgumentNullException(nameof(assemblyNames));
            var sb = new StringBuilder(512);
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<linker>");
            foreach (string name in assemblyNames
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                sb.Append("  <assembly fullname=\"")
                    .Append(SecurityElement.Escape(name))
                    .AppendLine("\" preserve=\"all\" />");
            }
            sb.AppendLine("</linker>");
            return sb.ToString();
        }

        internal static string CreateMinimalManifest(
            string sourceManifest,
            IEnumerable<string> requiredPackageNames)
        {
            if (sourceManifest == null) throw new ArgumentNullException(nameof(sourceManifest));
            if (requiredPackageNames == null) throw new ArgumentNullException(nameof(requiredPackageNames));

            var required = new HashSet<string>(
                requiredPackageNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.Ordinal);

            var dependencies = ReadDependencyEntries(sourceManifest);
            foreach (string module in dependencies.Keys.Where(id => id.StartsWith("com.unity.modules.", StringComparison.Ordinal)))
                required.Add(module);

            string[] missing = required.Where(id => !dependencies.ContainsKey(id)).OrderBy(id => id).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("主工程 Packages/manifest.json 缺少探针所需依赖：" +
                                                    string.Join(", ", missing));

            var sb = new StringBuilder(4096);
            sb.AppendLine("{");
            sb.AppendLine("  \"dependencies\": {");
            string[] ordered = required.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                string suffix = i + 1 < ordered.Length ? "," : string.Empty;
                sb.Append("    \"").Append(ordered[i]).Append("\": \"")
                    .Append(dependencies[ordered[i]]).Append('"').AppendLine(suffix);
            }
            string scopedRegistries = ExtractJsonArrayProperty(sourceManifest, "scopedRegistries");
            sb.AppendLine(scopedRegistries.Length > 0 ? "  }," : "  }");
            if (scopedRegistries.Length > 0)
                sb.Append("  \"scopedRegistries\": ").AppendLine(scopedRegistries);
            sb.AppendLine("}");
            return sb.ToString();
        }

        internal static bool ShouldSkipModulePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string normalized = relativePath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            return segments.Any(segment =>
                segment.Equals("Editor", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Tests", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Editor.meta", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Tests.meta", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Test.meta", StringComparison.OrdinalIgnoreCase));
        }

        private static void PrepareWorkspace(string projectDirectory, string childTemplateContent)
        {
            if (Directory.Exists(projectDirectory))
                throw new IOException("隔离构建目录已经存在，拒绝覆盖：" + projectDirectory);
            if (childTemplateContent == null)
                throw new ArgumentNullException(nameof(childTemplateContent));

            Directory.CreateDirectory(Path.Combine(projectDirectory, "Assets", "Editor"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Packages"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "ProjectSettings"));

            string frozenTemplate = FrozenChildTemplatePath(projectDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(frozenTemplate) ??
                                      throw new InvalidDataException("无法解析冻结输入目录。"));
            File.WriteAllText(frozenTemplate, childTemplateContent, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(projectDirectory, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs"),
                childTemplateContent,
                new UTF8Encoding(false));

            string projectVersion = FullPath("ProjectSettings/ProjectVersion.txt");
            File.Copy(projectVersion, Path.Combine(projectDirectory, "ProjectSettings", "ProjectVersion.txt"));
        }

        private static void StartNextProfile()
        {
            if (_activeRun == null) return;
            string stopReason = _activeRun.Report.StopAfterCurrentReason;
            if (!string.IsNullOrWhiteSpace(stopReason) || _activeRun.Pending.Count == 0)
            {
                CompleteRun(stopReason);
                return;
            }

            _activeProfile = _activeRun.Pending.Dequeue();
            ProfileRecord record = FindRecord(_activeProfile.Key);
            record.Status = "准备";
            record.Message = "正在复制所选 Module 并准备隔离工程。";
            NotifyChanged();

            try
            {
                PrepareProfileSources(
                    _activeRun.ProjectDirectory,
                    _activeProfile,
                    _activeRun.Report);
                string outputPath = Path.Combine(_activeRun.RunDirectory, "Output", _activeProfile.Key);
                string resultPath = Path.Combine(_activeRun.RunDirectory, "Results", _activeProfile.Key + ".json");
                string logPath = Path.Combine(_activeRun.RunDirectory, "Logs", _activeProfile.Key + ".log");
                Directory.CreateDirectory(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath) ?? _activeRun.RunDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? _activeRun.RunDirectory);

                record.OutputPath = outputPath;
                record.ResultPath = resultPath;
                record.LogPath = logPath;
                record.Status = "构建中";
                record.Message = $"Unity 子进程正在构建 {_activeProfile.Title}；主工程可继续查看，但不要重复启动探针。";
                WriteReports(_activeRun.Report);
                NotifyChanged();

                _childProcess = StartUnityChild(_activeRun, _activeProfile, outputPath, resultPath, logPath);
                record.ChildProcessId = _childProcess.Id;
                record.ChildProcessStartUtcTicks = _childProcess.StartTime.ToUniversalTime().Ticks;
                try
                {
                    WriteReports(_activeRun.Report);
                }
                catch (Exception exception)
                {
                    // child 已经启动，不能因报告 I/O 失败把它留成无 owner 进程，也不能覆盖引用去跑下一档。
                    StopAfterCurrentForOwnerFailure(record, exception, "保存子进程 PID");
                    return;
                }
                NotifyChanged();
            }
            catch (Exception ex)
            {
                if (_childProcess != null)
                {
                    // Process.Start 之后的 PID 读取、报告写入或 Changed 订阅者也可能抛错；此时 child
                    // 已经归本轮所有，绝不能递归启动下一档并覆盖唯一进程句柄。
                    StopAfterCurrentForOwnerFailure(record, ex, "接管已启动的子进程");
                    return;
                }
                record.Status = "失败";
                record.Message = ex.Message;
                record.Errors = Math.Max(record.Errors, 1);
                try
                {
                    WriteReports(_activeRun.Report);
                }
                catch (Exception persistenceException)
                {
                    AbortActiveRunAfterPersistenceFailure(
                        persistenceException,
                        "记录档位准备失败");
                    return;
                }
                Debug.LogException(ex);
                if (ex is FrozenInputDriftException)
                {
                    CompleteRun(ex.Message);
                    return;
                }
                StartNextProfile();
            }
        }

        private static Process StartUnityChild(
            ActiveRun run,
            ProfilePlan profile,
            string outputPath,
            string resultPath,
            string logPath)
        {
            var arguments = new List<string>
            {
                "-batchmode",
                "-quit",
                "-nographics",
                "-accept-apiupdate",
                "-projectPath", run.ProjectDirectory,
                "-buildTarget", run.Report.Target,
                "-executeMethod", "SSFrameworkBuildProbeChild.Run",
                "-ssProbeProfile", profile.Key,
                "-ssProbeAssemblies", string.Join(";", profile.Assemblies ?? Array.Empty<string>()),
                "-ssProbeOutput", outputPath,
                "-ssProbeResult", resultPath,
                "-ssProbeBackend", run.Report.ScriptingBackend,
                "-ssProbeStripping", run.Report.StrippingLevel,
                "-ssProbeDevelopment", run.Report.DevelopmentBuild ? "true" : "false",
                "-logFile", logPath,
            };
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = EditorApplication.applicationPath,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = run.ProjectDirectory,
            };
            SanitizeChildEnvironment(startInfo);
            Process process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("无法启动隔离 Unity 子进程。");
            return process;
        }

        private static void PollChildProcess()
        {
            if (_activeRun == null)
            {
                EditorApplication.update -= PollChildProcess;
                return;
            }
            if (_childProcess == null || !_childProcess.HasExited) return;

            int exitCode = _childProcess.ExitCode;
            _childProcess.Dispose();
            _childProcess = null;
            ProfileRecord record = FindRecord(_activeProfile.Key);
            record.ExitCode = exitCode;
            record.ChildProcessId = 0;
            record.ChildProcessStartUtcTicks = 0;
            ApplyChildResult(record, exitCode);
            try
            {
                WriteReports(_activeRun.Report);
            }
            catch (Exception exception)
            {
                // child 已到终态，立即收口内存状态，避免 update 每帧看到 null child 而永久保持 running。
                AbortActiveRunAfterPersistenceFailure(exception, "保存子进程结果");
                return;
            }
            NotifyChanged();
            StartNextProfile();
        }

        private static void ApplyChildResult(ProfileRecord record, int exitCode)
        {
            if (!string.IsNullOrWhiteSpace(record.ResultPath) && File.Exists(record.ResultPath))
            {
                try
                {
                    var child = JsonUtility.FromJson<ProfileRecord>(
                        File.ReadAllText(record.ResultPath, Encoding.UTF8));
                    record.Status = child.Status;
                    record.Message = child.Message;
                    record.BuildReportBytes = child.BuildReportBytes;
                    record.RawOutputBytes = child.RawOutputBytes > 0
                        ? child.RawOutputBytes
                        : child.OutputBytes;
                    record.OutputBytes = child.OutputBytes;
                    record.DurationSeconds = child.DurationSeconds;
                    record.Errors = child.Errors;
                    record.Warnings = child.Warnings;
                    record.LargestFiles = child.LargestFiles ?? Array.Empty<OutputFileRecord>();
                    if (!string.IsNullOrWhiteSpace(child.OutputPath)) record.OutputPath = child.OutputPath;
                    NormalizeShippingEvidence(record);
                    if (exitCode != 0 && record.Status == "成功")
                    {
                        record.Status = "失败";
                        record.Message = "子进程返回非零退出码：" + exitCode;
                    }
                    return;
                }
                catch (Exception ex)
                {
                    record.Message = "结果文件无法解析：" + ex.Message;
                }
            }

            record.Status = "失败";
            record.Errors = Math.Max(record.Errors, 1);
            string logExcerpt = ReadDiagnosticLogExcerpt(record.LogPath, 12);
            record.Message = string.IsNullOrWhiteSpace(logExcerpt)
                ? $"Unity 子进程退出码 {exitCode}，且没有生成结果文件。"
                : $"Unity 子进程退出码 {exitCode}。关键日志：\n{logExcerpt}";
        }

        private static void CompleteRun(string note)
        {
            if (_activeRun == null) return;
            CompleteWaitingProfiles(_activeRun.Report, note);
            _activeRun.Report.CompletedUtc = DateTime.UtcNow.ToString("O");
            try
            {
                WriteReports(_activeRun.Report);
                ClearPreviousLatestRunPreferenceNoThrow();
            }
            catch (Exception exception)
            {
                // 旧 JSON 仍是未完成状态；不能让 Domain Reload 把已经在内存中结束的任务复活。
                RestoreLatestRunPreference(_activeRun);
                LogPersistenceFailure(exception, "保存最终报告");
            }
            finally
            {
                ClearActiveRunState();
            }
        }

        private static void StopAfterCurrentForOwnerFailure(
            ProfileRecord record,
            Exception exception,
            string phase)
        {
            if (_activeRun == null) return;
            string reason = $"主进程状态提交失败；当前组合完成后停止（{phase}）：{exception.Message}";
            _activeRun.Report.StopAfterCurrentReason = reason;
            if (record != null)
                record.Message = (record.Message ?? string.Empty) + "\n" + reason;
            foreach (ProfileRecord waiting in _activeRun.Report.Profiles.Where(item => item.Status == "等待"))
                waiting.Message = reason;
            LogPersistenceFailure(exception, phase);
            NotifyChanged();
        }

        private static void AbortActiveRunAfterPersistenceFailure(Exception exception, string phase)
        {
            ActiveRun failedRun = _activeRun;
            try
            {
                if (_activeRun != null)
                {
                    string reason = $"报告持久化失败，本轮已安全收口（{phase}）：{exception.Message}";
                    _activeRun.Report.StopAfterCurrentReason = reason;
                    CompleteWaitingProfiles(_activeRun.Report, reason);
                    _activeRun.Report.CompletedUtc = DateTime.UtcNow.ToString("O");
                }
                RestoreLatestRunPreference(failedRun);
                LogPersistenceFailure(exception, phase);
            }
            finally
            {
                // EditorPrefs 或日志本身异常也不能把 IsRunning 留在 true。
                ClearActiveRunState();
            }
        }

        private static void RestoreLatestRunPreference(ActiveRun failedRun)
        {
            if (failedRun == null) return;
            string key = LatestRunPreferencePrefix + HashProjectPath();
            string previousKey = PreviousLatestRunPreferencePrefix + HashProjectPath();
            // 只有本轮仍占据 latest 指针时才恢复，避免覆盖别的窗口/进程刚写入的新任务。
            try
            {
                if (!string.Equals(EditorPrefs.GetString(key, string.Empty), failedRun.RunDirectory,
                        StringComparison.Ordinal))
                    return;
                string previous = !string.IsNullOrWhiteSpace(failedRun.PreviousLatestRunDirectory)
                    ? failedRun.PreviousLatestRunDirectory
                    : EditorPrefs.GetString(previousKey, string.Empty);
                if (string.IsNullOrWhiteSpace(previous))
                    EditorPrefs.DeleteKey(key);
                else
                    EditorPrefs.SetString(key, previous);
            }
            finally
            {
                EditorPrefs.DeleteKey(previousKey);
            }
        }

        private static void ClearPreviousLatestRunPreferenceNoThrow()
        {
            try
            {
                EditorPrefs.DeleteKey(PreviousLatestRunPreferencePrefix + HashProjectPath());
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BuildSizeProbe] 清理本地 previous-latest owner journal 失败：{exception.Message}");
            }
        }

        private static string ReadPreviousLatestRunDirectory()
        {
            try
            {
                return EditorPrefs.GetString(
                    PreviousLatestRunPreferencePrefix + HashProjectPath(), string.Empty);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BuildSizeProbe] 读取本地 previous-latest owner journal 失败：{exception.Message}");
                return string.Empty;
            }
        }

        private static void ClearActiveRunState()
        {
            Process child = _childProcess;
            _activeRun = null;
            _activeProfile = null;
            _childProcess = null;
            if (child != null)
            {
                try
                {
                    if (child.HasExited) child.Dispose();
                }
                catch
                {
                    // 进程句柄的清理不应让 Editor 回到永久 running；活动 child 不会走此分支。
                }
            }
            EditorApplication.update -= PollChildProcess;
            NotifyChanged();
        }

        private static void LogPersistenceFailure(Exception exception, string phase) =>
            Debug.LogError(
                $"[BuildSizeProbe] {phase}失败；旧报告仍保持完整，详情：{exception}");

        internal static void NotifyChanged()
        {
            Action handlers = Changed;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch (Exception exception)
                {
                    // UI 观察者不拥有探针状态机，坏窗口不能阻断下一档、最终落盘或 owner 清理。
                    Debug.LogError($"[BuildSizeProbe] 状态观察者刷新失败，探针继续运行：{exception}");
                }
            }
        }

        /// <summary>
        /// 最终跳过原因优先使用调用点给出的失败说明，其次使用报告中可跨 Domain Reload 恢复的
        /// “当前完成后停止”原因。不能把自动证据漂移改写成人工停止，也不能在重载后继续队列。
        /// </summary>
        internal static void CompleteWaitingProfiles(RunReport report, string note = null)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            string reason = !string.IsNullOrWhiteSpace(note)
                ? note
                : !string.IsNullOrWhiteSpace(report.StopAfterCurrentReason)
                    ? report.StopAfterCurrentReason
                    : "未运行。";
            foreach (ProfileRecord record in
                     (report.Profiles ?? Array.Empty<ProfileRecord>())
                     .Where(record => record != null && record.Status == "等待"))
            {
                record.Status = "跳过";
                record.Message = reason;
            }
        }

        private static void RecoverInterruptedRun()
        {
            if (IsRunning) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleRecovery();
                return;
            }
            RunReport report = LoadLatestReport();
            if (report == null || !string.IsNullOrWhiteSpace(report.CompletedUtc)) return;

            try
            {
                string sourceDrift = TryCreateRecoveryPlans(
                    report,
                    () => CreatePlansForKeys(GetRecoveryProfileKeys(report)),
                    out var plans);
                foreach (var record in report.Profiles.Where(record => record.Status == "准备"))
                {
                    record.Status = "等待";
                    record.Message = "主 Unity 重载后恢复到等待队列。";
                }
                var pending = new Queue<ProfilePlan>(report.Profiles
                    .Where(record => record.Status == "等待")
                    .Select(record => plans.TryGetValue(record.Key, out var plan) ? plan : null)
                    .Where(plan => plan != null));
                _activeRun = new ActiveRun
                {
                    ProjectDirectory = Path.Combine(report.RunDirectory, "Project"),
                    RunDirectory = report.RunDirectory,
                    PreviousLatestRunDirectory = ReadPreviousLatestRunDirectory(),
                    Pending = pending,
                    Report = report,
                };
                EditorApplication.update -= PollChildProcess;
                EditorApplication.update += PollChildProcess;

                ProfileRecord building = report.Profiles.FirstOrDefault(record => record.Status == "构建中");
                if (!string.IsNullOrEmpty(sourceDrift))
                {
                    string driftStopReason =
                        "检测到证据输入漂移；当前组合完成后停止：" + sourceDrift;
                    ChildProcessAttachResult driftAttach = building == null
                        ? ChildProcessAttachResult.ConfirmedNotOwned
                        : TryAttachUnityProcess(
                            building.ChildProcessId,
                            building.ChildProcessStartUtcTicks,
                            out _childProcess);
                    if (building != null && driftAttach == ChildProcessAttachResult.Attached)
                    {
                        _activeProfile = plans.TryGetValue(building.Key, out ProfilePlan currentPlan)
                            ? currentPlan
                            : new ProfilePlan { Key = building.Key, Title = building.Title };
                        report.StopAfterCurrentReason = driftStopReason;
                        building.Message = "检测到证据输入漂移；已重新附着当前子进程，完成后停止：" + sourceDrift;
                        foreach (ProfileRecord waiting in report.Profiles.Where(record => record.Status == "等待"))
                            waiting.Message = report.StopAfterCurrentReason;
                        WriteReports(report);
                        NotifyChanged();
                        return;
                    }

                    if (building != null && TryApplyCompletedChildResultDuringDrift(
                            report, building, driftStopReason))
                    {
                        CompleteRun(driftStopReason);
                        return;
                    }

                    if (building != null)
                    {
                        building.Status = "失败";
                        building.Errors = Math.Max(building.Errors, 1);
                        building.ChildProcessId = 0;
                        building.ChildProcessStartUtcTicks = 0;
                        building.Message = sourceDrift;
                    }
                    else
                    {
                        ProfileRecord invalidPending = report.Profiles.FirstOrDefault(
                            record => record.Status == "等待");
                        if (invalidPending != null)
                        {
                            invalidPending.Status = "失败";
                            invalidPending.Errors = Math.Max(invalidPending.Errors, 1);
                            invalidPending.Message = sourceDrift;
                        }
                    }
                    Debug.LogError("[BuildSizeProbe] " + sourceDrift);
                    CompleteRun(sourceDrift);
                    return;
                }
                if (building == null)
                {
                    WriteReports(report);
                    StartNextProfile();
                    return;
                }
                if (!plans.TryGetValue(building.Key, out _activeProfile))
                    throw new InvalidOperationException("恢复时找不到构建组合：" + building.Key);

                ChildProcessAttachResult attach = TryAttachUnityProcess(
                    building.ChildProcessId,
                    building.ChildProcessStartUtcTicks,
                    out _childProcess);
                if (attach == ChildProcessAttachResult.Attached)
                {
                    building.Message = "主 Unity 重载后已重新附着正在运行的隔离构建子进程。";
                    WriteReports(report);
                    NotifyChanged();
                    return;
                }

                bool unknownChildOwner =
                    attach == ChildProcessAttachResult.UnknownInspectionFailure ||
                    MustStopRecoveryForUnknownChild(building);
                bool hasResultFile = File.Exists(building.ResultPath);
                string unknownOwnerStopReason = unknownChildOwner
                    ? CreateUnknownChildOwnerStopReason(hasResultFile)
                    : null;
                if (hasResultFile)
                {
                    building.ChildProcessId = 0;
                    building.ChildProcessStartUtcTicks = 0;
                    ApplyChildResult(building, building.ExitCode);
                }
                else
                {
                    building.Status = "失败";
                    building.Errors = Math.Max(building.Errors, 1);
                    building.ChildProcessId = 0;
                    building.ChildProcessStartUtcTicks = 0;
                    building.Message = unknownChildOwner
                        ? unknownOwnerStopReason
                        : "主 Unity 重载后没有找到原子进程或结果文件；未猜测成功，继续后续组合。";
                }
                if (unknownChildOwner)
                {
                    report.StopAfterCurrentReason = unknownOwnerStopReason;
                    CompleteRun(unknownOwnerStopReason);
                    return;
                }
                WriteReports(report);
                StartNextProfile();
            }
            catch (Exception ex)
            {
                if (HasRunningChildProcess())
                {
                    ProfileRecord building = _activeRun?.Report?.Profiles?
                        .FirstOrDefault(record => record.Status == "构建中");
                    StopAfterCurrentForOwnerFailure(
                        building,
                        ex,
                        "恢复主进程所有权状态");
                    EditorApplication.update -= PollChildProcess;
                    EditorApplication.update += PollChildProcess;
                    return;
                }

                Debug.LogException(ex);
                ClearActiveRunState();
            }
        }

        private static bool HasRunningChildProcess()
        {
            if (_childProcess == null) return false;
            try
            {
                return !_childProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// “构建中但 PID 未提交”可能意味着 child 已启动、主进程只来不及持久化 owner journal。
        /// 没有独立结果前不能据此启动下一档；宁可停止矩阵，也不能让两个 Unity 写同一隔离工程。
        /// </summary>
        internal static bool MustStopRecoveryForUnknownChild(ProfileRecord building) =>
            building != null &&
            string.Equals(building.Status, "构建中", StringComparison.Ordinal) &&
            (building.ChildProcessId <= 0 || building.ChildProcessStartUtcTicks <= 0) &&
            (string.IsNullOrWhiteSpace(building.ResultPath) || !File.Exists(building.ResultPath));

        internal static string CreateUnknownChildOwnerStopReason(bool hasResultFile) =>
            "主 Unity 无法确认原 child 已终止（PID / 启动时间缺失或进程检查失败）" +
            (hasResultFile
                ? "；已接收原子结果，但仍不能证明旧进程不会继续写隔离工程。"
                : "，且尚无结果文件。") +
            "为避免两个 Unity 并发写同一隔离工程，本轮停止后续组合。";

        /// <summary>
        /// 漂移只禁止启动后续档位；已由冻结输入启动并原子落盘的当前 child 结果仍属于本轮证据。
        /// Domain Reload 恰好发生在 child 退出之后时，应消费结果再停止，不能因 PID 已消失误判失败。
        /// </summary>
        internal static bool TryApplyCompletedChildResultDuringDrift(
            RunReport report,
            ProfileRecord building,
            string driftStopReason)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (building == null || string.IsNullOrWhiteSpace(building.ResultPath) ||
                !File.Exists(building.ResultPath))
                return false;
            building.ChildProcessId = 0;
            building.ChildProcessStartUtcTicks = 0;
            ApplyChildResult(building, building.ExitCode);
            report.StopAfterCurrentReason = driftStopReason;
            return true;
        }

        /// <summary>
        /// 恢复期间无法重建当前拓扑本身也是输入漂移，而不是放弃 owner 的理由。调用方仍可使用
        /// 落盘 PID 附着已启动 child，并以占位 Profile 完成它；没有 child 时再明确失败并收口报告。
        /// </summary>
        internal static string TryCreateRecoveryPlans(
            RunReport report,
            Func<ProfilePlan[]> createPlans,
            out Dictionary<string, ProfilePlan> plans)
        {
            if (createPlans == null) throw new ArgumentNullException(nameof(createPlans));
            try
            {
                plans = (createPlans() ?? Array.Empty<ProfilePlan>())
                    .Where(plan => plan != null && !string.IsNullOrWhiteSpace(plan.Key))
                    .ToDictionary(plan => plan.Key, StringComparer.Ordinal);
                return FindRecoveryDrift(report, plans);
            }
            catch (Exception exception)
            {
                plans = new Dictionary<string, ProfilePlan>(StringComparer.Ordinal);
                return "恢复时无法重建当前 Module / Package 拓扑；将完成已启动档位后停止，" +
                       "拒绝让子进程失去 owner：" + exception.Message;
            }
        }

        /// <summary>
        /// Domain Reload 只重建本轮报告实际记录的组合。完整 Module 假设矩阵可能包含数十个档位；
        /// 为恢复三四个已选档位重新计算全部源码/Package 指纹只会放大主线程停顿，并不增加证据强度。
        /// </summary>
        internal static string[] GetRecoveryProfileKeys(RunReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            return (report.Profiles ?? Array.Empty<ProfileRecord>())
                .Where(record => record != null && !string.IsNullOrWhiteSpace(record.Key))
                .Select(record => record.Key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static ChildProcessAttachResult TryAttachUnityProcess(
            int processId,
            long processStartUtcTicks,
            out Process process) =>
            TryAttachUnityProcess(
                processId,
                processStartUtcTicks,
                Process.GetProcessById,
                Process.GetCurrentProcess().Id,
                out process);

        internal static ChildProcessAttachResult TryAttachUnityProcess(
            int processId,
            long processStartUtcTicks,
            Func<int, Process> processResolver,
            int ownerProcessId,
            out Process process)
        {
            process = null;
            if (processResolver == null) throw new ArgumentNullException(nameof(processResolver));
            if (processId <= 0 || processStartUtcTicks <= 0)
                return ChildProcessAttachResult.UnknownInspectionFailure;
            Process candidate = null;
            try
            {
                candidate = processResolver(processId);
                long candidateStartUtcTicks = candidate.StartTime.ToUniversalTime().Ticks;
                if (candidate.HasExited || !MatchesChildProcessIdentity(
                        processId,
                        processStartUtcTicks,
                        candidate.Id,
                        candidateStartUtcTicks,
                        candidate.ProcessName,
                        ownerProcessId))
                {
                    candidate.Dispose();
                    return ChildProcessAttachResult.ConfirmedNotOwned;
                }
                process = candidate;
                return ChildProcessAttachResult.Attached;
            }
            catch (ArgumentException)
            {
                candidate?.Dispose();
                return ChildProcessAttachResult.ConfirmedNotOwned;
            }
            catch
            {
                candidate?.Dispose();
                return ChildProcessAttachResult.UnknownInspectionFailure;
            }
        }

        internal static bool MatchesChildProcessIdentity(
            int expectedProcessId,
            long expectedStartUtcTicks,
            int candidateProcessId,
            long candidateStartUtcTicks,
            string candidateProcessName,
            int ownerProcessId) =>
            expectedProcessId > 0 &&
            expectedStartUtcTicks > 0 &&
            candidateProcessId == expectedProcessId &&
            candidateProcessId != ownerProcessId &&
            candidateStartUtcTicks == expectedStartUtcTicks &&
            !string.IsNullOrWhiteSpace(candidateProcessName) &&
            candidateProcessName.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void PrepareProfileSources(
            string projectDirectory,
            ProfilePlan profile,
            RunReport report)
        {
            ValidateFrozenEvidenceImplementation(report);
            ValidateFrozenProfileInputs(profile);
            ResetDerivedProjectState(projectDirectory);

            string childTemplateContent = ReadFrozenChildTemplate(
                FrozenChildTemplatePath(projectDirectory),
                report?.ChildTemplateFingerprint);
            string childTemplateDestination = Path.Combine(
                projectDirectory, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs");
            File.WriteAllText(childTemplateDestination, childTemplateContent, new UTF8Encoding(false));
            ValidateFrozenCopy(
                "子进程模板快照",
                report?.ChildTemplateFingerprint,
                () => ComputeTextFingerprint(File.ReadAllText(childTemplateDestination, Encoding.UTF8)));

            if (string.IsNullOrWhiteSpace(profile.MinimalManifest))
                throw new InvalidDataException(
                    $"构建组合 {profile.Key} 缺少启动时冻结的最小 manifest，拒绝在运行中重新猜依赖版本。");
            File.WriteAllText(
                Path.Combine(projectDirectory, "Packages", "manifest.json"),
                profile.MinimalManifest,
                new UTF8Encoding(false));

            string packagesDirectory = Path.Combine(projectDirectory, "Packages");
            FrameworkProjectPath.PhysicalTreeSnapshot packagesTree =
                FrameworkProjectPath.CapturePhysicalTree(packagesDirectory);
            foreach (string directory in packagesTree.Directories.Where(path =>
                         FrameworkProjectPath.PathsEqual(
                             Path.GetDirectoryName(path) ?? string.Empty, packagesDirectory)))
                DeleteDirectoryInsideWorkspace(directory, projectDirectory);
            string packageLock = Path.Combine(packagesDirectory, "packages-lock.json");
            if (File.Exists(packageLock)) File.Delete(packageLock);
            foreach (PackageSourcePlan package in profile.CopiedPackages ?? Array.Empty<PackageSourcePlan>())
            {
                if (package == null || string.IsNullOrWhiteSpace(package.PhysicalDirectory) ||
                    !Directory.Exists(package.PhysicalDirectory))
                    throw new DirectoryNotFoundException(
                        $"找不到 {package?.PackageName ?? "未知复制 Package"} 的物理源码目录：" +
                        (package?.PhysicalDirectory ?? "（空）"));
                string destination = Path.Combine(
                    packagesDirectory, SafeDirectoryName(package.PackageName));
                CopyDirectory(
                    Path.GetFullPath(package.PhysicalDirectory), destination, projectDirectory, _ => false);
                ValidateFrozenCopy(
                    "复制 Package " + package.PackageName,
                    package.SourceFingerprint,
                    () => ComputeCopiedPackageSourceFingerprint(destination));
            }

            string frameworkDestination = Path.Combine(projectDirectory, "Assets", "Framework");
            DeleteDirectoryInsideWorkspace(frameworkDestination, projectDirectory);
            Directory.CreateDirectory(frameworkDestination);

            foreach (ModuleSourcePlan module in profile.Sources)
            {
                if (module == null || string.IsNullOrWhiteSpace(module.PhysicalDirectory) ||
                    !Directory.Exists(module.PhysicalDirectory))
                    throw new DirectoryNotFoundException(
                        $"找不到 {module?.AssemblyName ?? "未知 Module"} 的物理源码目录：" +
                        (module?.PhysicalDirectory ?? "（空）"));
                string source = Path.GetFullPath(module.PhysicalDirectory);
                string destination = Path.Combine(
                    frameworkDestination,
                    ModuleDestinationDirectoryName(module));
                CopyDirectory(source, destination, projectDirectory, ShouldSkipModulePath);
                ValidateFrozenCopy(
                    "Module " + module.AssemblyName,
                    module.SourceFingerprint,
                    () => ComputeModuleSourceFingerprint(destination));
            }

            string rootDirectory = Path.Combine(projectDirectory, "Assets", "ProbeRoot");
            Directory.CreateDirectory(rootDirectory);
            File.WriteAllText(Path.Combine(rootDirectory, "link.xml"), CreateLinkXml(profile.Assemblies),
                new UTF8Encoding(false));
        }

        /// <summary>
        /// 每档复制前重新读取真实来源。计划只冻结身份字符串而不复核内容，会让分钟级矩阵把旧 SHA
        /// 与构建期间被用户或 Agent 改过的新源码组合到同一报告。
        /// </summary>
        internal static string FindFrozenProfileInputDrift(ProfilePlan profile)
        {
            if (profile == null) return "构建组合为空，无法验证冻结输入。";
            foreach (ModuleSourcePlan module in profile.Sources ?? Array.Empty<ModuleSourcePlan>())
            {
                if (module == null || string.IsNullOrWhiteSpace(module.PhysicalDirectory) ||
                    !Directory.Exists(module.PhysicalDirectory))
                    return $"Module {module?.AssemblyName ?? "未知"} 的冻结源码目录已不存在。";
                string actual = ComputeModuleSourceFingerprint(module.PhysicalDirectory);
                if (!string.Equals(actual, module.SourceFingerprint, StringComparison.Ordinal))
                    return $"Module {module.AssemblyName} 的源码已在本轮启动后变化；" +
                           $"冻结 SHA-256={module.SourceFingerprint ?? "（空）"}，当前={actual}。";
            }

            foreach (PackageSourcePlan package in profile.CopiedPackages ?? Array.Empty<PackageSourcePlan>())
            {
                if (package == null || string.IsNullOrWhiteSpace(package.PhysicalDirectory) ||
                    !Directory.Exists(package.PhysicalDirectory))
                    return $"复制 Package {package?.PackageName ?? "未知"} 的冻结源码目录已不存在。";
                string actual = ComputeCopiedPackageSourceFingerprint(package.PhysicalDirectory);
                if (!string.Equals(actual, package.SourceFingerprint, StringComparison.Ordinal))
                    return $"复制 Package {package.PackageName} 的源码已在本轮启动后变化；" +
                           $"冻结 SHA-256={package.SourceFingerprint ?? "（空）"}，当前={actual}。";
            }

            return string.Empty;
        }

        private static void ValidateFrozenProfileInputs(ProfilePlan profile)
        {
            string drift;
            try
            {
                drift = FindFrozenProfileInputDrift(profile);
            }
            catch (Exception exception)
            {
                throw new FrozenInputDriftException(
                    "无法重新读取本轮冻结输入，可能正在被外部写入：" + exception.Message);
            }
            if (!string.IsNullOrEmpty(drift))
                throw new FrozenInputDriftException(
                    drift + " 已拒绝复制并终止剩余档位，避免一份体积矩阵混入多个源码版本。");
        }

        private static void ValidateFrozenCopy(
            string label,
            string expected,
            Func<string> computeActual)
        {
            try
            {
                EnsureFrozenFingerprint(label, expected, computeActual());
            }
            catch (FrozenInputDriftException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FrozenInputDriftException(
                    $"{label} 复制后无法生成内容指纹，可能在复制过程中发生写入：{exception.Message}");
            }
        }

        private static void EnsureFrozenFingerprint(string label, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
            throw new FrozenInputDriftException(
                $"{label} 的实际复制内容与启动时冻结指纹不一致；" +
                $"冻结 SHA-256={expected ?? "（空）"}，复制后={actual ?? "（空）"}。" +
                "可能在复制过程中发生写入。");
        }

        /// <summary>
        /// 每个 Profile 都从新的 Unity 导入 / 编译状态开始，使物理删除证据不依赖上一档留下的
        /// AssetDatabase / Bee 缓存如何解释同路径、同时间戳的替换输入。
        /// 只删除隔离子工程中的派生目录；Assets、Packages、ProjectSettings 与报告 / 输出均保留。
        /// </summary>
        internal static void ResetDerivedProjectState(string projectDirectory)
        {
            ValidateDerivedProjectWorkspace(projectDirectory);
            string workspace = Path.GetFullPath(projectDirectory);
            foreach (string directoryName in new[] { "Library", "Temp", "obj" })
                DeleteDirectoryInsideWorkspace(Path.Combine(workspace, directoryName), workspace);
        }

        /// <summary>
        /// 递归删除前锁定探针专属工作区。仅“目标在调用方传入 workspace 内”不足以保护主工程，
        /// 因为误把项目根当 workspace 时，主 <c>Library</c> 也会通过该相对检查。
        /// </summary>
        internal static void ValidateDerivedProjectWorkspace(string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectDirectory))
                throw new ArgumentException("隔离工程目录不能为空。", nameof(projectDirectory));

            string workspace = Path.GetFullPath(projectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string runsRoot = FullPath(RunsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryInfo runDirectory = Directory.GetParent(workspace);
            DirectoryInfo parent = runDirectory?.Parent;
            bool exactRunsParent = parent != null &&
                                   FrameworkModuleSourceCatalog.IsPhysicalPathInside(parent.FullName, runsRoot) &&
                                   FrameworkModuleSourceCatalog.IsPhysicalPathInside(runsRoot, parent.FullName);
            if (!Path.GetFileName(workspace).Equals("Project", StringComparison.OrdinalIgnoreCase) ||
                runDirectory == null || !exactRunsParent)
                throw new InvalidOperationException(
                    "拒绝清理非探针工作区：路径必须严格匹配 Library/SSFramework/BuildSizeProbe/<run>/Project。" +
                    workspace);

            string projectVersion = Path.Combine(workspace, "ProjectSettings", "ProjectVersion.txt");
            string childTemplate = Path.Combine(
                workspace, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs");
            if (!File.Exists(projectVersion) || !File.Exists(childTemplate))
                throw new InvalidOperationException(
                    "拒绝清理缺少探针标记文件的工作区：" + workspace);
        }

        private static void WriteReports(RunReport report)
        {
            NormalizeShippingEvidence(report);
            Directory.CreateDirectory(report.RunDirectory);
            string json = JsonUtility.ToJson(report, true);
            string markdown = CreateMarkdownReport(report);

            // JSON 是 Domain Reload 恢复的提交标记，最后发布。两个文件都先在同目录完整写入临时文件，
            // 单个替换失败时旧报告仍保持可解析，不会留下被截断的 JSON 冒充最新状态。
            WriteTextAtomically(Path.Combine(report.RunDirectory, "report.md"), markdown);
            WriteTextAtomically(Path.Combine(report.RunDirectory, "report.json"), json);
        }

        internal static void WriteTextAtomically(
            string path,
            string content,
            Action<string, string> writeTemporary = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("报告路径不能为空。", nameof(path));
            string destination = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(destination) ??
                            throw new InvalidOperationException("无法解析报告父目录：" + destination);
            Directory.CreateDirectory(parent);
            string temporary = destination + ".ssframework-write-" + Guid.NewGuid().ToString("N");
            try
            {
                (writeTemporary ?? ((file, text) =>
                    File.WriteAllText(file, text ?? string.Empty, new UTF8Encoding(false))))(
                    temporary, content ?? string.Empty);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static string CreateMarkdownReport(RunReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var sb = new StringBuilder(4096);
            sb.AppendLine("# SSFramework 真实构建体积证据");
            sb.AppendLine();
            sb.AppendLine($"- Unity：{report.UnityVersion}");
            sb.AppendLine($"- 目标：{report.Target} / {report.ScriptingBackend} / stripping {report.StrippingLevel}");
            sb.AppendLine($"- 证据口径：{report.EvidenceScope}");
            if (!string.IsNullOrWhiteSpace(report.EvidenceImplementationFingerprint))
                sb.AppendLine($"- 证据实现 SHA-256：`{report.EvidenceImplementationFingerprint}`");
            if (!string.IsNullOrWhiteSpace(report.ChildTemplateFingerprint))
                sb.AppendLine($"- 子进程模板快照 SHA-256：`{report.ChildTemplateFingerprint}`");
            sb.AppendLine();
            sb.AppendLine("| 组合 | 状态 | 可发布输出 | BuildReport 总量 | 非发布构建证据 | 相对 Core | 用时 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            ProfileRecord core = report.Profiles.FirstOrDefault(record =>
                record.Key == "core" && record.Status == "成功");
            foreach (var record in report.Profiles)
            {
                string delta = core == null || record.Status != "成功"
                    ? "—"
                    : FormatSignedBytes(record.OutputBytes - core.OutputBytes);
                long nonShipping = Math.Max(0L, record.RawOutputBytes - record.OutputBytes);
                sb.AppendLine($"| {record.Title} | {record.Status} | {FormatBytes(record.OutputBytes)} | " +
                              $"{FormatBytes(record.BuildReportBytes)} | {FormatBytes(nonShipping)} | " +
                              $"{delta} | {record.DurationSeconds:F1}s |");
            }
            sb.AppendLine();
            sb.AppendLine("## 解释");
            sb.AppendLine();
            sb.AppendLine("- 每个组合在隔离空工程中构建，未选 Module 的源码、link.xml、业务场景和 HybridCLR 生成物都不在工程内。");
            sb.AppendLine("- 所选程序集完整保留，因此差值是可重复的体积上界；真实游戏只使用部分类型时通常会更小。");
            sb.AppendLine("- 可发布输出排除 Unity 明确标记为 BackUp/DoNotShip 的 IL2CPP 中间目录与调试符号；BuildReport 总量保留作诊断。 ");
            sb.AppendLine("- 不同 Unity、目标平台、脚本后端、裁剪级别或依赖版本之间不能直接横向比较。");
            foreach (var record in report.Profiles.Where(record => record.Status != "等待"))
            {
                sb.AppendLine();
                sb.AppendLine("## " + record.Title);
                sb.AppendLine();
                sb.AppendLine(record.Message ?? string.Empty);
                if (record.Assemblies?.Length > 0)
                    sb.AppendLine("\nModule：" + string.Join(", ", record.Assemblies));
                if (record.Sources?.Length > 0)
                {
                    sb.AppendLine("\n源码证据：");
                    foreach (ModuleSourcePlan source in record.Sources)
                    {
                        string owner = SourceOwner(source);
                        sb.AppendLine($"- {source.AssemblyName} ← {source.AssetDirectory} ({owner})");
                        if (!string.IsNullOrWhiteSpace(source.SourceFingerprint))
                            sb.AppendLine($"  - 实际复制内容 SHA-256：`{source.SourceFingerprint}`");
                    }
                }
                if (record.ManifestPackages?.Length > 0)
                    sb.AppendLine("\nmanifest Package：" + string.Join(", ", record.ManifestPackages));
                if (!string.IsNullOrWhiteSpace(record.ManifestFingerprint))
                    sb.AppendLine($"- 冻结 manifest SHA-256：`{record.ManifestFingerprint}`");
                if (record.CopiedPackages?.Length > 0)
                {
                    sb.AppendLine("\n复制 Package 证据：");
                    foreach (PackageSourcePlan package in record.CopiedPackages)
                    {
                        sb.AppendLine($"- {package.PackageName} ← {package.AssetDirectory} ({PackageSourceOwner(package)})");
                        if (!string.IsNullOrWhiteSpace(package.SourceFingerprint))
                            sb.AppendLine($"  - 实际复制内容 SHA-256：`{package.SourceFingerprint}`");
                    }
                }
                if (record.LargestFiles?.Length > 0)
                {
                    sb.AppendLine("\n较大的输出文件：");
                    foreach (var file in record.LargestFiles)
                        sb.AppendLine($"- {file.Path}：{FormatBytes(file.Bytes)} ({file.Role})");
                }
            }
            return sb.ToString();
        }

        internal static string FormatBytes(long bytes)
        {
            double value = Math.Abs((double)bytes);
            string sign = bytes < 0 ? "-" : string.Empty;
            if (value >= 1024d * 1024d * 1024d) return $"{sign}{value / (1024d * 1024d * 1024d):F2} GiB";
            if (value >= 1024d * 1024d) return $"{sign}{value / (1024d * 1024d):F2} MiB";
            if (value >= 1024d) return $"{sign}{value / 1024d:F1} KiB";
            return sign + value.ToString("F0") + " B";
        }

        private static string FormatSignedBytes(long bytes) =>
            bytes > 0 ? "+" + FormatBytes(bytes) : FormatBytes(bytes);

        internal static bool IsShippingOutputPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string normalized = relativePath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            if (segments.Any(segment =>
                    segment.IndexOf("BackUpThisFolder_ButDontShipItWithYourGame",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    segment.IndexOf("DoNotShip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    segment.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)))
                return false;
            return !normalized.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith(".symbols.zip", StringComparison.OrdinalIgnoreCase);
        }

        private static void NormalizeShippingEvidence(RunReport report)
        {
            if (report?.Profiles == null) return;
            foreach (var record in report.Profiles) NormalizeShippingEvidence(record);
        }

        private static void NormalizeShippingEvidence(ProfileRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.OutputPath) ||
                !Directory.Exists(record.OutputPath)) return;
            IReadOnlyList<string> files = FrameworkProjectPath
                .CapturePhysicalTree(Path.GetFullPath(record.OutputPath))
                .Files;
            record.RawOutputBytes = files.Sum(path => new FileInfo(path).Length);
            var shipping = files.Select(path => new
                {
                    Path = path,
                    Relative = RelativePath(record.OutputPath, path),
                    Bytes = new FileInfo(path).Length,
                })
                .Where(file => IsShippingOutputPath(file.Relative))
                .ToArray();
            record.OutputBytes = shipping.Sum(file => file.Bytes);
            record.LargestFiles = shipping.OrderByDescending(file => file.Bytes)
                .Take(10)
                .Select(file => new OutputFileRecord
                {
                    Path = file.Relative,
                    Role = string.IsNullOrEmpty(Path.GetExtension(file.Path))
                        ? "data"
                        : Path.GetExtension(file.Path).TrimStart('.'),
                    Bytes = file.Bytes,
                }).ToArray();
        }

        private static Dictionary<string, string> ReadDependencyEntries(
            string manifest,
            bool requireDependencies = true)
        {
            int dependencies = manifest.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            if (dependencies < 0)
            {
                if (!requireDependencies) return new Dictionary<string, string>(StringComparer.Ordinal);
                throw new InvalidDataException("Packages/manifest.json 缺少 dependencies。");
            }
            int openBrace = manifest.IndexOf('{', dependencies);
            int closeBrace = manifest.IndexOf('}', openBrace + 1);
            if (openBrace < 0 || closeBrace < 0)
                throw new InvalidDataException("Packages/manifest.json 的 dependencies 结构无法解析。");
            string block = manifest.Substring(openBrace + 1, closeBrace - openBrace - 1);
            return DependencyEntryRegex.Matches(block).Cast<Match>().ToDictionary(
                match => match.Groups["id"].Value,
                match => match.Groups["value"].Value,
                StringComparer.Ordinal);
        }

        private static string ExtractJsonArrayProperty(string json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName)) return string.Empty;
            int property = json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal);
            if (property < 0) return string.Empty;
            int colon = json.IndexOf(':', property + propertyName.Length + 2);
            int open = colon < 0 ? -1 : json.IndexOf('[', colon + 1);
            if (open < 0) throw new InvalidDataException($"Packages/manifest.json 的 {propertyName} 不是数组。");

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = open; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0)
                    return json.Substring(open, i - open + 1);
            }

            throw new InvalidDataException($"Packages/manifest.json 的 {propertyName} 数组没有闭合。");
        }

        private static void CopyDirectory(
            string source,
            string destination,
            string destinationBoundary,
            Func<string, bool> skip)
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("找不到隔离探针依赖目录：" + source);
            if (skip == null) throw new ArgumentNullException(nameof(skip));
            if (!FrameworkProjectPath.TryValidatePhysicalPath(
                    destinationBoundary, destination, out string destinationError))
                throw new InvalidOperationException(destinationError);

            FrameworkProjectPath.PhysicalTreeSnapshot sourceTree =
                FrameworkProjectPath.CapturePhysicalTree(Path.GetFullPath(source));
            Directory.CreateDirectory(destination);
            if (!FrameworkProjectPath.TryValidatePhysicalPath(
                    destinationBoundary, destination, out destinationError))
                throw new InvalidOperationException(destinationError);
            foreach (string directory in sourceTree.Directories)
            {
                string relative = RelativePath(source, directory);
                if (skip(relative)) continue;
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in sourceTree.Files)
            {
                string relative = RelativePath(source, file);
                if (skip(relative)) continue;
                string destinationFile = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destination);
                File.Copy(ExtendedLengthPath(file), ExtendedLengthPath(destinationFile), true);
            }
        }

        private static string RelativePath(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            var rootUri = new Uri(normalizedRoot);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static void DeleteDirectoryInsideWorkspace(string directory, string workspace)
        {
            FrameworkProjectPath.DeleteDirectoryWithinBoundary(directory, workspace);
        }

        private static ProfileRecord FindRecord(string key) =>
            _activeRun.Report.Profiles.First(record => record.Key == key);

        internal static void SanitizeChildEnvironment(System.Diagnostics.ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));

            // HybridCLR 会在主 Unity 进程中写入进程级 UNITY_IL2CPP_PATH。Process.Start 默认继承该值，
            // 但隔离工程刻意不安装 HybridCLR；若不移除，连普通脚本编译也会错误访问主工程的本地
            // libil2cpp，最终只留下误导性的“executeMethod class could not be found”。
            startInfo.EnvironmentVariables.Remove(UnityIl2CppPathEnvironmentVariable);
        }

        internal static string ReadDiagnosticLogExcerpt(string path, int lines)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || lines <= 0) return string.Empty;
            try
            {
                var signals = new List<string>(lines);
                var tail = new Queue<string>(lines);
                foreach (string line in File.ReadLines(path))
                {
                    if (signals.Count < lines && IsDiagnosticLogLine(line)) signals.Add(line);
                    if (tail.Count == lines) tail.Dequeue();
                    tail.Enqueue(line);
                }
                return signals.Count > 0
                    ? string.Join("\n", signals)
                    : string.Join("\n", tail);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsDiagnosticLogLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            return line.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   Regex.IsMatch(line, @"\berror\s+CS\d+\b", RegexOptions.IgnoreCase) ||
                   line.IndexOf("Compilation failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("executeMethod class", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("BuildFailedException", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string QuoteArgument(string value) =>
            string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"") + "\"";

        private static string FullPath(string projectRelativePath) =>
            Path.GetFullPath(Path.Combine(ProjectRoot, projectRelativePath));

        private static FrameworkModuleSourceCatalog.SourceLocation ResolveChildTemplate() =>
            FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                ChildTemplateFileName, FrameworkModuleAudit.CoreAssemblyName + ".Editor");

        private static string ReadCurrentChildTemplate()
        {
            string path = ResolveChildTemplate().PhysicalPath;
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到隔离构建子进程模板。", path);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        internal static string FrozenChildTemplatePath(string projectDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectDirectory))
                throw new ArgumentException("隔离工程目录不能为空。", nameof(projectDirectory));
            DirectoryInfo runDirectory = Directory.GetParent(Path.GetFullPath(projectDirectory));
            if (runDirectory == null)
                throw new InvalidDataException("无法从隔离工程目录解析运行目录：" + projectDirectory);
            return Path.Combine(runDirectory.FullName, FrozenInputsDirectoryName, ChildTemplateFileName);
        }

        internal static string ComputeChildTemplateFingerprint(string childTemplateContent) =>
            ComputeTextFingerprint(childTemplateContent);

        /// <summary>
        /// 把实际执行中的 Editor DLL、对应主探针源码与子进程模板绑定为一个证据实现身份。
        /// 只记录源码会漏掉“文件已写入但 Unity 尚未编译”的窗口，只记录 DLL 又无法识别模板变化。
        /// </summary>
        internal static string ComputeEvidenceImplementationFingerprint(string childTemplateContent)
        {
            string assemblyPath = typeof(FrameworkBuildSizeProbe).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                throw new FileNotFoundException("找不到当前已编译的构建探针 Editor 程序集。", assemblyPath);
            var source = FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                nameof(FrameworkBuildSizeProbe) + ".cs",
                FrameworkModuleAudit.CoreAssemblyName + ".Editor");
            if (!File.Exists(source.PhysicalPath))
                throw new FileNotFoundException("找不到构建探针主实现源码。", source.PhysicalPath);

            return ComputeTextFingerprint(string.Join("|",
                ComputeFileFingerprint(assemblyPath),
                ComputeFileFingerprint(source.PhysicalPath),
                ComputeTextFingerprint(childTemplateContent)));
        }

        internal static string FindFrozenChildTemplateDrift(
            string frozenTemplatePath,
            string expectedFingerprint)
        {
            if (string.IsNullOrWhiteSpace(expectedFingerprint))
                return "报告缺少子进程模板 SHA-256，无法验证启动快照。";
            if (string.IsNullOrWhiteSpace(frozenTemplatePath) || !File.Exists(frozenTemplatePath))
                return "启动时冻结的子进程模板快照已不存在：" +
                       (frozenTemplatePath ?? "（空）");
            string actual = ComputeTextFingerprint(File.ReadAllText(frozenTemplatePath, Encoding.UTF8));
            return string.Equals(actual, expectedFingerprint, StringComparison.Ordinal)
                ? string.Empty
                : $"子进程模板启动快照已变化；冻结 SHA-256={expectedFingerprint}，当前={actual}。";
        }

        private static string ReadFrozenChildTemplate(
            string frozenTemplatePath,
            string expectedFingerprint)
        {
            try
            {
                string drift = FindFrozenChildTemplateDrift(frozenTemplatePath, expectedFingerprint);
                if (!string.IsNullOrEmpty(drift))
                    throw new FrozenInputDriftException(
                        drift + " 已终止剩余档位，避免一份体积矩阵混入多个子进程实现。 ");
                return File.ReadAllText(frozenTemplatePath, Encoding.UTF8);
            }
            catch (FrozenInputDriftException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FrozenInputDriftException(
                    "无法读取本轮冻结的子进程模板，可能正在被外部写入：" + exception.Message);
            }
        }

        private static void ValidateFrozenEvidenceImplementation(RunReport report)
        {
            if (report == null)
                throw new FrozenInputDriftException("当前运行报告为空，无法验证证据实现身份。 ");
            try
            {
                string actual = ComputeEvidenceImplementationFingerprint(ReadCurrentChildTemplate());
                if (string.Equals(
                        actual, report.EvidenceImplementationFingerprint, StringComparison.Ordinal))
                    return;
                throw new FrozenInputDriftException(
                    "构建探针的已编译 Editor 实现、主源码或子模板已在本轮启动后变化；" +
                    $"冻结 SHA-256={report.EvidenceImplementationFingerprint ?? "（空）"}，当前={actual}。" +
                    " 已终止剩余档位，避免新旧证据逻辑混写。 ");
            }
            catch (FrozenInputDriftException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FrozenInputDriftException(
                    "无法重新验证本轮证据实现身份：" + exception.Message);
            }
        }

        internal static void ValidateDisjointSourceDirectories(
            IEnumerable<ModuleSourcePlan> sourcePlans)
        {
            ModuleSourcePlan[] sources = sourcePlans?
                .Where(source => source != null && !string.IsNullOrWhiteSpace(source.PhysicalDirectory))
                .OrderBy(source => source.AssemblyName, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<ModuleSourcePlan>();
            for (int i = 0; i < sources.Length; i++)
            for (int j = i + 1; j < sources.Length; j++)
            {
                if (!FrameworkModuleSourceCatalog.IsPhysicalPathInside(
                        sources[i].PhysicalDirectory, sources[j].PhysicalDirectory) &&
                    !FrameworkModuleSourceCatalog.IsPhysicalPathInside(
                        sources[j].PhysicalDirectory, sources[i].PhysicalDirectory))
                    continue;
                throw new InvalidDataException(
                    "隔离构建要求每个 Runtime Module 拥有互不嵌套的源码目录；否则复制一个 Module 会夹带另一个：" +
                    $"{sources[i].AssemblyName} ({sources[i].AssetDirectory}) ↔ " +
                    $"{sources[j].AssemblyName} ({sources[j].AssetDirectory})");
            }
        }

        internal static string FindSourceIdentityMismatch(
            IEnumerable<ModuleSourcePlan> recorded,
            IEnumerable<ModuleSourcePlan> current)
        {
            string[] oldKeys = SourceIdentityKeys(recorded);
            string[] currentKeys = SourceIdentityKeys(current);
            return oldKeys.SequenceEqual(currentKeys, StringComparer.Ordinal)
                ? string.Empty
                : "Module 源码身份已变化；原报告为 [" + string.Join(", ", oldKeys) +
                  "]，当前为 [" + string.Join(", ", currentKeys) + "]。";
        }

        internal static string FindRecoveryDrift(
            RunReport report,
            IReadOnlyDictionary<string, ProfilePlan> currentPlans)
        {
            if (report == null) return "恢复报告为空，无法验证 Module 源码身份。";
            if (report.FormatVersion != CurrentReportFormatVersion)
                return report.FormatVersion < CurrentReportFormatVersion
                    ? $"报告格式早于 v{CurrentReportFormatVersion}，缺少冻结输入前后复核、子进程身份或 Player 编译图证据契约，" +
                      "拒绝跨 Domain Reload 猜测续跑。"
                    : $"报告格式 v{report.FormatVersion} 新于当前工具支持的 v{CurrentReportFormatVersion}；" +
                      "旧代码不能安全解释未知字段，拒绝续跑。";
            string currentEvidenceFingerprint;
            try
            {
                currentEvidenceFingerprint =
                    ComputeEvidenceImplementationFingerprint(ReadCurrentChildTemplate());
            }
            catch (Exception exception)
            {
                return "无法验证当前构建探针证据实现身份，拒绝续跑：" + exception.Message;
            }
            if (string.IsNullOrWhiteSpace(report.EvidenceImplementationFingerprint))
                return "报告缺少证据实现 SHA-256，拒绝跨 Domain Reload 猜测续跑。";
            if (string.IsNullOrWhiteSpace(report.ChildTemplateFingerprint))
                return "报告缺少子进程模板快照 SHA-256，拒绝跨 Domain Reload 猜测续跑。";
            if (!string.Equals(
                    report.EvidenceImplementationFingerprint,
                    currentEvidenceFingerprint,
                    StringComparison.Ordinal))
                return "构建探针的已编译 Editor 实现、主源码或子模板已变化；" +
                       $"原报告 SHA-256 为 {report.EvidenceImplementationFingerprint}，" +
                       $"当前为 {currentEvidenceFingerprint}。";
            if (currentPlans == null) return "当前 Module 拓扑为空，无法验证恢复报告。";

            var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProfileRecord record in report.Profiles ?? Array.Empty<ProfileRecord>())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Key))
                    return "恢复报告包含没有稳定 Key 的构建组合，拒绝猜测续跑。";
                if (!recordedKeys.Add(record.Key))
                    return $"恢复报告重复记录构建组合 {record.Key}，拒绝猜测续跑。";
                if (!currentPlans.TryGetValue(record.Key, out ProfilePlan current))
                    return $"构建组合 {record.Key} 已不在当前 Module 拓扑中，拒绝静默跳过。";

                string mismatch = FindSourceIdentityMismatch(record.Sources, current.Sources);
                if (!string.IsNullOrEmpty(mismatch)) return $"构建组合 {record.Key}：{mismatch}";
                string[] recordedManifest = (record.ManifestPackages ?? Array.Empty<string>())
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                string[] currentManifest = (current.ManifestPackages ?? Array.Empty<string>())
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                if (!recordedManifest.SequenceEqual(currentManifest, StringComparer.Ordinal))
                    return $"构建组合 {record.Key}：manifest Package 依赖已变化；原报告为 [" +
                           string.Join(", ", recordedManifest) + "]，当前为 [" +
                           string.Join(", ", currentManifest) + "]。";
                if (!string.Equals(
                        record.ManifestFingerprint, current.ManifestFingerprint, StringComparison.Ordinal))
                    return $"构建组合 {record.Key}：冻结 manifest 的版本规格、内置 Module 或 registry 已变化；" +
                           $"原报告 SHA-256 为 {record.ManifestFingerprint ?? "（空）"}，" +
                           $"当前为 {current.ManifestFingerprint ?? "（空）"}。";
                mismatch = FindPackageSourceIdentityMismatch(record.CopiedPackages, current.CopiedPackages);
                if (!string.IsNullOrEmpty(mismatch)) return $"构建组合 {record.Key}：{mismatch}";
            }

            return string.Empty;
        }

        private static string[] SourceIdentityKeys(IEnumerable<ModuleSourcePlan> sources) =>
            (sources ?? Array.Empty<ModuleSourcePlan>())
            .Where(source => source != null)
            .Select(source => string.Join("|",
                source.AssemblyName ?? string.Empty,
                NormalizeAssetPath(source.AssetDirectory),
                SourceOwner(source),
                source.SourceFingerprint ?? string.Empty))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        internal static string FindPackageSourceIdentityMismatch(
            IEnumerable<PackageSourcePlan> recorded,
            IEnumerable<PackageSourcePlan> current)
        {
            string[] oldKeys = PackageSourceIdentityKeys(recorded);
            string[] currentKeys = PackageSourceIdentityKeys(current);
            return oldKeys.SequenceEqual(currentKeys, StringComparer.Ordinal)
                ? string.Empty
                : "复制 Package 源码身份已变化；原报告为 [" + string.Join(", ", oldKeys) +
                  "]，当前为 [" + string.Join(", ", currentKeys) + "]。";
        }

        private static string[] PackageSourceIdentityKeys(IEnumerable<PackageSourcePlan> sources) =>
            (sources ?? Array.Empty<PackageSourcePlan>())
            .Where(source => source != null)
            .Select(source => string.Join("|",
                source.PackageName ?? string.Empty,
                NormalizeAssetPath(source.AssetDirectory),
                PackageSourceOwner(source),
                source.SourceFingerprint ?? string.Empty))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        internal static string ComputeModuleSourceFingerprint(string sourceDirectory)
            => ComputeDirectoryFingerprint(sourceDirectory, ShouldSkipModulePath);

        internal static string ComputeCopiedPackageSourceFingerprint(string sourceDirectory)
            => ComputeDirectoryFingerprint(sourceDirectory, _ => false);

        private static string ComputeFileFingerprint(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("无法为不存在的文件生成内容指纹。", path);
            using SHA256 sha256 = SHA256.Create();
            using var input = new FileStream(
                ExtendedLengthPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return BitConverter.ToString(sha256.ComputeHash(input))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private static string ComputeTextFingerprint(string value)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            return BitConverter.ToString(sha256.ComputeHash(bytes))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private static string ComputeDirectoryFingerprint(
            string sourceDirectory,
            Func<string, bool> skip)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException("无法为不存在的源码目录生成内容指纹：" +
                                                     (sourceDirectory ?? "（空）"));
            if (skip == null) throw new ArgumentNullException(nameof(skip));

            string root = Path.GetFullPath(sourceDirectory);
            string[] files = FrameworkProjectPath.CapturePhysicalTree(root).Files
                .Select(path => new
                {
                    PhysicalPath = path,
                    RelativePath = RelativePath(root, path).Replace('\\', '/'),
                })
                .Where(file => !skip(file.RelativePath))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => file.PhysicalPath)
                .ToArray();

            using var sha256 = SHA256.Create();
            using (var hashingStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(hashingStream, new UTF8Encoding(false), true))
            {
                foreach (string file in files)
                {
                    string relativePath = RelativePath(root, file).Replace('\\', '/');
                    using var input = new FileStream(
                        ExtendedLengthPath(file),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    writer.Write(relativePath);
                    writer.Write(input.Length);
                    writer.Flush();
                    input.CopyTo(hashingStream);
                }
                writer.Flush();
                hashingStream.FlushFinalBlock();
            }

            return BitConverter.ToString(sha256.Hash ?? Array.Empty<byte>())
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        /// <summary>
        /// Unity Editor 的部分 .NET 文件流入口在 Windows 仍受传统 MAX_PATH 影响，而 Package
        /// 复制目标会比主工程来源多出较长的 run id。目录枚举可能成功、随后打开同一文件却失败，
        /// 因此只在实际 IO 边界添加 Win32 extended-length 前缀，报告和相对路径保持可读。
        /// </summary>
        internal static string ExtendedLengthPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (Path.DirectorySeparatorChar != '\\' || full.StartsWith(@"\\?\", StringComparison.Ordinal))
                return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        private static string SourceOwner(ModuleSourcePlan source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.PackageName)) return "Assets";
            if (!string.IsNullOrWhiteSpace(source.PackageId)) return source.PackageId;
            return source.PackageName +
                   (string.IsNullOrWhiteSpace(source.PackageVersion)
                       ? string.Empty
                       : "@" + source.PackageVersion);
        }

        private static string PackageSourceOwner(PackageSourcePlan source)
        {
            if (source == null) return "Embedded";
            if (!string.IsNullOrWhiteSpace(source.PackageId)) return source.PackageId;
            return (source.PackageName ?? "Embedded") +
                   (string.IsNullOrWhiteSpace(source.PackageVersion)
                       ? string.Empty
                       : "@" + source.PackageVersion);
        }

        private static string SafeDirectoryName(string value)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? "Module" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                candidate = candidate.Replace(invalid, '_');
            return candidate;
        }

        /// <summary>
        /// 为隔离工程生成可读、稳定且不会与程序集同名的源码目录。Unity 6000.3 在目录名与其中
        /// <c>.asmdef</c> 同名时可能把该定义误交给 <c>DefaultImporter</c>，形成没有真实 Module IL 的空壳构建。
        /// 源目录职责名保留可读性，程序集名负责消除不同 Package 都使用 <c>Runtime</c> 等叶目录时的碰撞。
        /// </summary>
        internal static string ModuleDestinationDirectoryName(ModuleSourcePlan module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            string normalizedAssetDirectory = NormalizeAssetPath(module.AssetDirectory);
            string sourceLeaf = Path.GetFileName(normalizedAssetDirectory);
            string assemblyName = string.IsNullOrWhiteSpace(module.AssemblyName)
                ? "Module"
                : module.AssemblyName.Trim();
            string assemblyIdentity = Regex.Replace(
                SafeDirectoryName(assemblyName), "[^A-Za-z0-9_-]", "_");
            string identityHash = ComputeTextFingerprint(assemblyName).Substring(0, 12);
            string candidate = SafeDirectoryName(sourceLeaf) + "__" + assemblyIdentity + "__" + identityHash;
            if (string.IsNullOrWhiteSpace(module.PhysicalDirectory) ||
                !Directory.Exists(module.PhysicalDirectory)) return candidate;

            var asmdefFileNames = new HashSet<string>(
                Directory.GetFiles(module.PhysicalDirectory, "*.asmdef", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension),
                StringComparer.OrdinalIgnoreCase);
            while (asmdefFileNames.Contains(candidate)) candidate += "__source";
            return candidate;
        }

        private static string NormalizeAssetPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');

        private static string ProjectRoot =>
            Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();

        private static string HashProjectPath()
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in ProjectRoot.ToUpperInvariant()) hash = hash * 31 + c;
                return hash.ToString("X8");
            }
        }
    }
}
