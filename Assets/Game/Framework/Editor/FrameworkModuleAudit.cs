using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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

            internal Snapshot(
                Dictionary<string, AssemblyInfo> assemblies,
                Dictionary<string, string> referencePaths,
                string[] hotUpdateRoots,
                string hotUpdateNote)
            {
                Assemblies = assemblies;
                ReferencePaths = referencePaths;
                HotUpdateRoots = hotUpdateRoots;
                HotUpdateNote = hotUpdateNote;
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
        /// 一次审计的结构化结果；所有展示层都从这里取数，避免文本报告与窗口结论各算一套。
        /// </summary>
        internal sealed class AuditResult
        {
            internal AssemblyInfo[] RuntimeModules = Array.Empty<AssemblyInfo>();
            internal DependencyIssue[] DependencyIssues = Array.Empty<DependencyIssue>();
            internal AuditProfile[] CommonProfiles = Array.Empty<AuditProfile>();
            internal AuditProfile FullProfile;
            internal AuditProfile HotUpdateProfile;
            internal string HotUpdateNote;
            internal DeletionCheck[] DeletionChecks = Array.Empty<DeletionCheck>();
            internal string[] Recommendations = Array.Empty<string>();
            internal bool AllRuntimeModulesOptIn;

            internal IEnumerable<AuditProfile> AllProfiles => CommonProfiles
                .Concat(FullProfile != null ? new[] { FullProfile } : Array.Empty<AuditProfile>())
                .Concat(HotUpdateProfile != null ? new[] { HotUpdateProfile } : Array.Empty<AuditProfile>());

            internal bool HasUnresolvedAssemblies =>
                AllProfiles.Any(profile => profile.Footprint.UnresolvedAssemblies.Count > 0);

            internal bool IsHealthy => DependencyIssues.Length == 0 &&
                                       AllRuntimeModulesOptIn &&
                                       !HasUnresolvedAssemblies &&
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
                string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                var dto = ReadAsmdef(asmdefPath);
                string outputPath = FullPath(assembly.outputPath);
                infos[assembly.name] = new AssemblyInfo
                {
                    Name = assembly.name,
                    AsmdefPath = asmdefPath ?? string.Empty,
                    OutputPath = outputPath,
                    OutputBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L,
                    AutoReferenced = dto?.autoReferenced ?? true,
                    DeclaredReferences = GetDeclaredReferences(dto),
                    ActualReferences = ReadAssemblyReferences(outputPath),
                };
            }

            var (hotRoots, hotNote) = ReadHotUpdateRoots();
            return new Snapshot(infos, referencePaths, hotRoots, hotNote);
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
            var fullProfile = Profile(
                "full", "全部运行时模块", "用于查看能力上限，不代表推荐所有项目全部引入。",
                runtimeModules.Select(module => module.Name));
            AuditProfile hotUpdateProfile = snapshot.HotUpdateRoots.Length > 0
                ? Profile("hot-update", "当前热更配置", "HybridCLR 以程序集为最小热更粒度。",
                    snapshot.HotUpdateRoots)
                : null;

            var coreClosure = ComputeReachableAssemblies(snapshot.Assemblies, new[] { CoreAssemblyName });
            var uguiClosure = ComputeReachableAssemblies(snapshot.Assemblies, new[] { UGuiAssemblyName });
            var toolkitClosure = ComputeReachableAssemblies(snapshot.Assemblies, new[] { ToolkitAssemblyName });
            var result = new AuditResult
            {
                RuntimeModules = runtimeModules,
                DependencyIssues = dependencyIssues,
                CommonProfiles = commonProfiles,
                FullProfile = fullProfile,
                HotUpdateProfile = hotUpdateProfile,
                HotUpdateNote = snapshot.HotUpdateNote,
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
            sb.AppendLine(result.IsHealthy
                ? "结论：当前模块边界健康，没有发现会阻碍按需裁剪的问题。"
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
            foreach (var profile in result.CommonProfiles)
                AppendProfile(sb, profile);
            AppendProfile(sb, result.FullProfile);

            sb.AppendLine("当前热更档位");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine("  " + result.HotUpdateNote);
            if (result.HotUpdateProfile != null)
                AppendFootprint(sb, result.HotUpdateProfile, indent: "  ");
            sb.AppendLine();

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

            if (result.IsHealthy)
            {
                recommendations.Add("当前边界允许从“只用核心”开始，再按需增加一个 UI 后端；不要为了备用能力把全部模块都放进业务 asmdef 或热更清单。");

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

        private static (string[] roots, string note) ReadHotUpdateRoots()
        {
            Type profileType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Game.Framework.Build.FrameworkHotUpdateProfile", false))
                .FirstOrDefault(type => type != null);
            if (profileType == null)
                return (Array.Empty<string>(), "未安装热更构建 Module；按纯 AOT 理解。");

            string[] guids = AssetDatabase.FindAssets("t:" + profileType.Name);
            if (guids.Length == 0)
                return (Array.Empty<string>(), "未找到 FrameworkHotUpdateProfile；未擅自创建配置资产。");

            string path = AssetDatabase.GUIDToAssetPath(guids.OrderBy(guid => AssetDatabase.GUIDToAssetPath(guid),
                StringComparer.Ordinal).First());
            var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
            var property = profileType.GetProperty("HotUpdateAssemblyNames", BindingFlags.Instance | BindingFlags.Public);
            if (profile == null || property?.GetValue(profile) is not IEnumerable<string> names)
                return (Array.Empty<string>(), $"无法读取热更 Profile：{path}");

            string[] roots = names.Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string note = roots.Length == 0
                ? $"{path}：纯 AOT 档位。"
                : $"{path}：{roots.Length} 个热更入口；HybridCLR 以程序集为最小粒度。";
            return (roots, note);
        }

        private static AsmdefJson ReadAsmdef(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<AsmdefJson>(File.ReadAllText(path));
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
