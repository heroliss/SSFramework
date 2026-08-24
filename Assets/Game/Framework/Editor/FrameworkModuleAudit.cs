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

        internal static string CreateReport(Snapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var runtimeModules = snapshot.Assemblies.Values
                .Where(info => info.IsFrameworkRuntime)
                .OrderBy(info => info.Name, StringComparer.Ordinal)
                .ToArray();
            var sb = new StringBuilder(8192);
            sb.AppendLine("Framework Module 裁剪审计");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine("证据：当前目标平台的 Player 编译图 + 当前已编译 DLL 的真实元数据引用。");
            sb.AppendLine("口径：下列字节数均为链接 / AOT / 压缩前的原始托管 DLL，不等于最终安装包增量；最终以 Player BuildReport 为准。");
            sb.AppendLine();

            sb.AppendLine($"运行时 Framework Module：{runtimeModules.Length} 个");
            foreach (var module in runtimeModules)
            {
                string auto = module.AutoReferenced ? "⚠ autoReferenced:true" : "autoReferenced:false";
                sb.AppendLine($"  • {module.Name}  {FormatBytes(module.OutputBytes)}  {auto}");
            }
            sb.AppendLine();

            AppendDependencyVisibility(sb, snapshot, runtimeModules);
            AppendPreset(sb, snapshot, "轻量 · Core-only", new[] { CoreAssemblyName });
            AppendPreset(sb, snapshot, "标准 · Core + UGUI", new[] { UGuiAssemblyName });
            AppendPreset(sb, snapshot, "标准 · Core + UI Toolkit", new[] { ToolkitAssemblyName });
            AppendPreset(sb, snapshot, "完整 · 全部 Runtime Module", runtimeModules.Select(module => module.Name));

            sb.AppendLine("当前热更档位");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine("  " + snapshot.HotUpdateNote);
            if (snapshot.HotUpdateRoots.Length > 0)
                AppendFootprint(sb, snapshot, snapshot.HotUpdateRoots, indent: "  ");
            sb.AppendLine();

            AppendDeletionTests(sb, snapshot);
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
            Snapshot snapshot,
            IEnumerable<AssemblyInfo> runtimeModules)
        {
            sb.AppendLine("依赖可见性");
            sb.AppendLine("────────────────────────────────────────");
            int issueCount = 0;
            foreach (var module in runtimeModules)
            {
                var hidden = FindUndeclaredExternalReferences(snapshot, module);
                if (hidden.Length == 0) continue;
                issueCount += hidden.Length;
                sb.AppendLine($"  ⚠ {module.Name} 的真实外部引用未在 asmdef 显式声明：{string.Join(", ", hidden)}");
            }
            if (issueCount == 0)
                sb.AppendLine("  ✓ 所有 Runtime Module 的真实外部引用都能从 asmdef 直接读出。");
            else
                sb.AppendLine($"  共 {issueCount} 条隐式引用；这不等于运行时错误，但会削弱删除测试、UPM 依赖声明与 AI 可导航性。");
            sb.AppendLine();
        }

        private static void AppendPreset(StringBuilder sb, Snapshot snapshot, string title, IEnumerable<string> roots)
        {
            sb.AppendLine(title);
            sb.AppendLine("────────────────────────────────────────");
            AppendFootprint(sb, snapshot, roots, indent: "  ");
            sb.AppendLine();
        }

        private static void AppendFootprint(StringBuilder sb, Snapshot snapshot, IEnumerable<string> roots, string indent)
        {
            string[] rootArray = roots.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var footprint = Measure(snapshot, rootArray);
            sb.AppendLine(indent + "入口：" + (rootArray.Length == 0 ? "（无）" : string.Join(", ", rootArray)));
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

        private static void AppendDeletionTests(StringBuilder sb, Snapshot snapshot)
        {
            sb.AppendLine("删除测试（真实元数据引用闭包）");
            sb.AppendLine("────────────────────────────────────────");
            var core = ComputeReachableAssemblies(snapshot.Assemblies, new[] { CoreAssemblyName });
            var ugui = ComputeReachableAssemblies(snapshot.Assemblies, new[] { UGuiAssemblyName });
            var toolkit = ComputeReachableAssemblies(snapshot.Assemblies, new[] { ToolkitAssemblyName });

            AppendDeletionResult(sb, "Core-only 不带 UI",
                !core.Any(name => name.Equals(SharedUiAssemblyName, StringComparison.Ordinal) ||
                                  name.StartsWith(SharedUiAssemblyName + ".", StringComparison.Ordinal)));
            AppendDeletionResult(sb, "UGUI 不带 Toolkit / Bridge",
                !ugui.Contains(ToolkitAssemblyName) && !ugui.Contains(BridgeAssemblyName));
            AppendDeletionResult(sb, "Toolkit 不带 UGUI / Bridge",
                !toolkit.Contains(UGuiAssemblyName) && !toolkit.Contains(BridgeAssemblyName));
            sb.AppendLine("  注：通过只证明程序集依赖方向成立，不证明最终玩家包已达到体积预算。");
        }

        private static void AppendDeletionResult(StringBuilder sb, string name, bool passed)
            => sb.AppendLine($"  {(passed ? "✓" : "✗")} {name}");

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

        private static string FormatBytes(long bytes)
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
