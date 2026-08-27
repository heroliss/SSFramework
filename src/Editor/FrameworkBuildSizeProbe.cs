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
        internal const string UnityIl2CppPathEnvironmentVariable = "UNITY_IL2CPP_PATH";

        private const string LatestRunPreferencePrefix = "SSFramework.BuildSizeProbe.LatestRun.";
        internal const int CurrentReportFormatVersion = 5;
        private static readonly Regex DependencyEntryRegex = new(
            "\\\"(?<id>[^\\\"]+)\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
            RegexOptions.Compiled);

        private static ActiveRun _activeRun;
        private static Process _childProcess;
        private static ProfilePlan _activeProfile;
        private static bool _stopAfterCurrent;

        internal static event Action Changed;

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
            public OutputFileRecord[] LargestFiles = Array.Empty<OutputFileRecord>();
        }

        [Serializable]
        internal sealed class RunReport
        {
            public int FormatVersion = CurrentReportFormatVersion;
            public string CreatedUtc;
            public string CompletedUtc;
            public string UnityVersion;
            public string Target;
            public string ScriptingBackend;
            public string StrippingLevel;
            public bool DevelopmentBuild;
            public string EvidenceScope;
            [NonSerialized]
            public string RunDirectory;
            public ProfileRecord[] Profiles = Array.Empty<ProfileRecord>();
        }

        private sealed class ActiveRun
        {
            internal string ProjectDirectory;
            internal string RunDirectory;
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
        internal static bool StopAfterCurrentRequested => _stopAfterCurrent;
        internal static RunReport CurrentReport => _activeRun?.Report;

        /// <summary>
        /// 无窗口状态的 Core 删除测试入口，供 CI / AI 自动化复用与人工快速回归。
        /// 完整组合选择仍使用“真实构建体积证据”窗口。
        /// </summary>
        [MenuItem("SSFramework/诊断/AI 自动化/Core 隔离构建（Player Build）", priority = 31)]
        private static void StartCoreOnlyFromMenu()
        {
            try
            {
                Start(new[] { "core" });
                FrameworkEditorFeedback.ReportSummary(
                    "Core 隔离构建",
                    "已在 Library/SSFramework/BuildSizeProbe 启动独立 Player Build；结果完成后可在“真实构建体积证据”窗口查看。");
            }
            catch (Exception exception)
            {
                FrameworkEditorFeedback.ReportResult("Core 隔离构建", false, exception.Message);
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
            Changed?.Invoke();
        }

        internal static void EnsureReportCanBeRebuilt(RunReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (report.FormatVersion > CurrentReportFormatVersion)
                throw new InvalidDataException(
                    $"报告格式 v{report.FormatVersion} 新于当前工具支持的 v{CurrentReportFormatVersion}；" +
                    "拒绝用旧代码重写并丢失未知字段。请切回生成该报告的版本。 ");
        }

        internal static ProfilePlan[] CreatePlans()
        {
            FrameworkModuleAudit.Snapshot snapshot = FrameworkModuleAudit.Capture();
            var result = FrameworkModuleAudit.Analyze(snapshot);
            var copiedSourceCache = new Dictionary<string, PackageSourcePlan>(StringComparer.Ordinal);
            string sourceManifest = File.ReadAllText(FullPath("Packages/manifest.json"), Encoding.UTF8);
            var profiles = result.CommonProfiles
                .Select(profile => (profile, advanced: false))
                .Concat(new[] { (profile: result.FullProfile, advanced: false) })
                .Concat(result.ModuleProfiles.Select(profile => (profile, advanced: true)));
            var runtimeByName = result.RuntimeModules.ToDictionary(module => module.Name, StringComparer.Ordinal);
            var sourceByName = runtimeByName.Values
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
            return profiles.Select(item =>
            {
                FrameworkModuleAudit.AuditProfile profile = item.profile;
                string[] assemblies = BuildFrameworkCompileClosure(
                    snapshot, profile.Footprint.FrameworkAssemblies, runtimeByName.Keys);
                PackageDependencyPlan dependencies = BuildPackageDependencyPlan(
                    snapshot, assemblies, copiedSourceCache);
                string minimalManifest = CreateMinimalManifest(
                    sourceManifest, dependencies.ManifestPackages);
                return new ProfilePlan
                {
                    Key = profile.Key,
                    Title = profile.Title,
                    Description = profile.Description,
                    RootAssemblies = profile.Roots,
                    Assemblies = assemblies,
                    Sources = assemblies.Select(name => sourceByName[name]).ToArray(),
                    ManifestPackages = dependencies.ManifestPackages,
                    ManifestFingerprint = ComputeTextFingerprint(minimalManifest),
                    MinimalManifest = minimalManifest,
                    CopiedPackages = dependencies.CopiedPackages,
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
                SourceFingerprint = ComputeDirectoryFingerprint(location.PhysicalPath, _ => false),
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
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Unity 正在编译或刷新资源，请完成后再启动构建探针。");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先退出 Play Mode，再启动隔离构建探针。");
            if (BuildPipeline.isBuildingPlayer)
                throw new InvalidOperationException("当前已有 Player Build 正在运行。");

            var selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
            ProfilePlan[] plans = CreatePlans().Where(plan => selected.Contains(plan.Key)).ToArray();
            if (plans.Length == 0)
                throw new InvalidOperationException("至少选择一个构建组合。");

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + target;
            string runDirectory = FullPath(Path.Combine(RunsRoot, runId));
            string projectDirectory = Path.Combine(runDirectory, "Project");

            var report = new RunReport
            {
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

            PrepareWorkspace(projectDirectory);
            _activeRun = new ActiveRun
            {
                ProjectDirectory = projectDirectory,
                RunDirectory = runDirectory,
                Pending = new Queue<ProfilePlan>(plans),
                Report = report,
            };
            _stopAfterCurrent = false;
            EditorPrefs.SetString(LatestRunPreferencePrefix + HashProjectPath(), runDirectory);
            WriteReports(_activeRun.Report);
            EditorApplication.update += PollChildProcess;
            StartNextProfile();
        }

        internal static void RequestStopAfterCurrent()
        {
            if (!IsRunning) return;
            _stopAfterCurrent = true;
            foreach (var record in _activeRun.Report.Profiles.Where(record => record.Status == "等待"))
                record.Message = "当前组合结束后停止，不再启动新的 Unity 子进程。";
            WriteReports(_activeRun.Report);
            Changed?.Invoke();
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

        private static void PrepareWorkspace(string projectDirectory)
        {
            if (Directory.Exists(projectDirectory))
                throw new IOException("隔离构建目录已经存在，拒绝覆盖：" + projectDirectory);

            Directory.CreateDirectory(Path.Combine(projectDirectory, "Assets", "Editor"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Packages"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "ProjectSettings"));

            string templateSource = ResolveChildTemplate().PhysicalPath;
            if (!File.Exists(templateSource))
                throw new FileNotFoundException("找不到隔离构建子进程模板。", templateSource);
            File.Copy(templateSource,
                Path.Combine(projectDirectory, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs"));

            string projectVersion = FullPath("ProjectSettings/ProjectVersion.txt");
            File.Copy(projectVersion, Path.Combine(projectDirectory, "ProjectSettings", "ProjectVersion.txt"));
        }

        private static void StartNextProfile()
        {
            if (_activeRun == null) return;
            if (_stopAfterCurrent || _activeRun.Pending.Count == 0)
            {
                CompleteRun(_stopAfterCurrent ? "按请求在当前组合完成后停止。" : null);
                return;
            }

            _activeProfile = _activeRun.Pending.Dequeue();
            ProfileRecord record = FindRecord(_activeProfile.Key);
            record.Status = "准备";
            record.Message = "正在复制所选 Module 并准备隔离工程。";
            Changed?.Invoke();

            try
            {
                PrepareProfileSources(_activeRun.ProjectDirectory, _activeProfile);
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
                Changed?.Invoke();

                _childProcess = StartUnityChild(_activeRun, _activeProfile, outputPath, resultPath, logPath);
                record.ChildProcessId = _childProcess.Id;
                WriteReports(_activeRun.Report);
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                record.Status = "失败";
                record.Message = ex.Message;
                record.Errors = Math.Max(record.Errors, 1);
                WriteReports(_activeRun.Report);
                Debug.LogException(ex);
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
            ApplyChildResult(record, exitCode);
            WriteReports(_activeRun.Report);
            Changed?.Invoke();
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
            foreach (var record in _activeRun.Report.Profiles.Where(record => record.Status == "等待"))
            {
                record.Status = "跳过";
                record.Message = note ?? "未运行。";
            }
            _activeRun.Report.CompletedUtc = DateTime.UtcNow.ToString("O");
            WriteReports(_activeRun.Report);
            _activeRun = null;
            _activeProfile = null;
            _stopAfterCurrent = false;
            EditorApplication.update -= PollChildProcess;
            Changed?.Invoke();
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
                var plans = CreatePlans().ToDictionary(plan => plan.Key, StringComparer.Ordinal);
                string sourceDrift = FindRecoveryDrift(report, plans);
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
                    Pending = pending,
                    Report = report,
                };
                _stopAfterCurrent = false;
                EditorApplication.update -= PollChildProcess;
                EditorApplication.update += PollChildProcess;

                ProfileRecord building = report.Profiles.FirstOrDefault(record => record.Status == "构建中");
                if (!string.IsNullOrEmpty(sourceDrift))
                {
                    if (building != null && TryAttachUnityProcess(building.ChildProcessId, out _childProcess))
                    {
                        _activeProfile = plans.TryGetValue(building.Key, out ProfilePlan currentPlan)
                            ? currentPlan
                            : new ProfilePlan { Key = building.Key, Title = building.Title };
                        _stopAfterCurrent = true;
                        building.Message = "检测到源码身份漂移；已重新附着当前子进程，完成后停止：" + sourceDrift;
                        foreach (ProfileRecord waiting in report.Profiles.Where(record => record.Status == "等待"))
                            waiting.Message = "检测到源码身份漂移；当前组合完成后跳过，避免混合两套来源。";
                        WriteReports(report);
                        Changed?.Invoke();
                        return;
                    }

                    if (building != null)
                    {
                        building.Status = "失败";
                        building.Errors = Math.Max(building.Errors, 1);
                        building.ChildProcessId = 0;
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
                    _stopAfterCurrent = true;
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

                if (TryAttachUnityProcess(building.ChildProcessId, out _childProcess))
                {
                    building.Message = "主 Unity 重载后已重新附着正在运行的隔离构建子进程。";
                    WriteReports(report);
                    Changed?.Invoke();
                    return;
                }

                if (File.Exists(building.ResultPath))
                {
                    building.ChildProcessId = 0;
                    ApplyChildResult(building, building.ExitCode);
                }
                else
                {
                    building.Status = "失败";
                    building.Errors = Math.Max(building.Errors, 1);
                    building.ChildProcessId = 0;
                    building.Message = "主 Unity 重载后没有找到原子进程或结果文件；未猜测成功，继续后续组合。";
                }
                WriteReports(report);
                StartNextProfile();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _activeRun = null;
                _activeProfile = null;
                _childProcess = null;
                EditorApplication.update -= PollChildProcess;
            }
        }

        private static bool TryAttachUnityProcess(int processId, out Process process)
        {
            process = null;
            if (processId <= 0) return false;
            try
            {
                Process candidate = Process.GetProcessById(processId);
                if (candidate.HasExited ||
                    candidate.ProcessName.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    candidate.Dispose();
                    return false;
                }
                process = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void PrepareProfileSources(string projectDirectory, ProfilePlan profile)
        {
            string childTemplate = ResolveChildTemplate().PhysicalPath;
            File.Copy(childTemplate,
                Path.Combine(projectDirectory, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs"), true);

            if (string.IsNullOrWhiteSpace(profile.MinimalManifest))
                throw new InvalidDataException(
                    $"构建组合 {profile.Key} 缺少启动时冻结的最小 manifest，拒绝在运行中重新猜依赖版本。");
            File.WriteAllText(
                Path.Combine(projectDirectory, "Packages", "manifest.json"),
                profile.MinimalManifest,
                new UTF8Encoding(false));

            string packagesDirectory = Path.Combine(projectDirectory, "Packages");
            foreach (string directory in Directory.GetDirectories(packagesDirectory))
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
                CopyDirectory(
                    Path.GetFullPath(package.PhysicalDirectory),
                    Path.Combine(packagesDirectory, SafeDirectoryName(package.PackageName)),
                    _ => false);
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
                    SafeDirectoryName(module.AssemblyName));
                CopyDirectory(source, destination, ShouldSkipModulePath);
            }

            string rootDirectory = Path.Combine(projectDirectory, "Assets", "ProbeRoot");
            Directory.CreateDirectory(rootDirectory);
            File.WriteAllText(Path.Combine(rootDirectory, "link.xml"), CreateLinkXml(profile.Assemblies),
                new UTF8Encoding(false));
        }

        private static void WriteReports(RunReport report)
        {
            NormalizeShippingEvidence(report);
            Directory.CreateDirectory(report.RunDirectory);
            File.WriteAllText(Path.Combine(report.RunDirectory, "report.json"),
                JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(report.RunDirectory, "report.md"),
                CreateMarkdownReport(report), new UTF8Encoding(false));
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
            string[] files = Directory.GetFiles(record.OutputPath, "*", SearchOption.AllDirectories);
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

        private static void CopyDirectory(string source, string destination, Func<string, bool> skip)
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("找不到隔离探针依赖目录：" + source);
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = RelativePath(source, directory);
                if (skip(relative)) continue;
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = RelativePath(source, file);
                if (skip(relative)) continue;
                string destinationFile = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destination);
                File.Copy(file, destinationFile, true);
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
            if (!Directory.Exists(directory)) return;
            string resolvedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            string resolvedWorkspace = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) +
                                       Path.DirectorySeparatorChar;
            if (!resolvedDirectory.StartsWith(resolvedWorkspace, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除隔离工作区之外的目录：" + resolvedDirectory);
            Directory.Delete(resolvedDirectory, true);
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            try
            {
                string[] all = File.ReadAllLines(path);
                string[] signals = all.Where(IsDiagnosticLogLine).Take(lines).ToArray();
                return signals.Length > 0
                    ? string.Join("\n", signals)
                    : string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
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
                    ? $"报告格式早于 v{CurrentReportFormatVersion}，缺少派生 Package 计划或复制内容指纹，" +
                      "拒绝跨 Domain Reload 猜测续跑。"
                    : $"报告格式 v{report.FormatVersion} 新于当前工具支持的 v{CurrentReportFormatVersion}；" +
                      "旧代码不能安全解释未知字段，拒绝续跑。";
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
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
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
                    using FileStream input = File.OpenRead(file);
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
