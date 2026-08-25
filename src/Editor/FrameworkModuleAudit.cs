using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 从当前 Player 编译图与已编译 DLL 元数据生成 Framework Module 裁剪证据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CompilationPipeline.GetAssemblies(AssembliesType)"/> 给出 asmdef 的编译可见图，但
    /// <c>auto-reference</c> 会把“编译器能看到”放大成“运行时一定依赖”。本审计继续读取 DLL 的
    /// <see cref="System.Reflection.AssemblyName"/> 引用表，只把真正写进元数据的引用计入闭包，避免误报。
    /// </para>
    /// <para>
    /// 字节数是链接、AOT、压缩前的原始托管程序集证据，只适合比较 Module 组合，不等于最终安装包增量。
    /// 最终结论仍以目标平台的 Player BuildReport 为准。
    /// </para>
    /// </remarks>
    internal static class FrameworkModuleAudit
    {
        internal const string CoreAssemblyName = "Game.Framework";
        internal const string SharedUiAssemblyName = "Game.Framework.UI";
        internal const string UGuiAssemblyName = "Game.Framework.UI.UGui";
        internal const string ToolkitAssemblyName = "Game.Framework.UI.Toolkit";
        internal const string BridgeAssemblyName = "Game.Framework.UI.Bridge";

        private static readonly string[] EditorOnlyConstraints = { "UNITY_EDITOR", "UNITY_INCLUDE_TESTS" };
        private static readonly Dictionary<string, AssemblyReferenceCacheEntry> AssemblyReferenceCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AssemblyReferenceCacheLock = new();

        internal sealed class AssemblyInfo
        {
            internal string Name;
            internal string AsmdefPath;
            internal string SourceDirectory;
            internal string PackageName;
            internal string PackageVersion;
            internal string PackageId;
            internal string OutputPath;
            internal long OutputBytes;
            internal bool AutoReferenced;
            internal string[] DeclaredReferences = Array.Empty<string>();
            internal string[] ActualReferences = Array.Empty<string>();

            internal bool IsFrameworkRuntime => IsFrameworkAssembly(Name) &&
                                                !Name.Equals("Game.Framework.Boot", StringComparison.Ordinal);
        }

        internal sealed class Snapshot
        {
            internal readonly Dictionary<string, AssemblyInfo> Assemblies;
            internal readonly Dictionary<string, string> ReferencePaths;
            internal readonly string[] HotUpdateRoots;
            internal readonly string HotUpdateNote;
            internal readonly LinkerPreservation[] LinkerPreservations;
            internal readonly Dictionary<string, string[]> DeclaredConsumersByDependency;
            internal readonly HotUpdateDeploymentEvidence HotUpdateDeployment;

            internal Snapshot(
                Dictionary<string, AssemblyInfo> assemblies,
                Dictionary<string, string> referencePaths,
                string[] hotUpdateRoots,
                string hotUpdateNote,
                LinkerPreservation[] linkerPreservations = null,
                Dictionary<string, string[]> declaredConsumersByDependency = null,
                HotUpdateDeploymentEvidence hotUpdateDeployment = null)
            {
                Assemblies = assemblies;
                ReferencePaths = referencePaths;
                HotUpdateRoots = hotUpdateRoots;
                HotUpdateNote = hotUpdateNote;
                LinkerPreservations = linkerPreservations ?? Array.Empty<LinkerPreservation>();
                DeclaredConsumersByDependency = declaredConsumersByDependency ??
                                                        new Dictionary<string, string[]>(StringComparer.Ordinal);
                HotUpdateDeployment = hotUpdateDeployment ?? new HotUpdateDeploymentEvidence();
            }
        }

        internal sealed class Footprint
        {
            internal readonly SortedSet<string> FrameworkAssemblies = new(StringComparer.Ordinal);
            internal readonly SortedSet<string> ProjectAssemblies = new(StringComparer.Ordinal);
            internal readonly SortedDictionary<string, long> ExternalAssemblies = new(StringComparer.Ordinal);
            internal readonly SortedSet<string> UnresolvedAssemblies = new(StringComparer.Ordinal);
            internal long FrameworkBytes;
            internal long ProjectBytes;
            internal long ExternalBytes;
        }

        /// <summary>记录“DLL 真实引用存在，但 asmdef 没有直接声明”的模块依赖。</summary>
        internal sealed class DependencyIssue
        {
            internal string ModuleName;
            internal string[] References = Array.Empty<string>();
        }

        /// <summary>一个常见入口组合及其真实程序集闭包，供窗口、文本报告和测试共同消费。</summary>
        internal sealed class AuditProfile
        {
            internal string Key;
            internal string Title;
            internal string Description;
            internal string[] Roots = Array.Empty<string>();
            internal Footprint Footprint;
        }

        /// <summary>用通俗名称解释一条 Module 删除测试及其结果。</summary>
        internal sealed class DeletionCheck
        {
            internal string Name;
            internal string Explanation;
            internal bool Passed;
        }

        /// <summary>
        /// 一条位于项目 Assets 或已安装 Package 中的 UnityLinker 根标记。它可能保留 Module 自身，也可能由某个可选 Module
        /// 保留外部程序集；后者同样会影响“没有引用该 Module 时是否真的能变小”。
        /// </summary>
        internal sealed class LinkerPreservation
        {
            internal string OwnerModuleName;
            internal string Path;
            internal string AssemblyName;
            internal string Scope;
            internal bool IgnoreIfUnreferenced;
            internal bool RequiredOnlyIfReferenced;
            internal string SourcePackageName;
            internal string SourcePackageVersion;
            internal string SourcePackageId;

            internal bool IsUnconditional => !IgnoreIfUnreferenced && !RequiredOnlyIfReferenced;
            internal bool IsGenerated => Path.StartsWith("Assets/HybridCLRGenerate/", StringComparison.OrdinalIgnoreCase);
            internal bool IsFrameworkModuleOwned => !string.IsNullOrEmpty(OwnerModuleName);
        }

        /// <summary>
        /// 一个 Runtime Module 的当前保留原因与移除准备信息。这里只陈述可证明的输入，
        /// 不把“出现在编译图”冒充“必然进入最终 Player”。
        /// </summary>
        internal sealed class ModuleStatus
        {
            internal AssemblyInfo Module;
            internal string[] DirectConsumers = Array.Empty<string>();
            internal string[] FrameworkConsumers = Array.Empty<string>();
            internal string[] ProjectConsumers = Array.Empty<string>();
            internal string[] RemovalBlockers = Array.Empty<string>();
            internal string[] FrameworkDependencies = Array.Empty<string>();
            internal string[] HotUpdateDependencies = Array.Empty<string>();
            internal bool IsHotUpdateRoot;
            internal LinkerPreservation[] TargetingPreservations = Array.Empty<LinkerPreservation>();
            internal LinkerPreservation[] OwnedPreservations = Array.Empty<LinkerPreservation>();
            internal string[] RetentionReasons = Array.Empty<string>();
            internal string[] RemovalSteps = Array.Empty<string>();

            internal bool HasUnconditionalPreservation =>
                TargetingPreservations.Any(rule => rule.IsUnconditional) ||
                OwnedPreservations.Any(rule => rule.IsUnconditional);

            internal bool HasHotUpdateViolation => !IsHotUpdateRoot && HotUpdateDependencies.Length > 0;
        }

        /// <summary>
        /// 热更 Profile 的只读派生证据。Build Editor Module 仍是具体设置、Generate 与中转清单的 owner；
        /// 通用审计经反射读取，保持删除 Build Module 后仍可编译。
        /// </summary>
        internal sealed class HotUpdateDeploymentEvidence
        {
            internal bool BuildModuleAvailable;
            internal bool ProfileAvailable;
            internal bool InspectionAvailable;
            internal int ProfileCount;
            internal string ProfilePath = string.Empty;
            internal string[] ProfileAssemblies = Array.Empty<string>();
            internal string[] SettingsAssemblies = Array.Empty<string>();
            internal string[] LegacySettingsAssemblies = Array.Empty<string>();
            internal bool SettingsAvailable;
            internal bool SettingsMatch;
            internal string SettingsMessage = string.Empty;
            internal bool GenerationRequired;
            internal bool GenerationFresh;
            internal string GenerationMessage = string.Empty;
            internal bool StagingRequired;
            internal bool StagedManifestExists;
            internal bool StagedManifestAvailable;
            internal bool StagedManifestMatches;
            internal string StagedVersion = string.Empty;
            internal string[] StagedAssemblies = Array.Empty<string>();
            internal string[] ExpectedAotMetadataDlls = Array.Empty<string>();
            internal string[] StagedAotMetadataDlls = Array.Empty<string>();
            internal string[] MissingStagedFiles = Array.Empty<string>();
            internal string[] UnexpectedStagedFiles = Array.Empty<string>();
            internal string[] InvalidStagedEntries = Array.Empty<string>();
            internal string StagedMessage = string.Empty;
            internal string Note = string.Empty;

            internal bool RequiresAttention => BuildModuleAvailable &&
                                               (!ProfileAvailable || ProfileCount > 1 || !InspectionAvailable ||
                                                !SettingsAvailable || !SettingsMatch ||
                                                (GenerationRequired && !GenerationFresh) ||
                                                (StagingRequired
                                                    ? !StagedManifestAvailable || !StagedManifestMatches
                                                    : StagedManifestExists && !StagedManifestMatches));
        }

        /// <summary>
        /// 一次审计的结构化结果；所有展示层都从这里取数，避免文本报告与窗口结论各算一套。
        /// </summary>
        internal sealed class AuditResult
        {
            internal AssemblyInfo[] RuntimeModules = Array.Empty<AssemblyInfo>();
            internal DependencyIssue[] DependencyIssues = Array.Empty<DependencyIssue>();
            internal AuditProfile[] CommonProfiles = Array.Empty<AuditProfile>();
            internal AuditProfile[] ModuleProfiles = Array.Empty<AuditProfile>();
            internal AuditProfile FullProfile;
            internal AuditProfile HotUpdateProfile;
            internal string HotUpdateNote;
            internal HotUpdateDeploymentEvidence HotUpdateDeployment = new();
            internal ModuleStatus[] ModuleStatuses = Array.Empty<ModuleStatus>();
            internal LinkerPreservation[] UnconditionalModulePreservations = Array.Empty<LinkerPreservation>();
            internal LinkerPreservation[] GlobalPreservations = Array.Empty<LinkerPreservation>();
            internal DeletionCheck[] DeletionChecks = Array.Empty<DeletionCheck>();
            internal string[] Recommendations = Array.Empty<string>();
            internal bool AllRuntimeModulesOptIn;

            internal IEnumerable<AuditProfile> AllProfiles => CommonProfiles
                .Concat(ModuleProfiles)
                .Concat(FullProfile != null ? new[] { FullProfile } : Array.Empty<AuditProfile>())
                .Concat(HotUpdateProfile != null ? new[] { HotUpdateProfile } : Array.Empty<AuditProfile>());

            internal bool HasUnresolvedAssemblies =>
                AllProfiles.Any(profile => profile.Footprint.UnresolvedAssemblies.Count > 0);

            internal bool HasRetentionWarnings => UnconditionalModulePreservations.Length > 0;
            internal bool HasHotUpdateViolations => ModuleStatuses.Any(status => status.HasHotUpdateViolation);
            internal bool HasHotUpdateDeploymentWarnings => HotUpdateDeployment?.RequiresAttention == true;

            internal bool RequiresAttention => !IsHealthy || HasRetentionWarnings || HasHotUpdateDeploymentWarnings;

            internal bool IsHealthy => DependencyIssues.Length == 0 &&
                                       AllRuntimeModulesOptIn &&
                                       !HasUnresolvedAssemblies &&
                                       !HasHotUpdateViolations &&
                                       DeletionChecks.All(check => check.Passed);
        }

        internal static Snapshot Capture()
        {
            var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Where(assembly => !IsEditorConstrained(assembly.name))
                .ToArray();

            var referencePaths = BuildReferencePathMap(playerAssemblies);
            var infos = new Dictionary<string, AssemblyInfo>(StringComparer.Ordinal);
            foreach (var assembly in playerAssemblies)
            {
                string reportedAsmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                FrameworkModuleSourceCatalog.SourceLocation asmdefSource = null;
                if (!string.IsNullOrWhiteSpace(reportedAsmdefPath) &&
                    !FrameworkModuleSourceCatalog.TryResolve(
                        reportedAsmdefPath, out asmdefSource, out string sourceReason))
                    throw new InvalidDataException(
                        $"无法解析程序集 {assembly.name} 的源码身份：{sourceReason}");
                if (IsFrameworkAssembly(assembly.name) &&
                    (asmdefSource == null || !File.Exists(asmdefSource.PhysicalPath)))
                    throw new FileNotFoundException(
                        $"Framework Module {assembly.name} 已进入 Player 编译图，但找不到其 asmdef 物理源码。",
                        asmdefSource?.PhysicalPath ?? reportedAsmdefPath);
                var dto = ReadAsmdef(reportedAsmdefPath);
                string outputPath = FullPath(assembly.outputPath);
                infos[assembly.name] = new AssemblyInfo
                {
                    Name = assembly.name,
                    AsmdefPath = asmdefSource?.AssetPath ?? reportedAsmdefPath ?? string.Empty,
                    SourceDirectory = asmdefSource?.PhysicalDirectory ?? string.Empty,
                    PackageName = asmdefSource?.PackageName ?? string.Empty,
                    PackageVersion = asmdefSource?.PackageVersion ?? string.Empty,
                    PackageId = asmdefSource?.PackageId ?? string.Empty,
                    OutputPath = outputPath,
                    OutputBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L,
                    AutoReferenced = dto?.autoReferenced ?? true,
                    DeclaredReferences = GetDeclaredReferences(dto),
                    ActualReferences = ReadAssemblyReferences(outputPath),
                };
            }

            HotUpdateDeploymentEvidence hotUpdate = ReadHotUpdateEvidence();
            return new Snapshot(
                infos,
                referencePaths,
                hotUpdate.ProfileAssemblies,
                hotUpdate.Note,
                ReadLinkerPreservations(infos),
                ReadDeclaredConsumers(),
                hotUpdate);
        }

        /// <summary>
        /// 把当前编译快照整理成健康结论、常用组合、删除检查和建议，不在展示层重复推导架构语义。
        /// </summary>
        internal static AuditResult Analyze(Snapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var runtimeModules = snapshot.Assemblies.Values
                .Where(info => info.IsFrameworkRuntime)
                .OrderBy(info => info.Name, StringComparer.Ordinal)
                .ToArray();
            var dependencyIssues = runtimeModules
                .Select(module => new DependencyIssue
                {
                    ModuleName = module.Name,
                    References = FindUndeclaredExternalReferences(snapshot, module),
                })
                .Where(issue => issue.References.Length > 0)
                .ToArray();

            AuditProfile Profile(string key, string title, string description, IEnumerable<string> roots)
            {
                string[] rootArray = roots.Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                return new AuditProfile
                {
                    Key = key,
                    Title = title,
                    Description = description,
                    Roots = rootArray,
                    Footprint = Measure(snapshot, rootArray),
                };
            }

            var commonProfiles = new[]
            {
                Profile("core", "只用核心", "适合只使用 Context、Command、Model、System 等基础能力。",
                    new[] { CoreAssemblyName }),
                Profile("ugui", "核心 + UGUI", "适合使用 UGUI 窗口框架与增量列表绑定。",
                    new[] { UGuiAssemblyName }),
                Profile("toolkit", "核心 + UI Toolkit", "适合使用 UI Toolkit 窗口框架与增量列表绑定。",
                    new[] { ToolkitAssemblyName }),
            };
            var moduleProfiles = runtimeModules
                .Where(module => !module.Name.Equals(CoreAssemblyName, StringComparison.Ordinal))
                .Select(module => Profile(
                    "module-" + ToProfileKey(module.Name),
                    FriendlyModuleName(module.Name) + " 入口",
                    $"以 {module.Name} 为唯一 Framework 入口，自动带上它的真实依赖闭包。",
                    new[] { module.Name }))
                .ToArray();
            var fullProfile = Profile(
                "full", "全部运行时模块", "用于查看能力上限，不代表推荐所有项目全部引入。",
                runtimeModules.Select(module => module.Name));
            AuditProfile hotUpdateProfile = snapshot.HotUpdateRoots.Length > 0
                ? Profile("hot-update", "热更 Profile 期望档位", "HybridCLR 以程序集为最小热更粒度；实际设置与产物仍需经过同步、Generate 和代码包构建。",
                    snapshot.HotUpdateRoots)
                : null;

            var coreClosure = ComputeReachableAssemblies(snapshot.Assemblies, new[] { CoreAssemblyName });
            var uguiClosure = ComputeReachableAssemblies(snapshot.Assemblies, new[] { UGuiAssemblyName });
            var toolkitClosure = ComputeReachableAssemblies(snapshot.Assemblies, new[] { ToolkitAssemblyName });
            ModuleStatus[] moduleStatuses = BuildModuleStatuses(snapshot, runtimeModules);
            var result = new AuditResult
            {
                RuntimeModules = runtimeModules,
                DependencyIssues = dependencyIssues,
                CommonProfiles = commonProfiles,
                ModuleProfiles = moduleProfiles,
                FullProfile = fullProfile,
                HotUpdateProfile = hotUpdateProfile,
                HotUpdateNote = snapshot.HotUpdateNote,
                HotUpdateDeployment = snapshot.HotUpdateDeployment,
                ModuleStatuses = moduleStatuses,
                UnconditionalModulePreservations = moduleStatuses
                    .SelectMany(status => status.TargetingPreservations.Concat(status.OwnedPreservations))
                    .Where(rule => rule.IsUnconditional)
                    .GroupBy(rule => rule.Path + "\0" + rule.AssemblyName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(rule => rule.OwnerModuleName, StringComparer.Ordinal)
                    .ThenBy(rule => rule.AssemblyName, StringComparer.Ordinal)
                    .ToArray(),
                GlobalPreservations = snapshot.LinkerPreservations
                    .Where(rule => !rule.IsFrameworkModuleOwned)
                    .OrderBy(rule => rule.Path, StringComparer.Ordinal)
                    .ThenBy(rule => rule.AssemblyName, StringComparer.Ordinal)
                    .ToArray(),
                AllRuntimeModulesOptIn = runtimeModules.All(module => !module.AutoReferenced),
                DeletionChecks = new[]
                {
                    new DeletionCheck
                    {
                        Name = "只用核心时不带 UI",
                        Explanation = "小项目可以只保留 MVCS / Context，不被窗口框架反向拖住。",
                        Passed = !coreClosure.Any(name => name.Equals(SharedUiAssemblyName, StringComparison.Ordinal) ||
                                                                  name.StartsWith(SharedUiAssemblyName + ".", StringComparison.Ordinal)),
                    },
                    new DeletionCheck
                    {
                        Name = "UGUI 不带 Toolkit / Bridge",
                        Explanation = "只选 UGUI 时，不会顺带引入另一套 UI 后端或嵌入桥。",
                        Passed = !uguiClosure.Contains(ToolkitAssemblyName) &&
                                 !uguiClosure.Contains(BridgeAssemblyName),
                    },
                    new DeletionCheck
                    {
                        Name = "Toolkit 不带 UGUI / Bridge",
                        Explanation = "只选 Toolkit 时，不会顺带引入 UGUI 后端或嵌入桥。",
                        Passed = !toolkitClosure.Contains(UGuiAssemblyName) &&
                                 !toolkitClosure.Contains(BridgeAssemblyName),
                    },
                },
            };
            result.Recommendations = BuildRecommendations(result);
            return result;
        }

        internal static string CreateReport(Snapshot snapshot) => CreateReport(Analyze(snapshot));

        internal static string CreateReport(AuditResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var sb = new StringBuilder(8192);
            sb.AppendLine("Framework Module 裁剪审计");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine(!result.RequiresAttention
                ? "结论：当前模块边界健康，没有发现会阻碍按需裁剪的问题。"
                : result.IsHealthy
                    ? "结论：程序集依赖方向健康，但 linker 保留或热更派生状态仍需要理解 / 处理。"
                    : "结论：发现需要处理或确认的问题，请先看检查结果。 ");
            sb.AppendLine("说明：这里比较的是编译后的原始 DLL，不是最终包体；真正发布大小仍以目标平台 Player BuildReport 为准。 ");
            sb.AppendLine();

            sb.AppendLine($"运行时 Framework Module：{result.RuntimeModules.Length} 个");
            foreach (var module in result.RuntimeModules)
            {
                string auto = module.AutoReferenced ? "⚠ autoReferenced:true" : "autoReferenced:false";
                sb.AppendLine($"  • {module.Name}  {FormatBytes(module.OutputBytes)}  {auto}");
            }
            sb.AppendLine();

            AppendDependencyVisibility(sb, result.DependencyIssues);
            AppendModuleStatuses(sb, result.ModuleStatuses);
            AppendGlobalPreservations(sb, result.GlobalPreservations);
            foreach (var profile in result.CommonProfiles)
                AppendProfile(sb, profile);
            AppendProfile(sb, result.FullProfile);

            sb.AppendLine("热更 Profile 期望档位");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine("  " + result.HotUpdateNote);
            if (result.HotUpdateProfile != null)
                AppendFootprint(sb, result.HotUpdateProfile, indent: "  ");
            sb.AppendLine();
            AppendHotUpdateDeployment(sb, result.HotUpdateDeployment);

            AppendDeletionTests(sb, result.DeletionChecks);
            return sb.ToString().TrimEnd();
        }

        internal static SortedSet<string> ComputeReachableAssemblies(
            IReadOnlyDictionary<string, AssemblyInfo> assemblies,
            IEnumerable<string> roots,
            Func<string, string[]> externalReferenceReader = null,
            Func<string, bool> isPlatformReference = null)
        {
            if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
            if (roots == null) throw new ArgumentNullException(nameof(roots));

            var result = new SortedSet<string>(StringComparer.Ordinal);
            isPlatformReference ??= IsPlatformReference;
            var pending = new Queue<string>(roots.Where(name => !string.IsNullOrWhiteSpace(name)));
            while (pending.Count > 0)
            {
                string name = pending.Dequeue();
                if (!result.Add(name)) continue;

                string[] references;
                if (assemblies.TryGetValue(name, out var info))
                    references = info.ActualReferences;
                else
                    references = externalReferenceReader?.Invoke(name) ?? Array.Empty<string>();

                foreach (string reference in references)
                    if (!isPlatformReference(reference))
                        pending.Enqueue(reference);
            }
            return result;
        }

        internal static string[] FindUndeclaredDirectReferences(
            AssemblyInfo info,
            Func<string, bool> isRelevantExternal)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (isRelevantExternal == null) throw new ArgumentNullException(nameof(isRelevantExternal));

            var declared = new HashSet<string>(info.DeclaredReferences, StringComparer.Ordinal);
            return info.ActualReferences
                .Where(isRelevantExternal)
                .Where(reference => !declared.Contains(reference))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();
        }

        internal static string[] FindUndeclaredExternalReferences(Snapshot snapshot, AssemblyInfo info)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return FindUndeclaredDirectReferences(info,
                reference => IsRelevantExternalReference(snapshot, reference));
        }

        internal static ModuleStatus[] BuildModuleStatuses(
            Snapshot snapshot,
            IEnumerable<AssemblyInfo> runtimeModules)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (runtimeModules == null) throw new ArgumentNullException(nameof(runtimeModules));

            var hotRoots = new HashSet<string>(snapshot.HotUpdateRoots, StringComparer.Ordinal);
            return runtimeModules
                .OrderBy(module => module.Name, StringComparer.Ordinal)
                .Select(module =>
                {
                    string[] consumers = snapshot.Assemblies.Values
                        .Where(candidate => !candidate.Name.Equals(module.Name, StringComparison.Ordinal))
                        .Where(candidate => candidate.ActualReferences.Contains(module.Name, StringComparer.Ordinal))
                        .Select(candidate => candidate.Name)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    string[] frameworkDependencies = module.ActualReferences
                        .Where(IsFrameworkAssembly)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    string[] frameworkConsumers = consumers
                        .Where(IsFrameworkAssembly)
                        .ToArray();
                    string[] projectConsumers = consumers
                        .Where(name => !IsFrameworkAssembly(name))
                        .ToArray();
                    // 与 HotUpdateAssemblyGraph 使用相同的 asmdef/编译图语义：即使代码暂未调用，
                    // 声明边仍会让 AOT 程序集依赖热更程序集，必须被校验拦下。
                    string[] hotDependencies = module.DeclaredReferences
                        .Where(hotRoots.Contains)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    string[] removalBlockers = snapshot.DeclaredConsumersByDependency.TryGetValue(
                        module.Name, out string[] declaredConsumers)
                        ? declaredConsumers
                        : Array.Empty<string>();
                    LinkerPreservation[] targeting = snapshot.LinkerPreservations
                        .Where(rule => rule.AssemblyName.Equals(module.Name, StringComparison.Ordinal))
                        .OrderBy(rule => rule.Path, StringComparer.Ordinal)
                        .ToArray();
                    LinkerPreservation[] owned = snapshot.LinkerPreservations
                        .Where(rule => rule.OwnerModuleName.Equals(module.Name, StringComparison.Ordinal))
                        .OrderBy(rule => rule.AssemblyName, StringComparer.Ordinal)
                        .ThenBy(rule => rule.Path, StringComparer.Ordinal)
                        .ToArray();
                    bool hot = hotRoots.Contains(module.Name);
                    return new ModuleStatus
                    {
                        Module = module,
                        DirectConsumers = consumers,
                        FrameworkConsumers = frameworkConsumers,
                        ProjectConsumers = projectConsumers,
                        RemovalBlockers = removalBlockers,
                        FrameworkDependencies = frameworkDependencies,
                        HotUpdateDependencies = hotDependencies,
                        IsHotUpdateRoot = hot,
                        TargetingPreservations = targeting,
                        OwnedPreservations = owned,
                        RetentionReasons = BuildRetentionReasons(
                            module, frameworkConsumers, projectConsumers, hot, hotDependencies, targeting, owned),
                        RemovalSteps = BuildRemovalSteps(
                            module, removalBlockers, hot, hotDependencies, targeting, owned),
                    };
                })
                .ToArray();
        }

        internal static LinkerPreservation[] ParseLinkerPreservations(
            string xml,
            string path,
            string ownerModuleName)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));
            if (path == null) throw new ArgumentNullException(nameof(path));

            var document = XDocument.Parse(xml, LoadOptions.None);
            XElement linker = document.Root;
            if (linker == null || linker.Name.LocalName != "linker")
                throw new InvalidDataException($"link.xml 缺少 linker 根元素：{path}");

            return linker.Elements()
                .Where(element => element.Name.LocalName == "assembly")
                .Select(element =>
                {
                    string fullname = (string)element.Attribute("fullname") ?? string.Empty;
                    string assemblyName = fullname.Split(',')[0].Trim();
                    string preserve = (string)element.Attribute("preserve");
                    bool ignore = IsTrue((string)element.Attribute("ignoreIfUnreferenced"));
                    int childRules = element.Elements().Count();
                    bool requiredOnlyIfReferenced = string.IsNullOrEmpty(preserve) && childRules > 0 &&
                                                    element.Elements().All(child =>
                                                        string.Equals((string)child.Attribute("required"), "0",
                                                            StringComparison.Ordinal));
                    string scope = !string.IsNullOrEmpty(preserve)
                        ? "preserve=" + preserve
                        : childRules > 0
                            ? $"{childRules} 条类型/成员规则"
                            : "默认保留整个程序集";
                    return new LinkerPreservation
                    {
                        OwnerModuleName = ownerModuleName ?? string.Empty,
                        Path = path,
                        AssemblyName = assemblyName,
                        Scope = scope,
                        IgnoreIfUnreferenced = ignore,
                        RequiredOnlyIfReferenced = requiredOnlyIfReferenced,
                    };
                })
                .Where(rule => !string.IsNullOrEmpty(rule.AssemblyName))
                .ToArray();
        }

        private static string[] BuildRetentionReasons(
            AssemblyInfo module,
            IReadOnlyCollection<string> frameworkConsumers,
            IReadOnlyCollection<string> projectConsumers,
            bool hot,
            IReadOnlyCollection<string> hotDependencies,
            IReadOnlyCollection<LinkerPreservation> targeting,
            IReadOnlyCollection<LinkerPreservation> owned)
        {
            var reasons = new List<string>();
            if (hot)
                reasons.Add("已列入 FrameworkHotUpdateProfile 的期望清单：完成同步与代码包构建后，完整 DLL 会进入 CodePackage；成员级 UnityLinker 不会替你移出这份部署清单。");
            if (hot && hotDependencies.Count > 0)
                reasons.Add("热更集合存在结构性传播：本 Module 直接引用已热更程序集 " +
                            string.Join("、", hotDependencies) +
                            "；只要本 Module 仍在 Player 编译图，就不能单独把它留在 AOT，否则会形成 AOT → 热更引用。");
            else if (!hot && hotDependencies.Count > 0)
                reasons.Add("当前存在非法的 AOT → 热更引用：本 Module 未列入热更 Profile，却直接引用 " +
                            string.Join("、", hotDependencies) +
                            "。先把本 Module 恢复为热更，或让它退出 Player 编译图 / 把依赖退回 AOT，再执行同步和构建。");
            if (projectConsumers.Count > 0)
                reasons.Add("项目程序集直接使用本 Module：" + string.Join("、", projectConsumers) +
                            "。这些是最需要先迁移或删除的真实消费方。");
            if (frameworkConsumers.Count > 0)
                reasons.Add("其他 Framework Module 直接依赖它：" + string.Join("、", frameworkConsumers) +
                            "。只有这些上层 Module 被项目选中时，这条引用链才成为最终 Player 的候选根。");
            foreach (var rule in targeting.Where(rule => rule.IsUnconditional))
                reasons.Add($"{rule.Path} 无条件保留本程序集（{rule.Scope}），它本身就是 UnityLinker 根标记。");
            foreach (var rule in targeting.Where(rule => !rule.IsUnconditional))
                reasons.Add($"{rule.Path} 含针对本程序集的条件规则（{rule.Scope}）；它只在程序集 / 类型已被引用时扩大保留，不单独建立根。");
            foreach (var rule in owned.Where(rule => rule.IsUnconditional &&
                                                      !rule.AssemblyName.Equals(module.Name, StringComparison.Ordinal)))
                reasons.Add($"本 Module 的 {rule.Path} 还会无条件保留 {rule.AssemblyName}（{rule.Scope}）；即使业务没有调用本 Module，也可能留下这项外部成本。");
            foreach (var rule in owned.Where(rule => !rule.IsUnconditional &&
                                                      !rule.AssemblyName.Equals(module.Name, StringComparison.Ordinal)))
                reasons.Add($"本 Module 的 {rule.Path} 对 {rule.AssemblyName} 使用条件保留（{rule.Scope}）；它不会独立建立根，但引用存在时会扩大保留范围。");
            if (reasons.Count == 0)
                reasons.Add("目前只证明源码会编译且程序集存在；没有发现热更清单、直接消费者或无条件 link.xml 根。是否进入最终 Player 仍由场景/资源根与 UnityLinker 决定。");
            return reasons.ToArray();
        }

        private static string[] BuildRemovalSteps(
            AssemblyInfo module,
            IReadOnlyCollection<string> removalBlockers,
            bool hot,
            IReadOnlyCollection<string> hotDependencies,
            IReadOnlyCollection<LinkerPreservation> targeting,
            IReadOnlyCollection<LinkerPreservation> owned)
        {
            if (module.Name.Equals(CoreAssemblyName, StringComparison.Ordinal))
                return new[] { "Core 是其余 Runtime Module 的稳定上游，不作为可删除项；轻量项目应从 Core 开始，只增加真正需要的 Module。" };

            var steps = new List<string>();
            if (removalBlockers.Count > 0)
                steps.Add("先处理完整 asmdef 图中的所有删除阻塞者（无论它们是否进入 Player）：" +
                          string.Join("、", removalBlockers) + "。即使没有实际调用，残留的 references 也会让物理删除后编译失败。");
            if (hot && hotDependencies.Count > 0)
                steps.Add("把“退出 Player 编译图（删除/卸载该 Module）”与“从 FrameworkHotUpdateProfile 移除”作为同一次结构变更；不要先单独同步取消热更。另一条路是先让它引用的热更依赖全部退回 AOT，但这通常会级联扩大改动。");
            else if (!hot && hotDependencies.Count > 0)
                steps.Add("当前 Profile 已处于非法中间状态：先把本 Module 恢复为热更，或在同一次结构变更中让它退出 Player 编译图；修正前不要执行同步、Generate 或出包。");
            else if (hot)
                steps.Add("若保留源码但取消热更，先确认没有 AOT → 热更引用，再从 FrameworkHotUpdateProfile 移除；若直接删除/卸载，则同时清理 Profile 条目。");
            if (targeting.Any(rule => rule.IsUnconditional) || owned.Any(rule => rule.IsUnconditional))
                steps.Add("复核该 Module 的 link.xml：物理移除 Module 时让规则一起消失；若只改为条件保留，必须做目标平台 IL2CPP/反射回归。");
            steps.Add("结构变更完成后再执行“同步热更设置”与 HybridCLR Generate，然后运行编译、模块裁剪审计和目标平台真实构建；不要凭 Console 是否安静判断成功。");
            return steps.ToArray();
        }

        private static void AppendDependencyVisibility(
            StringBuilder sb,
            IReadOnlyCollection<DependencyIssue> issues)
        {
            sb.AppendLine("依赖可见性");
            sb.AppendLine("────────────────────────────────────────");
            foreach (var issue in issues)
            {
                sb.AppendLine($"  ⚠ {issue.ModuleName} 的真实外部引用未在 asmdef 显式声明：" +
                              string.Join(", ", issue.References));
            }
            if (issues.Count == 0)
                sb.AppendLine("  ✓ 所有 Runtime Module 的真实外部引用都能从 asmdef 直接读出。");
            else
                sb.AppendLine($"  共 {issues.Sum(issue => issue.References.Length)} 条隐式引用；" +
                              "这不等于运行时错误，但会削弱删除测试、UPM 依赖声明与 AI 可导航性。");
            sb.AppendLine();
        }

        private static void AppendModuleStatuses(StringBuilder sb, IEnumerable<ModuleStatus> statuses)
        {
            sb.AppendLine("Module 当前保留原因");
            sb.AppendLine("────────────────────────────────────────");
            foreach (var status in statuses)
            {
                sb.AppendLine("  " + status.Module.Name);
                foreach (string reason in status.RetentionReasons)
                    sb.AppendLine("    • " + reason);
                sb.AppendLine("    移除准备：" + string.Join(" ", status.RemovalSteps));
            }
            sb.AppendLine();
        }

        private static void AppendGlobalPreservations(
            StringBuilder sb,
            IReadOnlyCollection<LinkerPreservation> preservations)
        {
            sb.AppendLine("全局与生成的 link.xml 证据");
            sb.AppendLine("────────────────────────────────────────");
            if (preservations.Count == 0)
            {
                sb.AppendLine("  （未发现 Framework Module 目录之外的规则）");
            }
            else
            {
                foreach (var group in preservations.GroupBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase))
                {
                    string origin = group.First().IsGenerated ? "生成物" : "项目/第三方";
                    sb.AppendLine($"  • {group.Key}（{origin}，{group.Count()} 条）");
                    foreach (var rule in group)
                    {
                        string condition = rule.IsUnconditional ? "无条件根" : "仅被引用时生效";
                        sb.AppendLine($"      {rule.AssemblyName} · {rule.Scope} · {condition}");
                    }
                }
            }
            sb.AppendLine("  这些规则不自动归罪于某个 Framework Module；生成物应修改来源配置后重新 Generate，第三方规则应在升级边界内处理。");
            sb.AppendLine();
        }

        private static void AppendHotUpdateDeployment(
            StringBuilder sb,
            HotUpdateDeploymentEvidence evidence)
        {
            sb.AppendLine("热更派生证据（只读）");
            sb.AppendLine("────────────────────────────────────────");
            if (evidence == null || !evidence.ProfileAvailable)
            {
                sb.AppendLine("  " + (evidence?.Note ?? "没有可用的 FrameworkHotUpdateProfile。"));
                sb.AppendLine();
                return;
            }
            if (!evidence.InspectionAvailable)
            {
                sb.AppendLine("  ⚠ " + evidence.Note);
                sb.AppendLine();
                return;
            }

            sb.AppendLine("  " + evidence.Note);
            sb.AppendLine("  " + evidence.SettingsMessage);
            sb.AppendLine("  " + evidence.GenerationMessage);
            sb.AppendLine("  " + evidence.StagedMessage);
            sb.AppendLine("  注：中转清单一致只证明结构与当前派生输入相符、所列文件存在；" +
                          "不证明 DLL 内容相对源码新鲜，也不代表 YooAsset bundle 或 CDN 已部署。 ");
            sb.AppendLine();
        }

        private static void AppendProfile(StringBuilder sb, AuditProfile profile)
        {
            sb.AppendLine(profile.Title);
            sb.AppendLine("────────────────────────────────────────");
            AppendFootprint(sb, profile, indent: "  ");
            sb.AppendLine();
        }

        private static void AppendFootprint(StringBuilder sb, AuditProfile profile, string indent)
        {
            var footprint = profile.Footprint;
            sb.AppendLine(indent + "适用：" + profile.Description);
            sb.AppendLine(indent + "入口：" +
                          (profile.Roots.Length == 0 ? "（无）" : string.Join(", ", profile.Roots)));
            sb.AppendLine(indent + "Framework：" +
                          (footprint.FrameworkAssemblies.Count == 0
                              ? "（无）"
                              : string.Join(", ", footprint.FrameworkAssemblies)));
            if (footprint.ProjectAssemblies.Count > 0)
                sb.AppendLine(indent + "项目程序集：" + string.Join(", ", footprint.ProjectAssemblies));
            sb.AppendLine(indent + $"原始托管字节：Framework {FormatBytes(footprint.FrameworkBytes)}" +
                          (footprint.ProjectBytes > 0 ? $" + 项目 {FormatBytes(footprint.ProjectBytes)}" : string.Empty) +
                          $" + 外部依赖 {FormatBytes(footprint.ExternalBytes)}");
            if (footprint.ExternalAssemblies.Count > 0)
            {
                string external = string.Join(", ", footprint.ExternalAssemblies
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key} {FormatBytes(pair.Value)}"));
                sb.AppendLine(indent + "外部依赖：" + external);
            }
            if (footprint.UnresolvedAssemblies.Count > 0)
                sb.AppendLine(indent + "⚠ 无法定位程序集文件，闭包与字节数可能不完整：" +
                              string.Join(", ", footprint.UnresolvedAssemblies));
        }

        private static Footprint Measure(Snapshot snapshot, IEnumerable<string> roots)
        {
            string[] ReadExternal(string name)
            {
                return snapshot.ReferencePaths.TryGetValue(name, out string path)
                    ? ReadAssemblyReferences(path)
                    : Array.Empty<string>();
            }

            var reachable = ComputeReachableAssemblies(
                snapshot.Assemblies, roots, ReadExternal, name => IsPlatformReference(snapshot, name));
            var footprint = new Footprint();
            foreach (string name in reachable)
            {
                if (IsPlatformReference(snapshot, name)) continue;
                if (snapshot.Assemblies.TryGetValue(name, out var info))
                {
                    if (IsFrameworkAssembly(name))
                    {
                        footprint.FrameworkAssemblies.Add(name);
                        footprint.FrameworkBytes += info.OutputBytes;
                    }
                    else if (IsProjectAssembly(info.AsmdefPath))
                    {
                        footprint.ProjectAssemblies.Add(name);
                        footprint.ProjectBytes += info.OutputBytes;
                    }
                    else
                    {
                        AddExternal(footprint, name, info.OutputBytes);
                    }
                    continue;
                }

                bool resolved = snapshot.ReferencePaths.TryGetValue(name, out string path) && File.Exists(path);
                long bytes = resolved ? new FileInfo(path).Length : 0L;
                if (!resolved) footprint.UnresolvedAssemblies.Add(name);
                AddExternal(footprint, name, bytes);
            }
            return footprint;
        }

        private static void AddExternal(Footprint footprint, string name, long bytes)
        {
            if (footprint.ExternalAssemblies.ContainsKey(name)) return;
            footprint.ExternalAssemblies[name] = bytes;
            footprint.ExternalBytes += bytes;
        }

        private static void AppendDeletionTests(StringBuilder sb, IReadOnlyCollection<DeletionCheck> checks)
        {
            sb.AppendLine("删除检查（真实元数据引用闭包）");
            sb.AppendLine("────────────────────────────────────────");
            foreach (var check in checks)
                AppendDeletionResult(sb, check.Name, check.Passed);
            sb.AppendLine("  注：通过只证明程序集依赖方向成立，不证明最终玩家包已达到体积预算。");
        }

        private static void AppendDeletionResult(StringBuilder sb, string name, bool passed)
            => sb.AppendLine($"  {(passed ? "✓" : "✗")} {name}");

        private static string[] BuildRecommendations(AuditResult result)
        {
            var recommendations = new List<string>();
            if (result.DependencyIssues.Length > 0)
                recommendations.Add("先把隐式外部引用补进对应 asmdef；否则编辑器能编译，不代表模块真的能独立取舍。");
            if (!result.AllRuntimeModulesOptIn)
                recommendations.Add("仍有 Runtime Module 开启 autoReferenced。轻量项目会更难看清是谁把它带进来，建议先改成显式引用。");
            if (result.HasUnresolvedAssemblies)
                recommendations.Add("有程序集文件无法定位，本次闭包和字节数不完整；先修编译或热更清单，再比较体积。");
            if (result.DeletionChecks.Any(check => !check.Passed))
                recommendations.Add("至少一条删除检查失败，说明可选模块发生了反向耦合；先修依赖方向，再讨论包体优化。");
            if (result.HasRetentionWarnings)
            {
                string targets = string.Join("、", result.UnconditionalModulePreservations
                    .Select(rule => $"{rule.OwnerModuleName} → {rule.AssemblyName}")
                    .Distinct(StringComparer.Ordinal));
                recommendations.Add("发现可选 Module 目录下的无条件 link.xml 保留：" + targets +
                                    "。这不一定是错误，但会让“没调用就自动消失”失效；应结合反射/热更需求逐条验证。 ");
            }
            if (result.HasHotUpdateViolations)
            {
                string violations = string.Join("；", result.ModuleStatuses
                    .Where(status => status.HasHotUpdateViolation)
                    .Select(status => status.Module.Name + "（AOT）→ " +
                                      string.Join("、", status.HotUpdateDependencies) + "（热更）"));
                recommendations.Add("发现当前热更 Profile 的非法引用边：" + violations +
                                    "。先恢复合法闭包或让对应 Module 退出 Player 编译图，修正前不要同步或出包。 ");
            }
            if (result.HasHotUpdateDeploymentWarnings)
            {
                var evidence = result.HotUpdateDeployment;
                if (!evidence.ProfileAvailable)
                    recommendations.Add(evidence.Note + " 打开“热更配置”以创建或定位单一 Profile。 ");
                else
                {
                    if (evidence.ProfileCount > 1)
                        recommendations.Add(evidence.Note + " 请合并为唯一 Profile，避免不同入口读取不同期望。 ");
                    if (!evidence.InspectionAvailable)
                        recommendations.Add("热更 Profile 存在，但无法读取派生证据：" + evidence.Note);
                    else
                    {
                        if (!evidence.SettingsAvailable || !evidence.SettingsMatch)
                            recommendations.Add(evidence.SettingsMessage);
                        if (evidence.GenerationRequired && !evidence.GenerationFresh)
                            recommendations.Add(evidence.GenerationMessage);
                        if ((evidence.StagingRequired && !evidence.StagedManifestAvailable) ||
                            (evidence.StagedManifestExists && !evidence.StagedManifestMatches))
                            recommendations.Add(evidence.StagedMessage);
                    }
                }
            }

            if (result.IsHealthy)
            {
                recommendations.Add("当前 asmdef 边界允许业务从“只用核心”开始，再按需增加 Module。注意热更不是独立开关：Core 热更时，仍参与 Player 编译且引用 Core 的 Module 也必须热更；强裁剪要把 Module 退出编译图与 Profile 清理作为同一次结构变更。 ");

                var coreExternal = result.CommonProfiles[0].Footprint.ExternalAssemblies;
                var uiOnlyLargest = result.CommonProfiles
                    .Skip(1)
                    .SelectMany(profile => profile.Footprint.ExternalAssemblies)
                    .Where(pair => !coreExternal.ContainsKey(pair.Key))
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(uiOnlyLargest.Key))
                    recommendations.Add($"单 UI 后端组合新增的最大外部 DLL 是 {uiOnlyLargest.Key}（{FormatBytes(uiOnlyLargest.Value)} 原始托管体积）。若目标是 Web / 小游戏，优先用真实构建验证它。 ");

                long collectionBytes = result.CommonProfiles
                    .Skip(1)
                    .SelectMany(profile => profile.Footprint.ExternalAssemblies)
                    .Where(pair => pair.Key.StartsWith("ObservableCollections", StringComparison.Ordinal))
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .Sum(group => group.First().Value);
                if (collectionBytes > 0)
                    recommendations.Add($"列表集合相关 DLL 原始合计约 {FormatBytes(collectionBytes)}。先看目标平台 BuildReport，再决定是否值得把列表绑定单独拆成 Module。 ");
            }

            recommendations.Add("原始 DLL 大小只用于发现候选；IL2CPP 裁剪、AOT、压缩后的最终差异必须看目标平台 Player BuildReport。 ");
            return recommendations.ToArray();
        }

        private static bool IsRelevantExternalReference(Snapshot snapshot, string reference)
        {
            if (IsPlatformReference(snapshot, reference) || IsFrameworkAssembly(reference)) return false;
            if (snapshot.Assemblies.TryGetValue(reference, out var info))
                return !IsProjectAssembly(info.AsmdefPath);
            return snapshot.ReferencePaths.ContainsKey(reference);
        }

        private static Dictionary<string, string> BuildReferencePathMap(
            UnityEditor.Compilation.Assembly[] assemblies)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                string output = FullPath(assembly.outputPath);
                if (File.Exists(output)) result[assembly.name] = output;
            }

            foreach (var assembly in assemblies)
            foreach (string reference in assembly.compiledAssemblyReferences ?? Array.Empty<string>())
            {
                string path = FullPath(reference);
                if (!File.Exists(path)) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                if (!result.ContainsKey(name)) result[name] = path;
            }
            return result;
        }

        private static string[] ReadAssemblyReferences(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("程序集路径不能为空。", nameof(path));

            string fullPath = FullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("无法读取程序集元数据：文件不存在。", fullPath);

            var file = new FileInfo(fullPath);
            lock (AssemblyReferenceCacheLock)
            {
                if (AssemblyReferenceCache.TryGetValue(fullPath, out var cached) &&
                    cached.Length == file.Length &&
                    cached.LastWriteUtc == file.LastWriteTimeUtc)
                    return cached.References;
            }

            try
            {
#pragma warning disable 618
                // ReflectionOnlyLoadFrom 会一直锁住 Library/ScriptAssemblies 下的 DLL/PDB，下一轮 Unity 编译
                // 就会在 Windows 报“用户映射区域”而失败。读入字节再加载，保留元数据能力但不占用源文件。
                var assembly = System.Reflection.Assembly.ReflectionOnlyLoad(File.ReadAllBytes(fullPath));
#pragma warning restore 618
                string[] references = assembly.GetReferencedAssemblies()
                    .Select(reference => reference.Name)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                // 反射只读程序集会留在当前 AppDomain 到下次域重载；一次报告包含多个组合，
                // 缓存引用表可避免重复刷新时不断为同一份 DLL 增加只读 Assembly 实例。
                lock (AssemblyReferenceCacheLock)
                    AssemblyReferenceCache[fullPath] = new AssemblyReferenceCacheEntry(
                        file.Length, file.LastWriteTimeUtc, references);
                return references;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法读取程序集元数据：{fullPath}", ex);
            }
        }

        private static HotUpdateDeploymentEvidence ReadHotUpdateEvidence()
        {
            var evidence = new HotUpdateDeploymentEvidence();
            Type profileType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Game.Framework.Build.FrameworkHotUpdateProfile", false))
                .FirstOrDefault(type => type != null);
            if (profileType == null)
            {
                evidence.Note = "未安装热更构建 Module；按纯 AOT 理解。";
                return evidence;
            }
            evidence.BuildModuleAvailable = true;

            string[] guids = AssetDatabase.FindAssets("t:" + profileType.Name);
            evidence.ProfileCount = guids.Length;
            if (guids.Length == 0)
            {
                evidence.Note = "未找到 FrameworkHotUpdateProfile；构建菜单会创建默认热更档位。若目标是纯 AOT，也应创建空 Profile 作为明确的单一真源。";
                return evidence;
            }
            evidence.ProfileAvailable = true;

            string path = AssetDatabase.GUIDToAssetPath(guids.OrderBy(guid => AssetDatabase.GUIDToAssetPath(guid),
                StringComparer.Ordinal).First());
            evidence.ProfilePath = path;
            var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
            var property = profileType.GetProperty("HotUpdateAssemblyNames", BindingFlags.Instance | BindingFlags.Public);
            if (profile == null || property?.GetValue(profile) is not IEnumerable<string> names)
            {
                evidence.Note = $"无法读取热更 Profile：{path}";
                return evidence;
            }

            evidence.ProfileAssemblies = names.Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string multiple = guids.Length > 1 ? $"；发现 {guids.Length} 个 Profile，仅检查排序第一项" : string.Empty;
            evidence.Note = evidence.ProfileAssemblies.Length == 0
                ? $"{path}：Profile 期望纯 AOT{multiple}。"
                : $"{path}：Profile 期望 {evidence.ProfileAssemblies.Length} 个热更入口{multiple}。";

            Type builderType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Game.Framework.Build.FrameworkHotUpdateBuilder", false))
                .FirstOrDefault(type => type != null);
            MethodInfo inspect = builderType?.GetMethod(
                "InspectEvidence",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (inspect == null)
            {
                evidence.Note += " 当前 Build Module 未提供派生证据检查。";
                return evidence;
            }

            try
            {
                object raw = inspect.Invoke(null, new[] { profile });
                ApplyHotUpdateInspection(evidence, raw);
            }
            catch (TargetInvocationException ex)
            {
                evidence.Note += " 派生证据检查失败：" + (ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                evidence.Note += " 派生证据检查失败：" + ex.Message;
            }
            return evidence;
        }

        internal static void ApplyHotUpdateInspection(HotUpdateDeploymentEvidence target, object raw)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (raw == null) return;

            target.InspectionAvailable = true;
            target.ProfileAssemblies = ReadEvidenceMember(raw, "ProfileAssemblies", target.ProfileAssemblies);
            target.SettingsAssemblies = ReadEvidenceMember(raw, "HybridClrSettingsAssemblies",
                Array.Empty<string>());
            target.LegacySettingsAssemblies = ReadEvidenceMember(raw, "HybridClrLegacyAssemblies",
                Array.Empty<string>());
            target.SettingsAvailable = ReadEvidenceMember(raw, "SettingsAvailable", false);
            target.SettingsMatch = ReadEvidenceMember(raw, "SettingsMatch", false);
            target.SettingsMessage = ReadEvidenceMember(raw, "SettingsMessage", string.Empty);
            target.GenerationRequired = ReadEvidenceMember(raw, "GenerationRequired", false);
            target.GenerationFresh = ReadEvidenceMember(raw, "GenerationFresh", false);
            target.GenerationMessage = ReadEvidenceMember(raw, "GenerationMessage", string.Empty);
            target.StagedManifestAvailable = ReadEvidenceMember(raw, "StagedManifestAvailable", false);
            target.StagingRequired = ReadEvidenceMember(raw, "StagingRequired",
                target.ProfileAssemblies.Length > 0);
            target.StagedManifestExists = ReadEvidenceMember(raw, "StagedManifestExists",
                target.StagedManifestAvailable);
            target.StagedManifestMatches = ReadEvidenceMember(raw, "StagedManifestMatches", false);
            target.StagedVersion = ReadEvidenceMember(raw, "StagedVersion", string.Empty);
            target.StagedAssemblies = ReadEvidenceMember(raw, "StagedAssemblies", Array.Empty<string>());
            target.ExpectedAotMetadataDlls = ReadEvidenceMember(raw, "ExpectedAotMetadataDlls",
                Array.Empty<string>());
            target.StagedAotMetadataDlls = ReadEvidenceMember(raw, "StagedAotMetadataDlls",
                Array.Empty<string>());
            target.MissingStagedFiles = ReadEvidenceMember(raw, "MissingStagedFiles", Array.Empty<string>());
            target.UnexpectedStagedFiles = ReadEvidenceMember(raw, "UnexpectedStagedFiles",
                Array.Empty<string>());
            target.InvalidStagedEntries = ReadEvidenceMember(raw, "InvalidStagedEntries",
                Array.Empty<string>());
            target.StagedMessage = ReadEvidenceMember(raw, "StagedMessage", string.Empty);
        }

        private static T ReadEvidenceMember<T>(object source, string name, T fallback)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = source.GetType();
            object value = type.GetField(name, flags)?.GetValue(source) ??
                           type.GetProperty(name, flags)?.GetValue(source);
            return value is T typed ? typed : fallback;
        }

        private static LinkerPreservation[] ReadLinkerPreservations(
            IReadOnlyDictionary<string, AssemblyInfo> assemblies)
        {
            var result = new List<LinkerPreservation>();
            foreach (FrameworkModuleSourceCatalog.SourceLocation source in
                     FrameworkModuleSourceCatalog.EnumerateFiles("link.xml"))
            {
                string owner = ResolveLinkerOwner(source.PhysicalPath, assemblies.Values);
                try
                {
                    LinkerPreservation[] parsed = ParseLinkerPreservations(
                        File.ReadAllText(source.PhysicalPath), source.AssetPath, owner);
                    foreach (LinkerPreservation rule in parsed)
                    {
                        rule.SourcePackageName = source.PackageName;
                        rule.SourcePackageVersion = source.PackageVersion;
                        rule.SourcePackageId = source.PackageId;
                    }
                    result.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"无法解析 UnityLinker 配置：{source.AssetPath}", ex);
                }
            }
            return result
                .OrderBy(rule => rule.Path, StringComparer.Ordinal)
                .ThenBy(rule => rule.AssemblyName, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 只在 link.xml 位于唯一且最深的 Module 源码目录时归属该 Module；package 根或同目录多
        /// asmdef 保持全局/Package 证据，避免用枚举顺序猜 owner。
        /// </summary>
        internal static string ResolveLinkerOwner(
            string physicalPath,
            IEnumerable<AssemblyInfo> assemblies)
        {
            var matches = (assemblies ?? Array.Empty<AssemblyInfo>())
                .Where(info => info != null && info.IsFrameworkRuntime &&
                               !string.IsNullOrWhiteSpace(info.SourceDirectory))
                .Where(info => FrameworkModuleSourceCatalog.IsPhysicalPathInside(
                    physicalPath, info.SourceDirectory))
                .Select(info => new
                {
                    info.Name,
                    Directory = Path.GetFullPath(info.SourceDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                })
                .ToArray();
            if (matches.Length == 0) return string.Empty;
            int deepest = matches.Max(item => item.Directory.Length);
            string[] owners = matches
                .Where(item => item.Directory.Length == deepest)
                .Select(item => item.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return owners.Length == 1 ? owners[0] : string.Empty;
        }

        /// <summary>
        /// 读取全部项目与 Package asmdef 的显式引用，供物理删除计划使用。它与 Player DLL 的真实消费
        /// 是两种证据：不进入 Player 的程序集不会保留玩家代码，却仍会在被引用 Module 删除后阻塞编译。
        /// </summary>
        private static Dictionary<string, string[]> ReadDeclaredConsumers()
        {
            var consumers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string path in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)))
            {
                AsmdefJson dto = ReadAsmdef(path);
                if (dto == null || string.IsNullOrWhiteSpace(dto.name)) continue;
                foreach (string dependency in GetDeclaredReferences(dto))
                {
                    if (string.IsNullOrWhiteSpace(dependency) ||
                        dependency.Equals(dto.name, StringComparison.Ordinal))
                        continue;
                    if (!consumers.TryGetValue(dependency, out var names))
                    {
                        names = new HashSet<string>(StringComparer.Ordinal);
                        consumers.Add(dependency, names);
                    }
                    names.Add(dto.name);
                }
            }

            return consumers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        }

        private static bool IsTrue(string value) =>
            value != null && (value.Equals("1", StringComparison.Ordinal) ||
                              value.Equals("true", StringComparison.OrdinalIgnoreCase));

        private static string ToProfileKey(string assemblyName)
        {
            var sb = new StringBuilder(assemblyName.Length);
            foreach (char character in assemblyName)
                sb.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
            return sb.ToString().Trim('-');
        }

        private static string FriendlyModuleName(string assemblyName)
        {
            string prefix = CoreAssemblyName + ".";
            return assemblyName.StartsWith(prefix, StringComparison.Ordinal)
                ? assemblyName.Substring(prefix.Length)
                : assemblyName;
        }

        private static AsmdefJson ReadAsmdef(string path)
        {
            if (!FrameworkModuleSourceCatalog.TryResolve(
                    path,
                    out FrameworkModuleSourceCatalog.SourceLocation source,
                    out _) ||
                !File.Exists(source.PhysicalPath))
                return null;
            try
            {
                return JsonUtility.FromJson<AsmdefJson>(File.ReadAllText(source.PhysicalPath));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string[] GetDeclaredReferences(AsmdefJson dto)
        {
            if (dto == null) return Array.Empty<string>();
            return (dto.references ?? Array.Empty<string>())
                .Concat(dto.precompiledReferences ?? Array.Empty<string>())
                .Select(NormalizeDeclaredReference)
                .Where(reference => !string.IsNullOrEmpty(reference))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();
        }

        private static string NormalizeDeclaredReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
            reference = reference.Trim();
            if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            {
                string asmdefPath = AssetDatabase.GUIDToAssetPath(reference.Substring(5));
                var dto = ReadAsmdef(asmdefPath);
                return dto?.name ?? reference;
            }
            // asmdef 的程序集名本来就常含点（R3.Unity / Sirenix.Serialization）；
            // Path.GetFileNameWithoutExtension 会把最后一段误当扩展名，只对 precompiledReferences 的 .dll 去后缀。
            return reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? reference.Substring(0, reference.Length - 4)
                : reference;
        }

        private static bool IsEditorConstrained(string assemblyName)
        {
            string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            var dto = ReadAsmdef(path);
            if (dto?.defineConstraints == null) return false;
            return dto.defineConstraints.Any(constraint => EditorOnlyConstraints.Contains(constraint));
        }

        private static bool IsFrameworkAssembly(string name)
            => name != null && (name.Equals(CoreAssemblyName, StringComparison.Ordinal) ||
                                name.StartsWith(CoreAssemblyName + ".", StringComparison.Ordinal));

        private static bool IsProjectAssembly(string asmdefPath)
        {
            if (string.IsNullOrEmpty(asmdefPath)) return true; // Assembly-CSharp 等项目预定义程序集。
            string normalized = asmdefPath.Replace('\\', '/');
            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlatformReference(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name.Equals("mscorlib", StringComparison.Ordinal) ||
                   name.Equals("netstandard", StringComparison.Ordinal) ||
                   name.Equals("System", StringComparison.Ordinal) ||
                   name.StartsWith("System.", StringComparison.Ordinal) ||
                   name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEditor.", StringComparison.Ordinal);
        }

        private static bool IsPlatformReference(Snapshot snapshot, string name)
        {
            if (string.IsNullOrEmpty(name) ||
                name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor.", StringComparison.Ordinal))
                return true;

            // NuGet 也会提供 System.* DLL（例如 Protobuf 依赖的 Unsafe）。按名字会误当 BCL，
            // 因此只在引用来自 Unity 安装目录时判为平台程序集；项目 Packages 下的同名 DLL 仍要计体积。
            if (snapshot.ReferencePaths.TryGetValue(name, out string path))
            {
                string normalized = path.Replace('\\', '/');
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(projectRoot) &&
                    normalized.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return IsPlatformReference(name);
        }

        private static string FullPath(string path)
            => string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KiB";
            return (bytes / (1024d * 1024d)).ToString("0.00") + " MiB";
        }

        [Serializable]
        private sealed class AsmdefJson
        {
            public string name;
            public string[] references;
            public string[] precompiledReferences;
            public bool autoReferenced = true;
            public string[] defineConstraints;
        }

        private sealed class AssemblyReferenceCacheEntry
        {
            internal readonly long Length;
            internal readonly DateTime LastWriteUtc;
            internal readonly string[] References;

            internal AssemblyReferenceCacheEntry(long length, DateTime lastWriteUtc, string[] references)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                References = references;
            }
        }
    }
}
