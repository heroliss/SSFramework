using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 从当前 Player 编译图、asmdef 声明与当前已编译 DLL 快照生成 Framework Module 裁剪证据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CompilationPipeline.GetAssemblies(AssembliesType)"/> 给出参与当前 Player 编译的程序集；
    /// <c>autoReferenced:false</c> 只阻止 Assembly-CSharp 等预定义程序集隐式引用该 asmdef，既不让它退出
    /// 编译图，也不会凭空制造或消除 DLL 元数据引用。本审计分别保留 asmdef 声明闭包与当前 DLL 的
    /// <see cref="System.Reflection.AssemblyName"/> 引用闭包，避免把任一层冒充最终保留结果。
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
        internal const string BootAssemblyName = "Game.Framework.Boot";

        private static readonly Dictionary<string, AssemblyReferenceCacheEntry> AssemblyReferenceCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AssemblyReferenceCacheLock = new();

        /// <summary>一次同步采集的阶段耗时；用于定位重型输入，不参与审计结论。</summary>
        internal sealed class CaptureTimings
        {
            internal double InputSnapshotSeconds;
            internal double PlayerGraphSeconds;
            internal double DependencyEvidenceSeconds;
            internal double HotUpdateEvidenceSeconds;
            internal double LinkerEvidenceSeconds;
            internal double TotalSeconds;
        }

        /// <summary>
        /// 仅在一次 <see cref="Capture()"/> 内存活的 Unity 输入快照。AssetDatabase、PluginImporter 与编译图
        /// 各读取一次，使 asmdef、DLL 与 linker 证据基于同一轮可见输入。
        /// </summary>
        private sealed class CaptureInputs
        {
            internal string[] AssetPaths = Array.Empty<string>();
            internal PluginImporter[] PluginImporters = Array.Empty<PluginImporter>();
            internal UnityEditor.Compilation.Assembly[] PlayerAssemblies =
                Array.Empty<UnityEditor.Compilation.Assembly>();
            internal UnityEditor.Compilation.Assembly[] EditorAssemblies =
                Array.Empty<UnityEditor.Compilation.Assembly>();
            internal BuildTarget[] BuildTargets = Array.Empty<BuildTarget>();
        }

        internal sealed class AssemblyInfo
        {
            internal string Name;
            internal string AsmdefPath;
            internal string SourceDirectory;
            internal string PackageName;
            internal string PackageVersion;
            internal string PackageId;
            internal FrameworkModuleSourceCatalog.SourceKind SourceKind;
            internal bool HasPackageDirectness;
            internal bool IsDirectPackageDependency;
            internal string OutputPath;
            internal long OutputBytes;
            internal bool AutoReferenced;
            internal bool OverrideReferences;
            /// <summary>asmdef 的 <c>references</c>，只表示对其他 asmdef 程序集的显式边。</summary>
            internal string[] DeclaredReferences = Array.Empty<string>();
            /// <summary>
            /// 仅当 <c>overrideReferences</c> 启用时生效的 <c>precompiledReferences</c>。
            /// 预编译 DLL 名写进 <c>references</c> 不会被当作显式声明。
            /// </summary>
            internal string[] DeclaredPrecompiledReferences = Array.Empty<string>();
            internal string[] ActualReferences = Array.Empty<string>();

            internal bool IsFrameworkRuntime => IsFrameworkAssembly(Name) &&
                                                !Name.Equals(BootAssemblyName, StringComparison.Ordinal);
        }

        internal sealed class Snapshot
        {
            internal readonly Dictionary<string, AssemblyInfo> Assemblies;
            internal readonly Dictionary<string, string> ReferencePaths;
            internal readonly string[] HotUpdateRoots;
            internal readonly string HotUpdateNote;
            internal readonly LinkerPreservation[] LinkerPreservations;
            internal readonly Dictionary<string, string[]> DeclaredConsumersByDependency;
            internal readonly DeclaredConsumerEvidence[] DeclaredConsumers;
            internal readonly ActualConsumerEvidence[] ActualConsumers;
            internal readonly Dictionary<string, DependencySource> DependencySources;
            internal readonly EvidenceIssue[] DependencyEvidenceIssues;
            internal readonly HotUpdateDeploymentEvidence HotUpdateDeployment;

            internal Snapshot(
                Dictionary<string, AssemblyInfo> assemblies,
                Dictionary<string, string> referencePaths,
                string[] hotUpdateRoots,
                string hotUpdateNote,
                LinkerPreservation[] linkerPreservations = null,
                Dictionary<string, string[]> declaredConsumersByDependency = null,
                HotUpdateDeploymentEvidence hotUpdateDeployment = null,
                DeclaredConsumerEvidence[] declaredConsumers = null,
                Dictionary<string, DependencySource> dependencySources = null,
                ActualConsumerEvidence[] actualConsumers = null,
                EvidenceIssue[] dependencyEvidenceIssues = null)
            {
                Assemblies = assemblies;
                ReferencePaths = referencePaths;
                HotUpdateRoots = hotUpdateRoots;
                HotUpdateNote = hotUpdateNote;
                LinkerPreservations = linkerPreservations ?? Array.Empty<LinkerPreservation>();
                DeclaredConsumersByDependency = declaredConsumersByDependency ??
                                                        new Dictionary<string, string[]>(StringComparer.Ordinal);
                DeclaredConsumers = declaredConsumers ?? Array.Empty<DeclaredConsumerEvidence>();
                ActualConsumers = actualConsumers ?? Array.Empty<ActualConsumerEvidence>();
                DependencySources = dependencySources ?? new Dictionary<string, DependencySource>(StringComparer.Ordinal);
                DependencyEvidenceIssues = dependencyEvidenceIssues ?? Array.Empty<EvidenceIssue>();
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

        internal enum DeclaredReferenceKind
        {
            AssemblyDefinition,
            PrecompiledAssembly,
        }

        internal enum ConsumerPlatformScope
        {
            Player,
            Editor,
            Tests,
            Mixed,
            Unknown,
        }

        /// <summary>
        /// 审计结论的行动级别。已知的无条件 linker 根属于成本说明，不会单独把结构检查降级为警告；
        /// 只有证据不完整或派生状态漂移才要求确认，结构契约破坏则视为错误。
        /// </summary>
        internal enum AuditOutcome
        {
            Clear,
            Advisory,
            Warning,
            Error,
        }

        /// <summary>
        /// 完整 asmdef 图中的一条声明边。它不证明当前 DLL 已调用目标，但会在删除目标后形成编译阻塞。
        /// </summary>
        internal sealed class DeclaredConsumerEvidence
        {
            internal string DependencyAssemblyName = string.Empty;
            internal string ConfiguredReference = string.Empty;
            internal DeclaredReferenceKind ReferenceKind;
            internal string ConsumerAssemblyName = string.Empty;
            internal string ConsumerAsmdefPath = string.Empty;
            internal FrameworkModuleSourceCatalog.SourceKind ConsumerSourceKind;
            internal string ConsumerPackageName = string.Empty;
            internal ConsumerPlatformScope PlatformScope;

            internal bool ConsumerIsFramework => IsFrameworkAssembly(ConsumerAssemblyName);
            internal bool ConsumerIsProjectAsset =>
                ConsumerSourceKind == FrameworkModuleSourceCatalog.SourceKind.ProjectAssets;
            internal bool ConsumerIsEditorOnly => PlatformScope is ConsumerPlatformScope.Editor or
                                                  ConsumerPlatformScope.Tests;
        }

        /// <summary>当前已编译 DLL 快照中的一条直接元数据引用，Player 与 Editor 变体分开标记。</summary>
        internal sealed class ActualConsumerEvidence
        {
            internal string DependencyAssemblyName = string.Empty;
            internal string ConsumerAssemblyName = string.Empty;
            internal string ConsumerAsmdefPath = string.Empty;
            internal FrameworkModuleSourceCatalog.SourceKind ConsumerSourceKind;
            internal string ConsumerPackageName = string.Empty;
            internal ConsumerPlatformScope PlatformScope;

            internal bool ConsumerIsFramework => IsFrameworkAssembly(ConsumerAssemblyName);
            internal bool ConsumerIsProjectAsset =>
                ConsumerSourceKind == FrameworkModuleSourceCatalog.SourceKind.ProjectAssets;
            internal bool ConsumerIsEditorOnly => PlatformScope is ConsumerPlatformScope.Editor or
                                                  ConsumerPlatformScope.Tests;
        }

        internal sealed class EvidenceIssue
        {
            internal string Code = string.Empty;
            internal string Message = string.Empty;
            /// <summary>
            /// 可选的程序集作用域。为空表示扫描级全局问题；有值时只收紧包含该程序集的依赖组。
            /// </summary>
            internal string SubjectAssemblyName = string.Empty;

            public override string ToString() => $"[{Code}] {Message}";
        }

        /// <summary>同一逻辑 AssemblyName 的一个物理实现；平台互斥变体不能被静默折叠。</summary>
        internal sealed class DependencySourceVariant
        {
            internal string AssetPath = string.Empty;
            internal string PhysicalPath = string.Empty;
            internal bool HasCompatibilityEvidence;
            internal bool IsEditorCompatible;
            /// <summary>只表示当前 <see cref="EditorUserBuildSettings.activeBuildTarget"/>，不是所有 Player 平台。</summary>
            internal bool IsActiveBuildTargetCompatible;
            internal string[] CompatibleBuildTargets = Array.Empty<string>();
        }

        /// <summary>某个可引用程序集的来源身份；未知来源保持 Unknown，不按名称猜第三方归属。</summary>
        internal sealed class DependencySource
        {
            internal string AssemblyName = string.Empty;
            internal string AssetPath = string.Empty;
            internal string PhysicalPath = string.Empty;
            internal string PackageName = string.Empty;
            internal string PackageVersion = string.Empty;
            internal string PackageId = string.Empty;
            internal FrameworkModuleSourceCatalog.SourceKind SourceKind =
                FrameworkModuleSourceCatalog.SourceKind.UnknownPackage;
            internal bool HasPackageDirectness;
            internal bool IsDirectPackageDependency;
            internal bool IsPrecompiledAssembly;
            internal bool IsExternal;
            internal DependencySourceVariant[] Variants = Array.Empty<DependencySourceVariant>();

            internal bool IsKnown => !string.IsNullOrWhiteSpace(AssetPath) ||
                                     !string.IsNullOrWhiteSpace(PhysicalPath) || Variants.Length > 0;

            internal IEnumerable<string> AllAssetPaths => Variants.Select(item => item.AssetPath)
                .Append(AssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            internal IEnumerable<string> AllPhysicalPaths => Variants.Select(item => item.PhysicalPath)
                .Append(PhysicalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        internal enum ExternalDependencyRole
        {
            BaseRuntime,
            OptionalRuntime,
            SharedRuntime,
            EditorTool,
            ProjectConsumer,
            Unknown,
        }

        internal enum ExternalDependencyRemovalState
        {
            RequiredByCore,
            RemoveWithOptionalModuleCandidate,
            RemoveWithEditorToolCandidate,
            SharedConsumerMigrationRequired,
            ProjectConsumerMigrationRequired,
            ReviewRequired,
        }

        /// <summary>
        /// 以实际 Package（或单个 Assets DLL / Unknown 程序集）为单位聚合的外部依赖证据。
        /// 安装来源、当前编译快照消费、完整 asmdef 声明和 what-if Profile 影响互不替代。
        /// </summary>
        internal sealed class ExternalDependencyEvidence
        {
            internal string Key = string.Empty;
            internal string DisplayName = string.Empty;
            internal string PackageName = string.Empty;
            internal string PackageVersion = string.Empty;
            internal string PackageId = string.Empty;
            internal FrameworkModuleSourceCatalog.SourceKind SourceKind;
            internal bool HasPackageDirectness;
            internal bool IsDirectPackageDependency;
            internal DependencySource[] Assemblies = Array.Empty<DependencySource>();
            internal DeclaredConsumerEvidence[] DeclaredConsumers = Array.Empty<DeclaredConsumerEvidence>();
            internal ActualConsumerEvidence[] ActualConsumers = Array.Empty<ActualConsumerEvidence>();
            internal ActualConsumerEvidence[] Introducers = Array.Empty<ActualConsumerEvidence>();
            internal EvidenceIssue[] EvidenceIssues = Array.Empty<EvidenceIssue>();
            internal string[] DirectProfileKeys = Array.Empty<string>();
            internal string[] TransitiveProfileKeys = Array.Empty<string>();
            internal string[] FrameworkConsumers = Array.Empty<string>();
            internal string[] ProjectConsumers = Array.Empty<string>();
            internal ExternalDependencyRole Role;
            internal ExternalDependencyRemovalState RemovalState;
            internal string Summary = string.Empty;
            internal string[] RemovalSteps = Array.Empty<string>();
            internal string[] VerificationSteps = Array.Empty<string>();
            internal SortedDictionary<string, long> ProfileRawBytesByKey =
                new(StringComparer.Ordinal);
            internal long InstalledBinaryBytes;
            internal bool HasInstalledBinaryMeasurement;

            internal bool HasProfileMeasurement => ProfileRawBytesByKey.Count > 0;
            internal long MaxProfileRawBytes => ProfileRawBytesByKey.Count == 0
                ? 0
                : ProfileRawBytesByKey.Values.Max();

            internal bool TryGetProfileRawBytes(string profileKey, out long bytes) =>
                ProfileRawBytesByKey.TryGetValue(profileKey, out bytes);

            internal bool HasUnknownSource => SourceKind ==
                                              FrameworkModuleSourceCatalog.SourceKind.UnknownPackage;
            internal bool HasEvidenceGaps => HasUnknownSource || EvidenceIssues.Length > 0;
            internal string[] AffectedProfileKeys => DirectProfileKeys.Concat(TransitiveProfileKeys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>记录“当前 DLL 快照存在引用，但 asmdef 没有直接声明”的模块依赖。</summary>
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
            internal bool PredefinedAutoReferenceDisabled;
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
        /// 热更 Profile 的只读派生证据。HybridCLR 热更构建 Module 仍是具体设置、Generate 与中转清单的 owner；
        /// 通用审计经反射读取，保持删除该可选 Module 后仍可编译。
        /// </summary>
        internal sealed class HotUpdateDeploymentEvidence
        {
            internal bool HotUpdateBuildModuleAvailable;
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

            internal bool RequiresAttention => HotUpdateBuildModuleAvailable &&
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
            internal ExternalDependencyEvidence[] ExternalDependencies = Array.Empty<ExternalDependencyEvidence>();
            internal EvidenceIssue[] DependencyEvidenceIssues = Array.Empty<EvidenceIssue>();
            internal string[] Recommendations = Array.Empty<string>();
            internal bool AllRuntimeModulesHavePredefinedAutoReferenceDisabled;

            internal IEnumerable<AuditProfile> AllProfiles => CommonProfiles
                .Concat(ModuleProfiles)
                .Concat(FullProfile != null ? new[] { FullProfile } : Array.Empty<AuditProfile>())
                .Concat(HotUpdateProfile != null ? new[] { HotUpdateProfile } : Array.Empty<AuditProfile>());

            internal bool HasUnresolvedAssemblies =>
                AllProfiles.Any(profile => profile.Footprint.UnresolvedAssemblies.Count > 0);

            internal bool HasRetentionAdvisories => UnconditionalModulePreservations.Length > 0;
            internal bool HasHotUpdateViolations => ModuleStatuses.Any(status => status.HasHotUpdateViolation);
            internal bool HasHotUpdateDeploymentWarnings => HotUpdateDeployment?.RequiresAttention == true;
            internal bool HasUnknownExternalDependencySources =>
                ExternalDependencies.Any(dependency => dependency.HasUnknownSource);
            internal bool HasDependencyEvidenceGaps => DependencyEvidenceIssues.Length > 0 ||
                                                       ExternalDependencies.Any(dependency =>
                                                           dependency.EvidenceIssues.Length > 0);
            internal int DependencyEvidenceIssueCount => DependencyEvidenceIssues.Length +
                                                          ExternalDependencies.Sum(dependency =>
                                                              dependency.EvidenceIssues.Length);

            internal bool IsHealthy => DependencyIssues.Length == 0 &&
                                       AllRuntimeModulesHavePredefinedAutoReferenceDisabled &&
                                       !HasUnresolvedAssemblies &&
                                       !HasHotUpdateViolations &&
                                       DeletionChecks.All(check => check.Passed);

            internal AuditOutcome Outcome => !IsHealthy
                ? AuditOutcome.Error
                : HasHotUpdateDeploymentWarnings || HasUnknownExternalDependencySources ||
                  HasDependencyEvidenceGaps
                    ? AuditOutcome.Warning
                    : HasRetentionAdvisories
                        ? AuditOutcome.Advisory
                        : AuditOutcome.Clear;

            internal bool RequiresAction => Outcome == AuditOutcome.Warning || Outcome == AuditOutcome.Error;
        }

        internal static Snapshot Capture() => Capture(out _);

        internal static Snapshot Capture(
            out CaptureTimings timings,
            Action<string, float> progress = null)
        {
            timings = new CaptureTimings();
            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();
            progress?.Invoke("建立 Unity 输入快照", 0.02f);
            var inputs = new CaptureInputs
            {
                AssetPaths = AssetDatabase.GetAllAssetPaths(),
                PluginImporters = PluginImporter.GetAllImporters(),
                PlayerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player),
                EditorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor),
                BuildTargets = Enum.GetValues(typeof(BuildTarget))
                    .Cast<BuildTarget>()
                    .Where(target => target != BuildTarget.NoTarget)
                    .GroupBy(target => (int)target)
                    .Select(group => group.First())
                    .ToArray(),
            };
            timings.InputSnapshotSeconds = phase.Elapsed.TotalSeconds;

            phase.Restart();
            progress?.Invoke("读取 Player 编译图与程序集元数据", 0.14f);
            UnityEditor.Compilation.Assembly[] playerAssemblies = inputs.PlayerAssemblies
                .Where(assembly => !IsEditorConstrained(assembly.name))
                .ToArray();

            var referencePaths = BuildReferencePathMap(playerAssemblies);
            Dictionary<string, string> precompiledIdentities = BuildPrecompiledReferenceIdentityMap(
                inputs.PluginImporters, out Dictionary<string, string> pluginIdentitiesByAssetPath);
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
                    SourceKind = asmdefSource?.Kind ?? FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                    HasPackageDirectness = asmdefSource?.HasPackageDirectness ?? false,
                    IsDirectPackageDependency = asmdefSource?.IsDirectPackageDependency ?? false,
                    OutputPath = outputPath,
                    OutputBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L,
                    AutoReferenced = dto?.autoReferenced ?? true,
                    OverrideReferences = dto?.overrideReferences ?? false,
                    DeclaredReferences = GetDeclaredAssemblyReferences(dto),
                    DeclaredPrecompiledReferences = GetEffectivePrecompiledReferences(dto, precompiledIdentities),
                    ActualReferences = ReadAssemblyReferences(outputPath),
                };
            }
            timings.PlayerGraphSeconds = phase.Elapsed.TotalSeconds;

            phase.Restart();
            progress?.Invoke("采集 asmdef 与第三方 DLL 依赖证据", 0.36f);
            DependencyCapture dependencyCapture = CaptureDependencyEvidence(
                infos,
                referencePaths,
                precompiledIdentities,
                pluginIdentitiesByAssetPath,
                inputs.AssetPaths,
                inputs.PluginImporters,
                inputs.EditorAssemblies,
                inputs.BuildTargets);
            timings.DependencyEvidenceSeconds = phase.Elapsed.TotalSeconds;

            phase.Restart();
            progress?.Invoke("读取热更 Profile 与派生证据", 0.76f);
            HotUpdateDeploymentEvidence hotUpdate = ReadHotUpdateEvidence(inputs);
            timings.HotUpdateEvidenceSeconds = phase.Elapsed.TotalSeconds;

            phase.Restart();
            progress?.Invoke("解析 UnityLinker 保留规则", 0.88f);
            LinkerPreservation[] linkerPreservations = ReadLinkerPreservations(
                infos, inputs.AssetPaths);
            timings.LinkerEvidenceSeconds = phase.Elapsed.TotalSeconds;
            timings.TotalSeconds = total.Elapsed.TotalSeconds;
            progress?.Invoke("采集完成", 1f);
            return new Snapshot(
                infos,
                referencePaths,
                hotUpdate.ProfileAssemblies,
                hotUpdate.Note,
                linkerPreservations,
                dependencyCapture.DeclaredConsumersByDependency,
                hotUpdate,
                dependencyCapture.DeclaredConsumers,
                dependencyCapture.Sources,
                dependencyCapture.ActualConsumers,
                dependencyCapture.Issues);
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
                // 只有无法归属到具体 AssemblyName 的扫描问题才属于审计全局；程序集级问题
                // 由 ExternalDependencyEvidence 按组消费，避免重复展示或污染无关依赖。
                DependencyEvidenceIssues = snapshot.DependencyEvidenceIssues
                    .Where(issue => string.IsNullOrWhiteSpace(issue.SubjectAssemblyName))
                    .ToArray(),
                AllRuntimeModulesHavePredefinedAutoReferenceDisabled =
                    runtimeModules.All(module => !module.AutoReferenced),
                DeletionChecks = BuildDependencyBoundaryChecks(snapshot),
            };
            IEnumerable<AuditProfile> evidenceProfiles = commonProfiles.Concat(moduleProfiles)
                .Concat(new[] { fullProfile });
            if (hotUpdateProfile != null) evidenceProfiles = evidenceProfiles.Concat(new[] { hotUpdateProfile });
            result.ExternalDependencies = BuildExternalDependencyEvidence(snapshot, evidenceProfiles);
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
            sb.AppendLine(result.Outcome switch
            {
                AuditOutcome.Clear =>
                    "结论：当前依赖声明一致，未发现已知的 Module 依赖方向或证据冲突。",
                AuditOutcome.Advisory =>
                    $"结论：结构检查通过；发现 {result.UnconditionalModulePreservations.Length} 条已知无条件 linker 保留规则。它们是裁剪成本说明，不是结构失败。",
                AuditOutcome.Warning =>
                    "结论：程序集依赖声明一致，但第三方来源或热更派生证据不完整 / 已漂移，需要确认后再作移除判断。",
                _ => "结论：发现需要处理的依赖、程序集定位或删除边界错误，请先看检查结果。 ",
            });
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
            AppendExternalDependencies(sb, result.ExternalDependencies, result.DependencyEvidenceIssues);
            foreach (var profile in result.CommonProfiles)
                AppendProfile(sb, profile, result.ExternalDependencies);
            AppendProfile(sb, result.FullProfile, result.ExternalDependencies);

            sb.AppendLine("热更 Profile 期望档位");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine("  " + result.HotUpdateNote);
            if (result.HotUpdateProfile != null)
                AppendFootprint(sb, result.HotUpdateProfile, result.ExternalDependencies, indent: "  ");
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

        /// <summary>
        /// 按 asmdef 的显式程序集边计算声明闭包。它与当前 DLL 元数据闭包保持分离：前者暴露删除阻塞，
        /// 后者说明当前编译产物真实使用了什么；任一闭包都不能单独代表最终 Player 保留结果。
        /// </summary>
        internal static SortedSet<string> ComputeDeclaredReachableAssemblies(
            IReadOnlyDictionary<string, AssemblyInfo> assemblies,
            IEnumerable<string> roots)
        {
            if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
            if (roots == null) throw new ArgumentNullException(nameof(roots));

            var result = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(roots.Where(name => !string.IsNullOrWhiteSpace(name)));
            while (pending.Count > 0)
            {
                string name = pending.Dequeue();
                if (!result.Add(name) || !assemblies.TryGetValue(name, out var info)) continue;

                foreach (string reference in info.DeclaredReferences)
                    if (!IsPlatformReference(reference))
                        pending.Enqueue(reference);
            }

            return result;
        }

        /// <summary>
        /// 从实际 Module Catalog 派生通用删除边界，不维护第二份可选模块注册表。Core 与 Boot 同时检查
        /// asmdef 声明和当前 DLL 元数据闭包，避免“源码声明错误但暂未使用”或“产物已反向引用但声明漂移”假绿。
        /// </summary>
        internal static DeletionCheck[] BuildDependencyBoundaryChecks(Snapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            bool IsFrameworkPlayerReference(string name) =>
                IsFrameworkAssembly(name) &&
                !IsEditorConstrained(name);
            bool IsFrameworkRuntimeReference(string name) =>
                IsFrameworkPlayerReference(name) &&
                !name.Equals(BootAssemblyName, StringComparison.Ordinal);

            string[] FindFrameworkRuntimeDependencies(string root, Func<string, bool> isCandidate)
            {
                if (!snapshot.Assemblies.ContainsKey(root)) return Array.Empty<string>();
                var actual = ComputeReachableAssemblies(snapshot.Assemblies, new[] { root });
                var declared = ComputeDeclaredReachableAssemblies(snapshot.Assemblies, new[] { root });
                return actual.Concat(declared)
                    .Where(isCandidate)
                    .Where(name => !name.Equals(root, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
            }

            string[] coreLeaks = FindFrameworkRuntimeDependencies(
                CoreAssemblyName, IsFrameworkPlayerReference);
            string[] bootLeaks = FindFrameworkRuntimeDependencies(
                BootAssemblyName, IsFrameworkRuntimeReference);
            string[] uguiLeaks = FindFrameworkRuntimeDependencies(
                UGuiAssemblyName,
                name => name.Equals(ToolkitAssemblyName, StringComparison.Ordinal) ||
                        name.Equals(BridgeAssemblyName, StringComparison.Ordinal));
            string[] toolkitLeaks = FindFrameworkRuntimeDependencies(
                ToolkitAssemblyName,
                name => name.Equals(UGuiAssemblyName, StringComparison.Ordinal) ||
                        name.Equals(BridgeAssemblyName, StringComparison.Ordinal));

            return new[]
            {
                new DeletionCheck
                {
                    Name = "Core 不反向依赖任何可选 Framework Module",
                    Explanation = coreLeaks.Length == 0
                        ? "Core 的 asmdef 声明与当前 DLL 元数据闭包都不含可选 Framework Player Module（含 Boot）。"
                        : "Core 闭包发现可选 Module：" + string.Join("、", coreLeaks) + "。",
                    Passed = coreLeaks.Length == 0,
                },
                new DeletionCheck
                {
                    Name = "Boot 不依赖 Framework Runtime",
                    Explanation = !snapshot.Assemblies.ContainsKey(BootAssemblyName)
                        ? "Boot 未参与当前 Player 编译图；未安装该热更启动薄壳时无需额外处理。"
                        : bootLeaks.Length == 0
                            ? "Boot 的 asmdef 声明与当前 DLL 元数据闭包都不含 Framework Runtime Module。"
                            : "Boot 闭包发现 Framework Runtime Module：" + string.Join("、", bootLeaks) + "。",
                    Passed = bootLeaks.Length == 0,
                },
                new DeletionCheck
                {
                    Name = "UGUI 不带 Toolkit / Bridge",
                    Explanation = uguiLeaks.Length == 0
                        ? "UGUI 的声明与当前 DLL 闭包都不会顺带引入另一套 UI 后端或嵌入桥。"
                        : "UGUI 闭包发现：" + string.Join("、", uguiLeaks) + "。",
                    Passed = uguiLeaks.Length == 0,
                },
                new DeletionCheck
                {
                    Name = "Toolkit 不带 UGUI / Bridge",
                    Explanation = toolkitLeaks.Length == 0
                        ? "Toolkit 的声明与当前 DLL 闭包都不会顺带引入 UGUI 后端或嵌入桥。"
                        : "Toolkit 闭包发现：" + string.Join("、", toolkitLeaks) + "。",
                    Passed = toolkitLeaks.Length == 0,
                },
            };
        }

        internal static string[] FindUndeclaredDirectReferences(
            AssemblyInfo info,
            Func<string, bool> isRelevantExternal,
            Func<string, bool> isPrecompiledReference = null)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (isRelevantExternal == null) throw new ArgumentNullException(nameof(isRelevantExternal));

            var declaredAssemblies = new HashSet<string>(info.DeclaredReferences, StringComparer.Ordinal);
            var declaredPrecompiled = new HashSet<string>(
                info.DeclaredPrecompiledReferences, StringComparer.Ordinal);
            return info.ActualReferences
                .Where(isRelevantExternal)
                .Where(reference => isPrecompiledReference?.Invoke(reference) == true
                    ? !declaredPrecompiled.Contains(reference)
                    : !declaredAssemblies.Contains(reference) && !declaredPrecompiled.Contains(reference))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();
        }

        internal static string[] FindUndeclaredExternalReferences(Snapshot snapshot, AssemblyInfo info)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return FindUndeclaredDirectReferences(info,
                reference => IsRelevantExternalReference(snapshot, reference),
                reference => !snapshot.Assemblies.ContainsKey(reference));
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
                        PredefinedAutoReferenceDisabled = !module.AutoReferenced,
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
                reasons.Add("当前 DLL 快照中，项目程序集直接引用本 Module：" +
                            string.Join("、", projectConsumers) +
                            "。该快照可能是 Unity 6000 返回的 Editor DLL 变体，应把它作为优先迁移候选，再由目标平台构建确认。");
            if (frameworkConsumers.Count > 0)
                reasons.Add("当前 DLL 快照中，其他 Framework Module 直接引用它：" +
                            string.Join("、", frameworkConsumers) +
                            "。只有目标 Player 变体也存在该边，且上层 Module 成为根时，引用链才会影响最终保留。");
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
                sb.AppendLine($"  ⚠ {issue.ModuleName} 的当前 DLL 快照外部引用未在 asmdef 显式声明：" +
                              string.Join(", ", issue.References));
            }
            if (issues.Count == 0)
                sb.AppendLine("  ✓ 所有 Runtime Module 的当前 DLL 快照外部引用都能从 asmdef 直接读出。");
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

        private static void AppendExternalDependencies(
            StringBuilder sb,
            IReadOnlyCollection<ExternalDependencyEvidence> dependencies,
            IReadOnlyCollection<EvidenceIssue> issues)
        {
            sb.AppendLine("第三方依赖证据目录");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine("  来源、Package 解析层级、当前 DLL 消费、asmdef 删除阻塞与 what-if 档位是不同证据；目录保持只读，不替代 Package Manager。 ");
            foreach (EvidenceIssue issue in issues)
                sb.AppendLine($"  ⚠ [{issue.Code}] {issue.Message}");
            if (dependencies.Count == 0)
                sb.AppendLine("  （一方消费者未引入可识别的外部程序集）");
            foreach (ExternalDependencyEvidence dependency in dependencies)
            {
                string version = string.IsNullOrWhiteSpace(dependency.PackageVersion)
                    ? string.Empty
                    : "@" + dependency.PackageVersion;
                string bytes = dependency.HasProfileMeasurement
                    ? FormatBytes(dependency.MaxProfileRawBytes) + "（最高档位原始字节）"
                    : "当前档位未测得字节";
                sb.AppendLine($"  • {dependency.DisplayName}{version} · {dependency.Summary} · {bytes}");
                sb.AppendLine("      程序集：" + string.Join("、", dependency.Assemblies.Select(item => item.AssemblyName)));
                if (dependency.ActualConsumers.Length > 0)
                    sb.AppendLine("      当前 DLL 直接消费者：" + string.Join("、", dependency.ActualConsumers.Select(item =>
                        item.ConsumerAssemblyName + "（" + item.PlatformScope + "）")));
                if (dependency.Introducers.Length > 0)
                    sb.AppendLine("      最初引入者：" + string.Join("、", dependency.Introducers.Select(item =>
                        item.ConsumerAssemblyName + "（" + item.PlatformScope + "）")));
                if (dependency.DeclaredConsumers.Length > 0)
                    sb.AppendLine("      asmdef 声明消费者：" + string.Join("、", dependency.DeclaredConsumers
                        .Select(item => item.ConsumerAssemblyName + "（" + item.PlatformScope + "）")
                        .Distinct(StringComparer.Ordinal)));
                if (dependency.AffectedProfileKeys.Length > 0)
                    sb.AppendLine("      影响档位：" + string.Join("、", dependency.AffectedProfileKeys));
                foreach (EvidenceIssue issue in dependency.EvidenceIssues)
                    sb.AppendLine($"      ⚠ [{issue.Code}] {issue.Message}");
                if (dependency.HasInstalledBinaryMeasurement && !dependency.HasProfileMeasurement)
                    sb.AppendLine("      已安装二进制：" + FormatBytes(dependency.InstalledBinaryBytes) +
                                  "（仅证明磁盘文件存在，不是 what-if Profile 体积）");
                sb.AppendLine("      移除前：" + string.Join(" ", dependency.RemovalSteps));
            }
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

        private static void AppendProfile(
            StringBuilder sb,
            AuditProfile profile,
            IReadOnlyCollection<ExternalDependencyEvidence> externalDependencies)
        {
            sb.AppendLine(profile.Title);
            sb.AppendLine("────────────────────────────────────────");
            AppendFootprint(sb, profile, externalDependencies, indent: "  ");
            sb.AppendLine();
        }

        private static void AppendFootprint(
            StringBuilder sb,
            AuditProfile profile,
            IReadOnlyCollection<ExternalDependencyEvidence> externalDependencies,
            string indent)
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
                string external = string.Join(", ", externalDependencies
                    .Where(dependency => dependency.AffectedProfileKeys.Contains(
                        profile.Key, StringComparer.Ordinal))
                    .OrderByDescending(dependency => dependency.ProfileRawBytesByKey.TryGetValue(
                        profile.Key, out long bytes) ? bytes : 0)
                    .ThenBy(dependency => dependency.DisplayName, StringComparer.Ordinal)
                    .Select(dependency => $"{dependency.DisplayName} " +
                                          FormatBytes(dependency.ProfileRawBytesByKey.TryGetValue(
                                              profile.Key, out long bytes) ? bytes : 0)));
                sb.AppendLine(indent + "外部依赖组：" + external + "（完整消费与移除证据见目录）");
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

        internal static ExternalDependencyEvidence[] BuildExternalDependencyEvidence(
            Snapshot snapshot,
            IEnumerable<AuditProfile> profiles)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));

            AuditProfile[] profileArray = profiles.Where(profile => profile != null).ToArray();
            var graph = new ExternalDependencyGraph(snapshot);
            HashSet<string> candidateNames = graph.DiscoverCandidates(profileArray);

            string GroupKey(DependencySource source) => !string.IsNullOrWhiteSpace(source.PackageName)
                ? "upm:" + source.PackageName
                : !string.IsNullOrWhiteSpace(source.AssetPath)
                    ? "asset:" + source.AssetPath
                    : "unknown:" + source.AssemblyName;

            return candidateNames
                .Where(name => !IsPlatformReference(snapshot, name))
                .Select(graph.ResolveSource)
                .Where(source => source.IsExternal)
                .GroupBy(GroupKey, StringComparer.Ordinal)
                .Select(group => BuildExternalDependencyGroup(
                    snapshot, graph, profileArray, group.Key, group))
                .OrderBy(dependency => dependency.Role)
                .ThenBy(dependency => dependency.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static ExternalDependencyEvidence BuildExternalDependencyGroup(
            Snapshot snapshot,
            ExternalDependencyGraph graph,
            IReadOnlyCollection<AuditProfile> profiles,
            string key,
            IEnumerable<DependencySource> groupedSources)
        {
            DependencySource[] sources = groupedSources
                .GroupBy(source => source.AssemblyName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(source => source.AssemblyName, StringComparer.Ordinal)
                .ToArray();
            var assemblyNames = new HashSet<string>(sources.Select(source => source.AssemblyName),
                StringComparer.Ordinal);
            DependencySource first = sources[0];

            DeclaredConsumerEvidence[] declared = snapshot.DeclaredConsumers
                .Where(edge => assemblyNames.Contains(edge.DependencyAssemblyName))
                .OrderBy(edge => edge.ConsumerAssemblyName, StringComparer.Ordinal)
                .ThenBy(edge => edge.DependencyAssemblyName, StringComparer.Ordinal)
                .ToArray();
            ActualConsumerEvidence[] actual = snapshot.ActualConsumers
                .Where(edge => assemblyNames.Contains(edge.DependencyAssemblyName))
                .Where(edge => !assemblyNames.Contains(edge.ConsumerAssemblyName))
                .OrderBy(edge => edge.ConsumerAssemblyName, StringComparer.Ordinal)
                .ThenBy(edge => edge.DependencyAssemblyName, StringComparer.Ordinal)
                .ThenBy(edge => edge.PlatformScope)
                .ToArray();

            IntroducerTrace introducerTrace = graph.FindIntroducers(assemblyNames);
            ActualConsumerEvidence[] introducers = introducerTrace.Introducers;
            string[] frameworkConsumers = introducers
                .Where(edge => edge.ConsumerIsFramework)
                .Select(edge => edge.ConsumerAssemblyName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] projectConsumers = introducers
                .Where(edge => edge.ConsumerIsProjectAsset && !edge.ConsumerIsFramework)
                .Select(edge => edge.ConsumerAssemblyName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var directProfiles = new List<string>();
            var transitiveProfiles = new List<string>();
            foreach (AuditProfile profile in profiles)
            {
                if (!profile.Footprint.ExternalAssemblies.Keys.Any(assemblyNames.Contains)) continue;
                bool direct = profile.Roots.Any(root =>
                    snapshot.Assemblies.TryGetValue(root, out AssemblyInfo rootInfo) &&
                    rootInfo.ActualReferences.Any(assemblyNames.Contains));
                (direct ? directProfiles : transitiveProfiles).Add(profile.Key);
            }

            var profileRawBytesByKey = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (AuditProfile profile in profiles)
            {
                KeyValuePair<string, long>[] measurements = profile.Footprint.ExternalAssemblies
                    .Where(pair => assemblyNames.Contains(pair.Key))
                    .ToArray();
                if (measurements.Length > 0)
                    profileRawBytesByKey[profile.Key] = measurements.Sum(pair => pair.Value);
            }

            string[] installedPhysicalPaths = sources
                .Where(source => source.IsPrecompiledAssembly)
                .SelectMany(source => source.AllPhysicalPaths)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            long installedBinaryBytes = installedPhysicalPaths.Sum(path => new FileInfo(path).Length);
            bool hasInstalledBinaryMeasurement = installedPhysicalPaths.Length > 0;

            string[] affected = directProfiles.Concat(transitiveProfiles)
                .Distinct(StringComparer.Ordinal).ToArray();
            EvidenceIssue[] scopedScanIssues = snapshot.DependencyEvidenceIssues
                .Where(issue => !string.IsNullOrWhiteSpace(issue.SubjectAssemblyName) &&
                                assemblyNames.Contains(issue.SubjectAssemblyName))
                .ToArray();
            bool hasGlobalScanGap = snapshot.DependencyEvidenceIssues.Any(issue =>
                string.IsNullOrWhiteSpace(issue.SubjectAssemblyName));
            EvidenceIssue[] groupIssues = ValidateDependencyGroupConsistency(sources)
                .Concat(scopedScanIssues)
                .Concat(introducerTrace.HasUnknownPlatformEvidence
                    ? new[]
                    {
                        new EvidenceIssue
                        {
                            Code = "dependency-platform-scope-unknown",
                            Message = "至少一条引入链的平台范围无法确认；角色保留，删除结论收紧为待复核。 ",
                        },
                    }
                    : Array.Empty<EvidenceIssue>())
                .ToArray();
            bool hasGaps = hasGlobalScanGap || groupIssues.Length > 0 ||
                           sources.Any(source => source.SourceKind ==
                                                 FrameworkModuleSourceCatalog.SourceKind.UnknownPackage);
            bool coreIntroduces = introducers.Any(edge =>
                edge.ConsumerAssemblyName.Equals(CoreAssemblyName, StringComparison.Ordinal));
            bool firstPartyOnlyEditor = introducers.Length > 0 &&
                                        introducers.All(edge => edge.ConsumerIsEditorOnly);
            bool hasAnyProjectIntroducer = introducers.Any(edge =>
                edge.ConsumerIsProjectAsset && !edge.ConsumerIsFramework);
            string[] frameworkRuntimeIntroducers = introducers
                .Where(edge => edge.ConsumerIsFramework && !edge.ConsumerIsEditorOnly)
                .Select(edge => edge.ConsumerAssemblyName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            ExternalDependencyRole role;
            if (coreIntroduces)
            {
                role = ExternalDependencyRole.BaseRuntime;
            }
            else if (firstPartyOnlyEditor)
            {
                role = ExternalDependencyRole.EditorTool;
            }
            else if (hasAnyProjectIntroducer)
            {
                role = ExternalDependencyRole.ProjectConsumer;
            }
            else if (frameworkRuntimeIntroducers.Length == 1)
            {
                role = ExternalDependencyRole.OptionalRuntime;
            }
            else if (frameworkRuntimeIntroducers.Length > 1)
            {
                role = ExternalDependencyRole.SharedRuntime;
            }
            else
            {
                role = ExternalDependencyRole.Unknown;
            }

            ExternalDependencyRemovalState removal = hasGaps
                ? ExternalDependencyRemovalState.ReviewRequired
                : role switch
                {
                    ExternalDependencyRole.BaseRuntime => ExternalDependencyRemovalState.RequiredByCore,
                    ExternalDependencyRole.OptionalRuntime =>
                        ExternalDependencyRemovalState.RemoveWithOptionalModuleCandidate,
                    ExternalDependencyRole.EditorTool =>
                        ExternalDependencyRemovalState.RemoveWithEditorToolCandidate,
                    ExternalDependencyRole.SharedRuntime =>
                        ExternalDependencyRemovalState.SharedConsumerMigrationRequired,
                    ExternalDependencyRole.ProjectConsumer =>
                        ExternalDependencyRemovalState.ProjectConsumerMigrationRequired,
                    _ => ExternalDependencyRemovalState.ReviewRequired,
                };

            var evidence = new ExternalDependencyEvidence
            {
                Key = key,
                DisplayName = !string.IsNullOrWhiteSpace(first.PackageName)
                    ? first.PackageName
                    : sources.Length == 1 ? first.AssemblyName : key,
                PackageName = first.PackageName,
                PackageVersion = first.PackageVersion,
                PackageId = first.PackageId,
                SourceKind = first.SourceKind,
                HasPackageDirectness = first.HasPackageDirectness,
                IsDirectPackageDependency = first.IsDirectPackageDependency,
                Assemblies = sources,
                DeclaredConsumers = declared,
                ActualConsumers = actual,
                Introducers = introducers,
                EvidenceIssues = groupIssues,
                DirectProfileKeys = directProfiles.Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                TransitiveProfileKeys = transitiveProfiles.Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                FrameworkConsumers = frameworkConsumers,
                ProjectConsumers = projectConsumers,
                Role = role,
                RemovalState = removal,
                ProfileRawBytesByKey = profileRawBytesByKey,
                InstalledBinaryBytes = installedBinaryBytes,
                HasInstalledBinaryMeasurement = hasInstalledBinaryMeasurement,
            };
            evidence.Summary = BuildExternalDependencySummary(evidence);
            evidence.RemovalSteps = BuildExternalDependencyRemovalSteps(evidence);
            evidence.VerificationSteps = BuildExternalDependencyVerificationSteps(evidence);
            return evidence;
        }

        private sealed class IntroducerTrace
        {
            internal ActualConsumerEvidence[] Introducers = Array.Empty<ActualConsumerEvidence>();
            internal bool HasUnknownPlatformEvidence;
        }

        /// <summary>
        /// 一次建立正向/反向 AssemblyRef 索引。候选只从一方消费者与 Profile 出发，再沿外部依赖链扩展；
        /// 反向回溯携带平台范围，避免 Editor/当前目标的同名 DLL 变体被串成一条不存在的路径。
        /// </summary>
        private sealed class ExternalDependencyGraph
        {
            private const int PlayerFlag = 1;
            private const int EditorFlag = 2;
            private const int TestsFlag = 4;
            private const int AnyMask = PlayerFlag | EditorFlag | TestsFlag;

            private readonly Snapshot _snapshot;
            private readonly Dictionary<string, ActualConsumerEvidence[]> _outgoing;
            private readonly Dictionary<string, ActualConsumerEvidence[]> _incoming;

            internal ExternalDependencyGraph(Snapshot snapshot)
            {
                _snapshot = snapshot;
                _outgoing = snapshot.ActualConsumers
                    .GroupBy(edge => edge.ConsumerAssemblyName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                _incoming = snapshot.ActualConsumers
                    .GroupBy(edge => edge.DependencyAssemblyName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            }

            internal HashSet<string> DiscoverCandidates(IEnumerable<AuditProfile> profiles)
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                var visited = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
                var pending = new Queue<(string AssemblyName, int PlatformMask)>();

                void Enqueue(string assemblyName, int mask)
                {
                    if (mask == 0 || !IsExternalDependency(assemblyName)) return;
                    if (!visited.TryGetValue(assemblyName, out HashSet<int> masks))
                    {
                        masks = new HashSet<int>();
                        visited[assemblyName] = masks;
                    }
                    if (!masks.Add(mask)) return;
                    result.Add(assemblyName);
                    pending.Enqueue((assemblyName, mask));
                }

                foreach (AuditProfile profile in profiles)
                    foreach (string assemblyName in profile.Footprint.ExternalAssemblies.Keys)
                        Enqueue(assemblyName, PlayerFlag);
                foreach (DeclaredConsumerEvidence edge in _snapshot.DeclaredConsumers)
                    if (IsFirstParty(edge))
                        Enqueue(edge.DependencyAssemblyName, ScopeMask(edge.PlatformScope));
                foreach (ActualConsumerEvidence edge in _snapshot.ActualConsumers)
                    if (IsFirstParty(edge))
                        Enqueue(edge.DependencyAssemblyName, ScopeMask(edge.PlatformScope));

                while (pending.Count > 0)
                {
                    (string consumer, int pathMask) = pending.Dequeue();
                    if (!_outgoing.TryGetValue(consumer, out ActualConsumerEvidence[] edges)) continue;
                    foreach (ActualConsumerEvidence edge in edges)
                        Enqueue(edge.DependencyAssemblyName,
                            pathMask & ScopeMask(edge.PlatformScope));
                }
                return result;
            }

            internal DependencySource ResolveSource(string name)
            {
                if (_snapshot.DependencySources.TryGetValue(name, out DependencySource source)) return source;
                if (_snapshot.Assemblies.TryGetValue(name, out AssemblyInfo info))
                    return new DependencySource
                    {
                        AssemblyName = name,
                        AssetPath = info.AsmdefPath,
                        PhysicalPath = string.IsNullOrWhiteSpace(info.SourceDirectory)
                            ? string.Empty
                            : Path.Combine(info.SourceDirectory, Path.GetFileName(info.AsmdefPath)),
                        PackageName = info.PackageName,
                        PackageVersion = info.PackageVersion,
                        PackageId = info.PackageId,
                        SourceKind = info.SourceKind,
                        HasPackageDirectness = info.HasPackageDirectness,
                        IsDirectPackageDependency = info.IsDirectPackageDependency,
                        IsExternal = !IsFrameworkAssembly(name) &&
                                     info.SourceKind != FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
                    };
                return new DependencySource
                {
                    AssemblyName = name,
                    SourceKind = FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                    IsExternal = true,
                };
            }

            internal IntroducerTrace FindIntroducers(ISet<string> assemblyNames)
            {
                var visited = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
                var pending = new Queue<(string AssemblyName, int PlatformMask)>();
                var introducers = new List<ActualConsumerEvidence>();
                bool unknownAtBoundary = false;

                void Enqueue(string assemblyName, int mask, bool unknown)
                {
                    if (mask == 0) return;
                    if (!visited.TryGetValue(assemblyName, out Dictionary<int, bool> masks))
                    {
                        masks = new Dictionary<int, bool>();
                        visited[assemblyName] = masks;
                    }
                    if (masks.TryGetValue(mask, out bool existingUnknown) &&
                        (existingUnknown || !unknown))
                        return;
                    masks[mask] = existingUnknown || unknown;
                    pending.Enqueue((assemblyName, mask));
                }

                foreach (string assemblyName in assemblyNames)
                    Enqueue(assemblyName, AnyMask, false);

                while (pending.Count > 0)
                {
                    (string dependency, int pathMask) = pending.Dequeue();
                    bool pathUnknown = visited[dependency][pathMask];
                    if (!_incoming.TryGetValue(dependency, out ActualConsumerEvidence[] edges)) continue;
                    foreach (ActualConsumerEvidence edge in edges)
                    {
                        int edgeMask = ScopeMask(edge.PlatformScope);
                        int intersection = pathMask & edgeMask;
                        if (intersection == 0) continue;
                        bool nextUnknown = pathUnknown || edge.PlatformScope == ConsumerPlatformScope.Unknown;
                        if (IsFirstParty(edge))
                        {
                            introducers.Add(CloneWithScope(edge, ScopeFromMask(intersection)));
                            unknownAtBoundary |= nextUnknown;
                        }
                        else
                        {
                            Enqueue(edge.ConsumerAssemblyName, intersection, nextUnknown);
                        }
                    }
                }

                foreach (DeclaredConsumerEvidence edge in _snapshot.DeclaredConsumers.Where(IsFirstParty))
                {
                    if (!visited.TryGetValue(
                            edge.DependencyAssemblyName, out Dictionary<int, bool> dependencyMasks))
                        continue;
                    foreach (var pair in dependencyMasks)
                    {
                        int intersection = pair.Key & ScopeMask(edge.PlatformScope);
                        if (intersection == 0) continue;
                        introducers.Add(new ActualConsumerEvidence
                        {
                            DependencyAssemblyName = edge.DependencyAssemblyName,
                            ConsumerAssemblyName = edge.ConsumerAssemblyName,
                            ConsumerAsmdefPath = edge.ConsumerAsmdefPath,
                            ConsumerSourceKind = edge.ConsumerSourceKind,
                            ConsumerPackageName = edge.ConsumerPackageName,
                            PlatformScope = ScopeFromMask(intersection),
                        });
                        unknownAtBoundary |= pair.Value || edge.PlatformScope == ConsumerPlatformScope.Unknown;
                    }
                }

                return new IntroducerTrace
                {
                    Introducers = introducers
                        .GroupBy(edge => edge.ConsumerAssemblyName, StringComparer.Ordinal)
                        .Select(group =>
                        {
                            ActualConsumerEvidence preferred = group.First();
                            return CloneWithScope(preferred,
                                CombinePlatformScopes(group.Select(edge => edge.PlatformScope)));
                        })
                        .OrderBy(edge => edge.ConsumerAssemblyName, StringComparer.Ordinal)
                        .ToArray(),
                    HasUnknownPlatformEvidence = unknownAtBoundary,
                };
            }

            private bool IsExternalDependency(string assemblyName) =>
                !IsPlatformReference(_snapshot, assemblyName) && ResolveSource(assemblyName).IsExternal;

            private bool IsFirstParty(ActualConsumerEvidence edge)
            {
                if (edge.ConsumerIsFramework) return true;
                if (_snapshot.DependencySources.TryGetValue(
                        edge.ConsumerAssemblyName, out DependencySource source))
                    return !source.IsExternal && edge.ConsumerIsProjectAsset;
                if (_snapshot.Assemblies.TryGetValue(edge.ConsumerAssemblyName, out AssemblyInfo info))
                    return info.SourceKind == FrameworkModuleSourceCatalog.SourceKind.ProjectAssets;
                return edge.ConsumerIsProjectAsset;
            }

            private static bool IsFirstParty(DeclaredConsumerEvidence edge) =>
                edge.ConsumerIsFramework || edge.ConsumerIsProjectAsset;

            private static int ScopeMask(ConsumerPlatformScope scope) => scope switch
            {
                ConsumerPlatformScope.Player => PlayerFlag,
                // Editor 程序集也可被 Test 程序集消费；Tests 是 Editor 域的更窄子集。
                ConsumerPlatformScope.Editor => EditorFlag | TestsFlag,
                ConsumerPlatformScope.Tests => TestsFlag,
                ConsumerPlatformScope.Mixed => AnyMask,
                _ => AnyMask,
            };

            private static ConsumerPlatformScope ScopeFromMask(int mask) => mask switch
            {
                PlayerFlag => ConsumerPlatformScope.Player,
                EditorFlag => ConsumerPlatformScope.Editor,
                TestsFlag => ConsumerPlatformScope.Tests,
                EditorFlag | TestsFlag => ConsumerPlatformScope.Editor,
                PlayerFlag | EditorFlag => ConsumerPlatformScope.Mixed,
                AnyMask => ConsumerPlatformScope.Mixed,
                _ => ConsumerPlatformScope.Unknown,
            };

            private static ActualConsumerEvidence CloneWithScope(
                ActualConsumerEvidence edge,
                ConsumerPlatformScope scope) => new()
            {
                DependencyAssemblyName = edge.DependencyAssemblyName,
                ConsumerAssemblyName = edge.ConsumerAssemblyName,
                ConsumerAsmdefPath = edge.ConsumerAsmdefPath,
                ConsumerSourceKind = edge.ConsumerSourceKind,
                ConsumerPackageName = edge.ConsumerPackageName,
                PlatformScope = scope,
            };
        }

        private static ConsumerPlatformScope CombinePlatformScopes(
            IEnumerable<ConsumerPlatformScope> scopes)
        {
            ConsumerPlatformScope[] values = scopes.Distinct().ToArray();
            if (values.Length == 0) return ConsumerPlatformScope.Unknown;
            if (values.Length == 1) return values[0];
            if (values.Contains(ConsumerPlatformScope.Player) ||
                values.Contains(ConsumerPlatformScope.Mixed))
                return ConsumerPlatformScope.Mixed;
            if (values.All(value => value is ConsumerPlatformScope.Editor or ConsumerPlatformScope.Tests))
                return ConsumerPlatformScope.Editor;
            return ConsumerPlatformScope.Unknown;
        }

        internal static bool AreDependencySourceVariantsPlatformExclusive(
            DependencySourceVariant left,
            DependencySourceVariant right) =>
            left != null && right != null &&
            left.HasCompatibilityEvidence && right.HasCompatibilityEvidence &&
            (left.IsEditorCompatible || left.CompatibleBuildTargets.Length > 0) &&
            (right.IsEditorCompatible || right.CompatibleBuildTargets.Length > 0) &&
            !(left.IsEditorCompatible && right.IsEditorCompatible) &&
            !left.CompatibleBuildTargets.Intersect(
                right.CompatibleBuildTargets, StringComparer.Ordinal).Any();

        private static EvidenceIssue[] ValidateDependencyGroupConsistency(
            IReadOnlyCollection<DependencySource> sources)
        {
            var issues = new List<EvidenceIssue>();
            void Add(string code, string message) =>
                issues.Add(new EvidenceIssue { Code = code, Message = message });

            if (sources.Select(source => source.SourceKind).Distinct().Count() > 1)
                Add("package-source-kind-inconsistent", "同一依赖组中的程序集来源类型不一致。 ");
            string[] versions = sources.Select(source => source.PackageVersion)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (versions.Length > 1)
                Add("package-version-inconsistent", "同一 Package 依赖组解析到了多个版本：" +
                                                     string.Join("、", versions) + "。 ");
            string[] packageIds = sources.Select(source => source.PackageId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (packageIds.Length > 1)
                Add("package-id-inconsistent", "同一 Package 依赖组的解析标识不一致：" +
                                                string.Join("、", packageIds) + "。 ");
            bool[] directness = sources.Where(source => source.HasPackageDirectness)
                .Select(source => source.IsDirectPackageDependency)
                .Distinct()
                .ToArray();
            if (directness.Length > 1 ||
                (sources.Any(source => source.HasPackageDirectness) &&
                 sources.Any(source => !source.HasPackageDirectness)))
                Add("package-directness-inconsistent", "同一 Package 依赖组的直接/间接解析证据不一致。 ");
            return issues.ToArray();
        }

        internal static string DescribeSourceKind(FrameworkModuleSourceCatalog.SourceKind kind) => kind switch
        {
            FrameworkModuleSourceCatalog.SourceKind.ProjectAssets => "Assets 插件",
            FrameworkModuleSourceCatalog.SourceKind.BuiltInPackage => "Unity 内置 Package",
            FrameworkModuleSourceCatalog.SourceKind.EmbeddedPackage => "嵌入式 Package",
            FrameworkModuleSourceCatalog.SourceKind.GitPackage => "Git Package",
            FrameworkModuleSourceCatalog.SourceKind.LocalPackage => "本地路径 Package",
            FrameworkModuleSourceCatalog.SourceKind.LocalTarballPackage => "本地压缩包 Package",
            FrameworkModuleSourceCatalog.SourceKind.RegistryPackage => "Registry Package",
            _ => "来源未知",
        };

        internal static string DescribeRemovalState(ExternalDependencyRemovalState state) => state switch
        {
            ExternalDependencyRemovalState.RequiredByCore => "核心基础依赖",
            ExternalDependencyRemovalState.RemoveWithOptionalModuleCandidate => "可随单一可选 Module 评估移除",
            ExternalDependencyRemovalState.RemoveWithEditorToolCandidate => "可随 Editor 工具评估移除",
            ExternalDependencyRemovalState.SharedConsumerMigrationRequired => "多个能力共享，需先迁移消费者",
            ExternalDependencyRemovalState.ProjectConsumerMigrationRequired => "项目代码仍在使用，需先迁移",
            _ => "证据不完整，暂不能判断",
        };

        private static string BuildExternalDependencySummary(ExternalDependencyEvidence evidence)
        {
            string source = DescribeSourceKind(evidence.SourceKind);
            string packageDepth = !evidence.HasPackageDirectness
                ? string.Empty
                : evidence.IsDirectPackageDependency ? "，项目直接声明" : "，由其他 Package 间接解析";
            return $"{source}{packageDepth}；{DescribeRemovalState(evidence.RemovalState)}。";
        }

        private static string[] BuildExternalDependencyRemovalSteps(ExternalDependencyEvidence evidence)
        {
            var steps = new List<string>();
            if (evidence.FrameworkConsumers.Length > 0)
                steps.Add("先处理 Framework 消费者：" + string.Join("、", evidence.FrameworkConsumers) + "。");
            if (evidence.ProjectConsumers.Length > 0)
                steps.Add("先迁移项目消费者：" + string.Join("、", evidence.ProjectConsumers) + "。");
            switch (evidence.RemovalState)
            {
                case ExternalDependencyRemovalState.RequiredByCore:
                    steps.Add("它位于 Core what-if 闭包中；若要替换，需要把 Core 的公共契约与实现一起设计为新的 Seam，不能只删文件。 ");
                    break;
                case ExternalDependencyRemovalState.RemoveWithOptionalModuleCandidate:
                    steps.Add("把唯一引入它的可选 Module、对应 Editor/Test 辅助和热更 Profile 清理放在同一次结构变更中。 ");
                    break;
                case ExternalDependencyRemovalState.RemoveWithEditorToolCandidate:
                    steps.Add("先移除或关闭对应 Editor 工具程序集，再按该依赖的安装形态从 Package Manager 或 Assets 中处理。 ");
                    break;
                case ExternalDependencyRemovalState.ReviewRequired:
                    steps.Add("先修复顶部列出的来源或扫描缺口；在证据完整前，不执行删除。 ");
                    break;
            }
            if (!string.IsNullOrWhiteSpace(evidence.PackageName))
            {
                if (!evidence.HasPackageDirectness)
                    steps.Add("Package 的直接/间接层级当前不可用；先在 Package Manager 与 Packages/manifest.json 核实上游，再决定移除入口。 ");
                else
                    steps.Add(evidence.IsDirectPackageDependency
                        ? "消费者清零后再通过 Unity Package Manager 处理直接依赖；本窗口保持只读。 "
                        : "这是解析得到的间接 Package；应调整它的直接上游，而不是把缓存目录当成卸载入口。 ");
            }
            else if (evidence.Assemblies.Any(source => source.IsPrecompiledAssembly))
                steps.Add("消费者清零后再从版本控制中移除对应 Assets DLL 与配套资源；本窗口只负责定位和解释。 ");
            return steps.Distinct(StringComparer.Ordinal).ToArray();
        }

        private static string[] BuildExternalDependencyVerificationSteps(ExternalDependencyEvidence evidence) => new[]
        {
            "等待 Unity 完成重新编译，并再次运行模块裁剪审计，确认没有 Unknown、声明阻塞或当前 DLL 消费边。",
            "运行受影响 Module 的 EditMode / PlayMode 测试；涉及反射、序列化、热更或 link.xml 时覆盖对应运行路径。",
            "在真实目标平台生成 Player BuildReport；原始托管 DLL 字节只用于找候选，不能证明最终包体变化。",
        };

        private static void AppendDeletionTests(StringBuilder sb, IReadOnlyCollection<DeletionCheck> checks)
        {
            sb.AppendLine("删除检查（asmdef 声明 + 当前 DLL 元数据闭包）");
            sb.AppendLine("────────────────────────────────────────");
            foreach (var check in checks)
                AppendDeletionResult(sb, check.Name, check.Explanation, check.Passed);
            sb.AppendLine("  注：检查同时比较 asmdef 声明与当前 DLL 元数据闭包；通过仍不证明最终玩家包已达到体积预算。");
        }

        private static void AppendDeletionResult(StringBuilder sb, string name, string explanation, bool passed)
        {
            sb.AppendLine($"  {(passed ? "✓" : "✗")} {name}");
            if (!string.IsNullOrWhiteSpace(explanation)) sb.AppendLine("    " + explanation);
        }

        private static string[] BuildRecommendations(AuditResult result)
        {
            var recommendations = new List<string>();
            if (result.HasDependencyEvidenceGaps)
                recommendations.Add("第三方依赖扫描存在证据缺口；先修复无法读取的 asmdef、Editor DLL 或预编译引用映射，再判断依赖能否移除。 ");
            else if (result.HasUnknownExternalDependencySources)
                recommendations.Add("至少一个外部程序集无法映射到 Assets 或已注册 Package；先补全来源证据，不要按名称猜供应商或直接删除。 ");
            if (result.DependencyIssues.Length > 0)
                recommendations.Add("先把隐式外部引用补进对应 asmdef；否则编辑器能编译，不代表模块真的能独立取舍。");
            if (!result.AllRuntimeModulesHavePredefinedAutoReferenceDisabled)
                recommendations.Add("仍有 Runtime Module 开启 autoReferenced。它会允许 Assembly-CSharp 等预定义程序集在没有 asmdef 声明的情况下引用该 Module；建议关闭后由消费程序集显式声明。这个设置不决定 Module 是否参与 Player 编译或最终保留。 ");
            if (result.HasUnresolvedAssemblies)
                recommendations.Add("有程序集文件无法定位，本次闭包和字节数不完整；先修编译或热更清单，再比较体积。");
            if (result.DeletionChecks.Any(check => !check.Passed))
                recommendations.Add("至少一条删除检查失败，说明可选模块发生了反向耦合；先修依赖方向，再讨论包体优化。");
            if (result.HasRetentionAdvisories)
            {
                string targets = string.Join("、", result.UnconditionalModulePreservations
                    .Select(rule => $"{rule.OwnerModuleName} → {rule.AssemblyName}")
                    .Distinct(StringComparer.Ordinal));
                recommendations.Add("已知保留说明：可选 Module 目录下存在无条件 link.xml 保留：" + targets +
                                    "。这不是依赖错误；保留该 Module 时，它会成为 UnityLinker 根并限制程序集或成员裁剪。" +
                                    "物理移除 Module 会连同其 link.xml 一并移除，保留 Module 时则应结合反射、序列化或热更需求理解这项成本。 ");
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
                recommendations.Add("当前 asmdef 依赖方向允许业务从 Core 开始，只声明真正使用的 Module；但源码或 Package 中仍存在的 Runtime Module 会参与 Player 编译，autoReferenced:false 不代表自动移除。Core 热更时，仍参与编译且引用 Core 的 Module 也必须热更；强裁剪要把 Module 退出编译图与 Profile 清理作为同一次结构变更。 ");

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
                string name = ReadManagedAssemblyIdentity(path);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!result.TryGetValue(name, out string existing))
                {
                    result[name] = path;
                    continue;
                }
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) continue;

                string existingHash = ComputeFileSha256(existing);
                string candidateHash = ComputeFileSha256(path);
                if (!string.Equals(existingHash, candidateHash, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Player 编译图中 AssemblyName '{name}' 对应多个内容不同的 DLL：{existing}；{path}");

                // 相同身份且字节相同的副本不会改变闭包；固定选字典序较小路径，保证报告稳定。
                if (string.Compare(path, existing, StringComparison.OrdinalIgnoreCase) < 0)
                    result[name] = path;
            }
            return result;
        }

        internal static string ReadManagedAssemblyIdentity(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return AssemblyName.GetAssemblyName(FullPath(path)).Name ?? string.Empty;
            }
            catch (BadImageFormatException)
            {
                return string.Empty;
            }
            catch (FileLoadException)
            {
                return string.Empty;
            }
            catch (Exception)
            {
                // 身份读取是证据采集，不应让单个受损/无权限 DLL 中断整个目录；调用方会把空身份
                // 转成带资产路径的结构化 issue。真正的编译输出不可读仍由 Capture 主路径 fail-fast。
                return string.Empty;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        internal static string[] ReadAssemblyReferences(string path)
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

        private static HotUpdateDeploymentEvidence ReadHotUpdateEvidence(CaptureInputs inputs)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            var evidence = new HotUpdateDeploymentEvidence();
            Type profileType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Game.Framework.Build.FrameworkHotUpdateProfile", false))
                .FirstOrDefault(type => type != null);
            if (profileType == null)
            {
                evidence.Note = "未安装 HybridCLR 热更构建 Module；按纯 AOT 理解。资源构建 Module 是否安装与此状态无关。";
                return evidence;
            }
            evidence.HotUpdateBuildModuleAvailable = true;

            IReadOnlyList<string> paths = FrameworkEditorProfileCatalog.GetPaths(profileType);
            evidence.ProfileCount = paths.Count;
            if (paths.Count == 0)
            {
                evidence.Note = "未找到 FrameworkHotUpdateProfile；请在代码热更新工作台明确创建。若目标是纯 AOT，也应保留空 Profile 作为明确的单一真源。";
                return evidence;
            }
            evidence.ProfileAvailable = true;

            string path = paths[0];
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
            string multiple = paths.Count > 1 ? $"；发现 {paths.Count} 个 Profile，仅检查排序第一项" : string.Empty;
            evidence.Note = evidence.ProfileAssemblies.Length == 0
                ? $"{path}：Profile 期望纯 AOT{multiple}。"
                : $"{path}：Profile 期望 {evidence.ProfileAssemblies.Length} 个热更入口{multiple}。";

            Type builderType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Game.Framework.Build.FrameworkHotUpdateBuilder", false))
                .FirstOrDefault(type => type != null);
            const BindingFlags inspectFlags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo inspect = builderType?.GetMethod(
                "InspectEvidenceFromSnapshot",
                inspectFlags,
                binder: null,
                new[]
                {
                    profileType,
                    typeof(string[]),
                    typeof(UnityEditor.Compilation.Assembly[]),
                },
                modifiers: null) ?? builderType?.GetMethod(
                "InspectEvidence",
                inspectFlags,
                binder: null,
                new[] { profileType },
                modifiers: null);
            if (inspect == null)
            {
                evidence.Note += " 当前 Build Module 未提供派生证据检查。";
                return evidence;
            }

            try
            {
                object[] arguments = inspect.GetParameters().Length == 3
                    ? new object[] { profile, inputs.AssetPaths, inputs.PlayerAssemblies }
                    : new[] { profile };
                object raw = inspect.Invoke(null, arguments);
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
            IReadOnlyDictionary<string, AssemblyInfo> assemblies,
            IEnumerable<string> assetPaths)
        {
            var result = new List<LinkerPreservation>();
            foreach (FrameworkModuleSourceCatalog.SourceLocation source in
                     FrameworkModuleSourceCatalog.EnumerateFiles("link.xml", assetPaths))
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
        /// 一次读取全部 asmdef 与可见预编译 DLL，统一产出来源、有效声明边、Player/Editor 当前 DLL 边和扫描问题。
        /// 声明读取失败不会再被解释成“没有消费者”。
        /// </summary>
        private static DependencyCapture CaptureDependencyEvidence(
            IReadOnlyDictionary<string, AssemblyInfo> playerAssemblies,
            IReadOnlyDictionary<string, string> referencePaths,
            IReadOnlyDictionary<string, string> precompiledIdentities,
            IReadOnlyDictionary<string, string> pluginIdentitiesByAssetPath,
            IEnumerable<string> assetPaths,
            IEnumerable<PluginImporter> pluginImporters,
            IEnumerable<UnityEditor.Compilation.Assembly> editorAssemblies,
            IReadOnlyCollection<BuildTarget> buildTargets)
        {
            var capture = new DependencyCapture();
            var declarations = new List<AsmdefRecord>();
            foreach (string path in assetPaths
                         .Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!FrameworkModuleSourceCatalog.TryResolve(
                        path, out FrameworkModuleSourceCatalog.SourceLocation source, out string reason) ||
                    !File.Exists(source.PhysicalPath))
                {
                    capture.AddIssue("asmdef-unreadable", $"{path}：{reason}");
                    continue;
                }

                AsmdefJson dto;
                try
                {
                    dto = JsonUtility.FromJson<AsmdefJson>(File.ReadAllText(source.PhysicalPath));
                }
                catch (Exception ex)
                {
                    capture.AddIssue("asmdef-invalid", $"{path}：{ex.Message}");
                    continue;
                }
                if (dto == null || string.IsNullOrWhiteSpace(dto.name))
                {
                    capture.AddIssue("asmdef-missing-name", path + "：缺少有效程序集名。");
                    continue;
                }

                var record = new AsmdefRecord
                {
                    Dto = dto,
                    Source = source,
                    PlatformScope = ClassifyPlatformScope(dto),
                };
                declarations.Add(record);
                capture.AddSource(CreateDependencySource(dto.name, source, false));
            }

            foreach (PluginImporter importer in pluginImporters
                         .OrderBy(importer => importer.assetPath, StringComparer.Ordinal))
            {
                if (importer == null || importer.isNativePlugin ||
                    !importer.assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!FrameworkModuleSourceCatalog.TryResolve(
                        importer.assetPath, out var source, out string sourceReason))
                {
                    capture.AddIssue("managed-plugin-source-unresolved",
                        $"{importer.assetPath}：{sourceReason}");
                    continue;
                }
                pluginIdentitiesByAssetPath.TryGetValue(importer.assetPath, out string identity);
                if (string.IsNullOrWhiteSpace(identity))
                {
                    capture.AddIssue("managed-plugin-identity-unreadable",
                        $"{importer.assetPath}：无法读取托管 DLL AssemblyName；若它不是托管插件，请检查 PluginImporter 类型。 ");
                    continue;
                }
                bool editorCompatible;
                bool playerCompatible;
                string[] compatibleBuildTargets;
                try
                {
                    editorCompatible = importer.GetCompatibleWithEditor();
                    compatibleBuildTargets = buildTargets
                        .Where(importer.GetCompatibleWithPlatform)
                        .Select(target => target.ToString())
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    playerCompatible = compatibleBuildTargets.Contains(
                        EditorUserBuildSettings.activeBuildTarget.ToString(), StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    capture.AddIssue("managed-plugin-platform-unreadable",
                        $"{importer.assetPath}：无法读取平台兼容性（{ex.Message}）。 ", identity);
                    editorCompatible = false;
                    playerCompatible = false;
                    compatibleBuildTargets = Array.Empty<string>();
                }
                capture.AddSource(CreateDependencySource(
                    identity, source, true, true, editorCompatible, playerCompatible,
                    compatibleBuildTargets));
            }

            foreach (var pair in referencePaths.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (capture.Sources.ContainsKey(pair.Key)) continue;
                if (FrameworkModuleSourceCatalog.TryResolve(pair.Value, out var source, out _))
                    capture.AddSource(CreateDependencySource(pair.Key, source, true));
                else
                    capture.Sources[pair.Key] = new DependencySource
                    {
                        AssemblyName = pair.Key,
                        PhysicalPath = pair.Value,
                        SourceKind = FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                        IsPrecompiledAssembly = true,
                        IsExternal = true,
                    };
            }

            // 预编译 DLL 也可能引用另一个预编译 DLL。只看一方编译输出会漏掉这段传递链，
            // 进而把真实依赖误判成“没有消费者”。同一平台互斥变体分别读取，Finish 再去重。
            foreach (DependencySource source in capture.Sources.Values
                         .Where(source => source.IsExternal && source.IsPrecompiledAssembly)
                         .OrderBy(source => source.AssemblyName, StringComparer.Ordinal)
                         .ToArray())
            {
                foreach (ActualConsumerEvidence edge in ReadPrecompiledActualConsumers(
                             source, issue => capture.AddIssue(
                                 issue.Code, issue.Message, issue.SubjectAssemblyName)))
                    capture.AddActual(edge);
            }

            foreach (AsmdefRecord record in declarations.Where(IsFirstPartyConsumer))
            {
                foreach (string configured in record.Dto.references ?? Array.Empty<string>())
                {
                    string dependency = NormalizeDeclaredReference(configured);
                    if (dependency.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                    {
                        capture.AddIssue("asmdef-guid-unresolved",
                            $"{record.Source.AssetPath}：无法还原 {configured}。");
                        continue;
                    }
                    capture.AddDeclared(record, configured, dependency,
                        DeclaredReferenceKind.AssemblyDefinition);
                }

                if (!record.Dto.overrideReferences) continue;
                foreach (string configured in record.Dto.precompiledReferences ?? Array.Empty<string>())
                {
                    string file = Path.GetFileName(configured?.Trim() ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(file) ||
                        !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        !precompiledIdentities.TryGetValue(file, out string dependency) ||
                        string.IsNullOrWhiteSpace(dependency))
                    {
                        capture.AddIssue("precompiled-reference-unresolved",
                            $"{record.Source.AssetPath}：有效 precompiledReferences 无法映射到 DLL AssemblyName（{configured}）。");
                        continue;
                    }
                    capture.AddDeclared(record, configured, dependency,
                        DeclaredReferenceKind.PrecompiledAssembly);
                }
            }

            foreach (var pair in playerAssemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AssemblyInfo consumer = pair.Value;
                var source = capture.Sources.TryGetValue(consumer.Name, out DependencySource known)
                    ? known
                    : null;
                capture.AddActualReferences(
                    consumer.Name,
                    consumer.AsmdefPath,
                    source,
                    ConsumerPlatformScope.Player,
                    consumer.ActualReferences);
            }

            foreach (UnityEditor.Compilation.Assembly assembly in editorAssemblies
                         .OrderBy(item => item.name, StringComparer.Ordinal))
            {
                string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                FrameworkModuleSourceCatalog.SourceLocation source = null;
                bool predefinedAssembly = string.IsNullOrWhiteSpace(path);
                if (!predefinedAssembly &&
                    !FrameworkModuleSourceCatalog.TryResolve(path, out source, out string sourceReason))
                    capture.AddIssue("editor-assembly-source-unresolved",
                        $"{assembly.name}（{path}）：{sourceReason}", assembly.name);
                bool firstParty = predefinedAssembly ||
                                  source?.Kind == FrameworkModuleSourceCatalog.SourceKind.ProjectAssets ||
                                  IsFrameworkAssembly(assembly.name);
                AsmdefRecord record = declarations.FirstOrDefault(item =>
                    item.Dto.name.Equals(assembly.name, StringComparison.Ordinal));
                string output = FullPath(assembly.outputPath);
                if (!File.Exists(output))
                {
                    capture.AddIssue("editor-assembly-missing",
                        $"{assembly.name}：当前 Editor DLL 快照不存在（{output}）。",
                        firstParty ? string.Empty : assembly.name);
                    continue;
                }
                DependencySource consumerSource = source != null
                    ? CreateDependencySource(assembly.name, source, false)
                    : predefinedAssembly
                        ? null
                        : new DependencySource
                        {
                            AssemblyName = assembly.name,
                            AssetPath = path,
                            SourceKind = FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                            IsExternal = true,
                        };
                if (consumerSource != null) capture.AddSource(consumerSource);
                string[] references;
                try
                {
                    references = ReadAssemblyReferences(output);
                }
                catch (Exception ex)
                {
                    capture.AddIssue("editor-assembly-metadata-unreadable",
                        $"{assembly.name}（{output}）：{ex.Message}",
                        firstParty ? string.Empty : assembly.name);
                    continue;
                }
                capture.AddActualReferences(
                    assembly.name,
                    source?.AssetPath ?? path ?? string.Empty,
                    consumerSource,
                    ClassifyEditorSnapshotScope(record?.PlatformScope ?? ConsumerPlatformScope.Unknown),
                    references);
            }

            capture.Finish();
            return capture;
        }

        internal static ActualConsumerEvidence[] ReadPrecompiledActualConsumers(
            DependencySource source,
            Action<EvidenceIssue> issueSink = null)
        {
            if (source == null || !source.IsPrecompiledAssembly) return Array.Empty<ActualConsumerEvidence>();
            DependencySourceVariant[] variants = source.Variants.Length > 0
                ? source.Variants
                : source.AllPhysicalPaths.Select(path => new DependencySourceVariant
                {
                    PhysicalPath = path,
                }).ToArray();
            var result = new List<ActualConsumerEvidence>();
            foreach (DependencySourceVariant variant in variants)
            {
                if (!File.Exists(variant.PhysicalPath))
                {
                    issueSink?.Invoke(new EvidenceIssue
                    {
                        Code = "precompiled-assembly-missing",
                        Message = $"{source.AssemblyName}：预编译 DLL 不存在（{variant.AssetPath} / {variant.PhysicalPath}）。 ",
                        SubjectAssemblyName = source.AssemblyName,
                    });
                    continue;
                }
                // 物理变体仍保留在来源目录中，但当前 Editor / active BuildTarget 均不兼容时，
                // 它不属于“当前已编译 DLL 快照”，不能把其引用边混入当前平台图。
                if (variant.HasCompatibilityEvidence && !variant.IsEditorCompatible &&
                    !variant.IsActiveBuildTargetCompatible)
                    continue;
                string[] references;
                try
                {
                    references = ReadAssemblyReferences(variant.PhysicalPath);
                }
                catch (Exception ex)
                {
                    issueSink?.Invoke(new EvidenceIssue
                    {
                        Code = "precompiled-assembly-metadata-unreadable",
                        Message = $"{source.AssemblyName}（{variant.AssetPath}）：{ex.Message}",
                        SubjectAssemblyName = source.AssemblyName,
                    });
                    continue;
                }
                ConsumerPlatformScope scope = !variant.HasCompatibilityEvidence
                    ? ConsumerPlatformScope.Unknown
                    : variant.IsEditorCompatible && variant.IsActiveBuildTargetCompatible
                        ? ConsumerPlatformScope.Mixed
                        : variant.IsEditorCompatible
                            ? ConsumerPlatformScope.Editor
                            : variant.IsActiveBuildTargetCompatible
                                ? ConsumerPlatformScope.Player
                                : ConsumerPlatformScope.Unknown;
                result.AddRange(references.Select(dependency => new ActualConsumerEvidence
                {
                    DependencyAssemblyName = dependency,
                    ConsumerAssemblyName = source.AssemblyName,
                    ConsumerAsmdefPath = variant.AssetPath,
                    ConsumerSourceKind = source.SourceKind,
                    ConsumerPackageName = source.PackageName,
                    PlatformScope = scope,
                }));
            }
            return result
                .GroupBy(edge => edge.DependencyAssemblyName + "\0" + edge.PlatformScope,
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(edge => edge.DependencyAssemblyName, StringComparer.Ordinal)
                .ThenBy(edge => edge.PlatformScope)
                .ToArray();
        }

        private static bool IsFirstPartyConsumer(AsmdefRecord record) =>
            record.Source.Kind == FrameworkModuleSourceCatalog.SourceKind.ProjectAssets ||
            IsFrameworkAssembly(record.Dto.name);

        private static DependencySource CreateDependencySource(
            string assemblyName,
            FrameworkModuleSourceCatalog.SourceLocation source,
            bool isPrecompiled,
            bool hasCompatibilityEvidence = false,
            bool isEditorCompatible = false,
            bool isPlayerCompatible = false,
            string[] compatibleBuildTargets = null) => new()
        {
            AssemblyName = assemblyName ?? string.Empty,
            AssetPath = source?.AssetPath ?? string.Empty,
            PhysicalPath = source?.PhysicalPath ?? string.Empty,
            PackageName = source?.PackageName ?? string.Empty,
            PackageVersion = source?.PackageVersion ?? string.Empty,
            PackageId = source?.PackageId ?? string.Empty,
            SourceKind = source?.Kind ?? FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
            HasPackageDirectness = source?.HasPackageDirectness ?? false,
            IsDirectPackageDependency = source?.IsDirectPackageDependency ?? false,
            IsPrecompiledAssembly = isPrecompiled,
            IsExternal = source == null || source.IsPackage || isPrecompiled,
            Variants = source == null
                ? Array.Empty<DependencySourceVariant>()
                : new[]
                {
                    new DependencySourceVariant
                    {
                        AssetPath = source.AssetPath ?? string.Empty,
                        PhysicalPath = source.PhysicalPath ?? string.Empty,
                        HasCompatibilityEvidence = hasCompatibilityEvidence,
                        IsEditorCompatible = isEditorCompatible,
                        IsActiveBuildTargetCompatible = isPlayerCompatible,
                        CompatibleBuildTargets = compatibleBuildTargets ?? Array.Empty<string>(),
                    },
                },
        };

        private static ConsumerPlatformScope ClassifyPlatformScope(AsmdefJson dto)
        {
            string[] constraints = dto?.defineConstraints ?? Array.Empty<string>();
            ConsumerPlatformScope constraintScope = ClassifyDefineConstraintScope(constraints);
            bool hasPlatformConstraint = constraints.Any(value =>
                value?.Contains("UNITY_EDITOR", StringComparison.Ordinal) == true ||
                value?.Contains("UNITY_INCLUDE_TESTS", StringComparison.Ordinal) == true);
            if (hasPlatformConstraint && constraintScope == ConsumerPlatformScope.Unknown)
                return ConsumerPlatformScope.Unknown;
            string[] includes = dto?.includePlatforms ?? Array.Empty<string>();
            bool includesEditor = includes.Any(platform =>
                platform.Equals("Editor", StringComparison.OrdinalIgnoreCase));
            ConsumerPlatformScope platformScope;
            if (includes.Length > 0)
                platformScope = includesEditor
                    ? includes.Length == 1 ? ConsumerPlatformScope.Editor : ConsumerPlatformScope.Mixed
                    : ConsumerPlatformScope.Player;
            else
            {
                string[] excludes = dto?.excludePlatforms ?? Array.Empty<string>();
                platformScope = excludes.Any(platform =>
                    platform.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                    ? ConsumerPlatformScope.Player
                    : ConsumerPlatformScope.Mixed;
            }

            return IntersectDeclaredPlatformScopes(constraintScope, platformScope);
        }

        /// <summary>
        /// CompilationPipeline 的 Editor 快照只证明 Editor 域中的引用；即使同一 asmdef 也参与
        /// Player 编译，这里的边仍不能反向冒充 Player 证据。Test 程序集保留更窄的 Tests 范围。
        /// </summary>
        internal static ConsumerPlatformScope ClassifyEditorSnapshotScope(
            ConsumerPlatformScope declaredScope) =>
            declaredScope == ConsumerPlatformScope.Tests
                ? ConsumerPlatformScope.Tests
                : ConsumerPlatformScope.Editor;

        internal static ConsumerPlatformScope ClassifyPlatformScopeForTests(
            string[] constraints,
            string[] includes,
            string[] excludes) => ClassifyPlatformScope(new AsmdefJson
        {
            defineConstraints = constraints,
            includePlatforms = includes,
            excludePlatforms = excludes,
        });

        internal static ConsumerPlatformScope ClassifyDefineConstraintScope(
            IEnumerable<string> constraints)
        {
            string[] values = (constraints ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray();
            if (values.Length == 0) return ConsumerPlatformScope.Unknown;
            if (values.Any(value => value.Contains("||") || value.Contains("&&") ||
                                    value.Contains('(') || value.Contains(')')))
                return ConsumerPlatformScope.Unknown;

            bool editor = values.Contains("UNITY_EDITOR", StringComparer.Ordinal);
            bool notEditor = values.Contains("!UNITY_EDITOR", StringComparer.Ordinal);
            bool tests = values.Contains("UNITY_INCLUDE_TESTS", StringComparer.Ordinal);
            bool notTests = values.Contains("!UNITY_INCLUDE_TESTS", StringComparer.Ordinal);
            if ((editor && notEditor) || (tests && notTests) || (tests && notEditor))
                return ConsumerPlatformScope.Unknown;
            if (tests) return ConsumerPlatformScope.Tests;
            if (editor) return ConsumerPlatformScope.Editor;
            if (notEditor) return ConsumerPlatformScope.Player;
            if (notTests) return ConsumerPlatformScope.Mixed;
            return ConsumerPlatformScope.Unknown;
        }

        private static ConsumerPlatformScope IntersectDeclaredPlatformScopes(
            ConsumerPlatformScope constraint,
            ConsumerPlatformScope platform)
        {
            if (constraint == ConsumerPlatformScope.Unknown) return platform;
            if (platform == ConsumerPlatformScope.Mixed) return constraint;
            if (constraint == ConsumerPlatformScope.Mixed) return platform;
            if (constraint == platform) return constraint;
            if (constraint == ConsumerPlatformScope.Tests && platform == ConsumerPlatformScope.Editor)
                return ConsumerPlatformScope.Tests;
            return ConsumerPlatformScope.Unknown;
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

        private static string[] GetDeclaredAssemblyReferences(AsmdefJson dto)
        {
            if (dto == null) return Array.Empty<string>();
            return NormalizeDeclaredReferences(dto.references);
        }

        private static string[] GetEffectivePrecompiledReferences(
            AsmdefJson dto,
            IReadOnlyDictionary<string, string> precompiledIdentities)
        {
            if (dto == null || !dto.overrideReferences) return Array.Empty<string>();
            // Unity 要求这里写带 .dll 后缀的 PluginImporter 文件名；无后缀字符串不是有效声明，
            // 配置按“文件名”，真实 AssemblyRef 按 DLL 内部 AssemblyName；两者允许合法地不同。
            return (dto.precompiledReferences ?? Array.Empty<string>())
                .Where(reference => reference?.Trim().EndsWith(
                    ".dll", StringComparison.OrdinalIgnoreCase) == true)
                .Select(reference => Path.GetFileName(reference.Trim()))
                .Where(file => precompiledIdentities != null && precompiledIdentities.ContainsKey(file))
                .Select(file => precompiledIdentities[file])
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToArray();
        }

        private static Dictionary<string, string> BuildPrecompiledReferenceIdentityMap(
            IEnumerable<PluginImporter> pluginImporters,
            out Dictionary<string, string> identitiesByAssetPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            identitiesByAssetPath = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PluginImporter importer in pluginImporters)
            {
                if (importer == null) continue;
                string file = Path.GetFileName(importer.assetPath);
                string identity = ReadManagedPluginAssemblyIdentity(importer);
                if (!string.IsNullOrWhiteSpace(importer.assetPath))
                    identitiesByAssetPath[importer.assetPath] = identity;
                if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(identity)) continue;
                if (result.TryGetValue(file, out string existing) &&
                    !existing.Equals(identity, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"PluginImporter 文件名 {file} 对应多个 AssemblyName：{existing}；{identity}。" +
                        "precompiledReferences 无法无歧义解析，请先消除同名 DLL。 ");
                result[file] = identity;
            }
            return result;
        }

        internal static string ReadManagedPluginAssemblyIdentity(PluginImporter importer)
        {
            if (importer == null || importer.isNativePlugin ||
                !importer.assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !FrameworkModuleSourceCatalog.TryResolve(importer.assetPath, out var source, out _))
                return string.Empty;
            return ReadManagedAssemblyIdentity(source.PhysicalPath);
        }

        internal static bool IsPrecompiledAssemblyReference(
            string reference,
            ISet<string> asmdefNames,
            ISet<string> precompiledAssemblyNames)
        {
            if (string.IsNullOrWhiteSpace(reference) ||
                reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                return false;
            if (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
            return (asmdefNames == null || !asmdefNames.Contains(reference)) &&
                   precompiledAssemblyNames?.Contains(reference) == true;
        }

        private static string[] NormalizeDeclaredReferences(IEnumerable<string> references)
        {
            return (references ?? Array.Empty<string>())
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
            // asmdef 的程序集名本来就常含点（R3.Unity / Google.Protobuf）；
            // Path.GetFileNameWithoutExtension 会把最后一段误当扩展名，只对 precompiledReferences 的 .dll 去后缀。
            return reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? reference.Substring(0, reference.Length - 4)
                : reference;
        }

        internal static bool IsEditorConstrained(string assemblyName)
        {
            string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            var dto = ReadAsmdef(path);
            ConsumerPlatformScope scope = ClassifyPlatformScope(dto);
            return scope is ConsumerPlatformScope.Editor or ConsumerPlatformScope.Tests;
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
                   name.Equals("Microsoft.CSharp", StringComparison.Ordinal) ||
                   name.Equals("Microsoft.CodeAnalysis", StringComparison.Ordinal) ||
                   name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal) ||
                   name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) ||
                   name.Equals("Mono.Posix", StringComparison.Ordinal) ||
                   name.Equals("UnityEngine", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                   name.Equals("UnityEditor", StringComparison.Ordinal) ||
                   name.StartsWith("Unity.CompilationPipeline.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEditor.", StringComparison.Ordinal);
        }

        internal static bool IsPlatformReference(Snapshot snapshot, string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            // UnityEngine/UnityEditor 模块由 asmdef 的引擎引用语义提供，不通过 references / precompiledReferences
            // 声明；即使它们的源码或二进制来自已注册 Package，也不能按普通第三方程序集判漏声明。
            if (name.Equals("UnityEngine", StringComparison.Ordinal) ||
                name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                name.Equals("UnityEditor", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor.", StringComparison.Ordinal) ||
                name.StartsWith("Unity.CompilationPipeline.", StringComparison.Ordinal))
                return true;

            // NuGet 也会提供 System.* DLL（例如 Protobuf 依赖的 Unsafe）。按名字会误当 BCL，
            // 因此已解析到 Assets / Package 的外部来源优先于名字规则；Editor-only DLL 可能不在 Player
            // ReferencePaths 中，仍不能被 System.* 前缀静默吞掉。
            if (snapshot.DependencySources.TryGetValue(name, out DependencySource source) &&
                source.IsExternal && source.IsKnown &&
                (!string.IsNullOrWhiteSpace(source.PackageName) ||
                 source.AllAssetPaths.Any(path =>
                     path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))))
                return false;

            // Unity/Bee 可能把 netstandard 等平台 DLL 复制到项目 Library 下；物理路径位于项目根内
            // 不能证明它是项目依赖。只有上面的 Source Catalog 能把 System.* 覆盖为外部来源。
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
            public bool overrideReferences;
            public bool autoReferenced = true;
            public string[] includePlatforms;
            public string[] excludePlatforms;
            public string[] defineConstraints;
        }

        private sealed class AsmdefRecord
        {
            internal AsmdefJson Dto;
            internal FrameworkModuleSourceCatalog.SourceLocation Source;
            internal ConsumerPlatformScope PlatformScope;
        }

        private sealed class DependencyCapture
        {
            private readonly List<DeclaredConsumerEvidence> _declared = new();
            private readonly List<ActualConsumerEvidence> _actual = new();
            private readonly List<EvidenceIssue> _issues = new();

            internal readonly Dictionary<string, DependencySource> Sources =
                new(StringComparer.Ordinal);
            internal DeclaredConsumerEvidence[] DeclaredConsumers = Array.Empty<DeclaredConsumerEvidence>();
            internal ActualConsumerEvidence[] ActualConsumers = Array.Empty<ActualConsumerEvidence>();
            internal EvidenceIssue[] Issues = Array.Empty<EvidenceIssue>();
            internal Dictionary<string, string[]> DeclaredConsumersByDependency =
                new(StringComparer.Ordinal);

            internal void AddSource(DependencySource candidate)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.AssemblyName)) return;
                if (!Sources.TryGetValue(candidate.AssemblyName, out DependencySource existing))
                {
                    Sources.Add(candidate.AssemblyName, candidate);
                    return;
                }
                if (!existing.IsKnown && candidate.IsKnown)
                {
                    MergeVariants(candidate, existing, validateCompatibility: false);
                    Sources[candidate.AssemblyName] = candidate;
                    return;
                }
                if (existing.IsKnown && !candidate.IsKnown) return;
                if (string.Equals(existing.AssetPath, candidate.AssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    MergeVariants(existing, candidate, validateCompatibility: false);
                    return;
                }

                bool samePackage = !string.IsNullOrWhiteSpace(existing.PackageName) &&
                                   existing.PackageName.Equals(candidate.PackageName, StringComparison.Ordinal);
                if (samePackage)
                {
                    if (existing.SourceKind != candidate.SourceKind ||
                        !string.Equals(existing.PackageVersion, candidate.PackageVersion,
                            StringComparison.Ordinal) ||
                        existing.HasPackageDirectness != candidate.HasPackageDirectness ||
                        existing.HasPackageDirectness &&
                        existing.IsDirectPackageDependency != candidate.IsDirectPackageDependency)
                        AddIssue("package-source-inconsistent",
                            $"{candidate.AssemblyName} 的 Package 来源证据不一致：" +
                            $"{existing.AssetPath}；{candidate.AssetPath}。 ",
                            candidate.AssemblyName);
                    MergeVariants(existing, candidate, validateCompatibility: false);
                    return;
                }

                bool bothAssetsDll = existing.SourceKind ==
                                     FrameworkModuleSourceCatalog.SourceKind.ProjectAssets &&
                                     candidate.SourceKind ==
                                     FrameworkModuleSourceCatalog.SourceKind.ProjectAssets &&
                                     existing.IsPrecompiledAssembly && candidate.IsPrecompiledAssembly;
                if (bothAssetsDll)
                {
                    MergeVariants(existing, candidate, validateCompatibility: true);
                    return;
                }
                AddIssue("assembly-source-ambiguous",
                    $"AssemblyName {candidate.AssemblyName} 对应多个来源：{existing.AssetPath}；{candidate.AssetPath}。",
                    candidate.AssemblyName);
            }

            private void MergeVariants(
                DependencySource target,
                DependencySource incoming,
                bool validateCompatibility)
            {
                var merged = target.Variants.ToList();
                foreach (DependencySourceVariant variant in incoming.Variants)
                {
                    int samePath = merged.FindIndex(item =>
                        item.AssetPath.Equals(variant.AssetPath, StringComparison.OrdinalIgnoreCase) &&
                        item.PhysicalPath.Equals(variant.PhysicalPath, StringComparison.OrdinalIgnoreCase));
                    if (samePath >= 0)
                    {
                        if (!merged[samePath].HasCompatibilityEvidence &&
                            variant.HasCompatibilityEvidence)
                            merged[samePath] = variant;
                        continue;
                    }

                    if (validateCompatibility && merged.Any(existing =>
                            !ArePlatformExclusive(existing, variant)))
                        AddIssue("assembly-source-ambiguous",
                            $"AssemblyName {target.AssemblyName} 的 Assets DLL 变体平台范围重叠或无法证明互斥：" +
                            $"{target.AssetPath}；{variant.AssetPath}。 ", target.AssemblyName);
                    merged.Add(variant);
                }
                target.Variants = merged
                    .OrderBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.PhysicalPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            private static bool ArePlatformExclusive(
                DependencySourceVariant left,
                DependencySourceVariant right) =>
                AreDependencySourceVariantsPlatformExclusive(left, right);

            internal void AddDeclared(
                AsmdefRecord consumer,
                string configured,
                string dependency,
                DeclaredReferenceKind kind)
            {
                if (string.IsNullOrWhiteSpace(dependency) ||
                    dependency.Equals(consumer.Dto.name, StringComparison.Ordinal))
                    return;
                _declared.Add(new DeclaredConsumerEvidence
                {
                    DependencyAssemblyName = dependency,
                    ConfiguredReference = configured?.Trim() ?? string.Empty,
                    ReferenceKind = kind,
                    ConsumerAssemblyName = consumer.Dto.name,
                    ConsumerAsmdefPath = consumer.Source.AssetPath,
                    ConsumerSourceKind = consumer.Source.Kind,
                    ConsumerPackageName = consumer.Source.PackageName,
                    PlatformScope = consumer.PlatformScope,
                });
                if (!Sources.ContainsKey(dependency))
                    Sources[dependency] = new DependencySource
                    {
                        AssemblyName = dependency,
                        SourceKind = FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                        IsExternal = true,
                    };
            }

            internal void AddActualReferences(
                string consumerName,
                string asmdefPath,
                DependencySource consumerSource,
                ConsumerPlatformScope scope,
                IEnumerable<string> references)
            {
                foreach (string dependency in references ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(dependency) ||
                        dependency.Equals(consumerName, StringComparison.Ordinal))
                        continue;
                    AddActual(new ActualConsumerEvidence
                    {
                        DependencyAssemblyName = dependency,
                        ConsumerAssemblyName = consumerName ?? string.Empty,
                        ConsumerAsmdefPath = asmdefPath ?? string.Empty,
                        ConsumerSourceKind = consumerSource?.SourceKind ??
                                             FrameworkModuleSourceCatalog.SourceKind.ProjectAssets,
                        ConsumerPackageName = consumerSource?.PackageName ?? string.Empty,
                        PlatformScope = scope,
                    });
                }
            }

            internal void AddActual(ActualConsumerEvidence edge)
            {
                if (edge == null || string.IsNullOrWhiteSpace(edge.DependencyAssemblyName) ||
                    edge.DependencyAssemblyName.Equals(edge.ConsumerAssemblyName, StringComparison.Ordinal))
                    return;
                _actual.Add(edge);
                if (!Sources.ContainsKey(edge.DependencyAssemblyName))
                    Sources[edge.DependencyAssemblyName] = new DependencySource
                    {
                        AssemblyName = edge.DependencyAssemblyName,
                        SourceKind = FrameworkModuleSourceCatalog.SourceKind.UnknownPackage,
                        IsExternal = true,
                    };
            }

            internal void AddIssue(string code, string message, string subjectAssemblyName = "")
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                _issues.Add(new EvidenceIssue
                {
                    Code = code ?? string.Empty,
                    Message = message,
                    SubjectAssemblyName = subjectAssemblyName ?? string.Empty,
                });
            }

            internal void Finish()
            {
                DeclaredConsumers = _declared
                    .GroupBy(item => item.DependencyAssemblyName + "\0" + item.ConsumerAssemblyName + "\0" +
                                     item.ReferenceKind, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(item => item.DependencyAssemblyName, StringComparer.Ordinal)
                    .ThenBy(item => item.ConsumerAssemblyName, StringComparer.Ordinal)
                    .ToArray();
                ActualConsumers = _actual
                    .GroupBy(item => item.DependencyAssemblyName + "\0" + item.ConsumerAssemblyName + "\0" +
                                     item.PlatformScope, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(item => item.DependencyAssemblyName, StringComparer.Ordinal)
                    .ThenBy(item => item.ConsumerAssemblyName, StringComparer.Ordinal)
                    .ToArray();
                Issues = _issues
                    .GroupBy(issue => issue.Code + "\0" + issue.SubjectAssemblyName + "\0" +
                                      issue.Message, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(issue => issue.Code, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                    .ToArray();
                DeclaredConsumersByDependency = DeclaredConsumers
                    .GroupBy(item => item.DependencyAssemblyName, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(item => item.ConsumerAssemblyName)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .ToArray(),
                        StringComparer.Ordinal);
            }
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
