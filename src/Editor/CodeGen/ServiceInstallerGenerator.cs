using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Framework.Context;
// 不 using UnityEditor.Compilation：它的 Assembly 与 System.Reflection.Assembly 歧义（CS0104）——该命名空间只用一处，全限定调用。
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 服务安装器生成器（ADR-0019）：扫描 <see cref="ServiceInstallerProfile"/> 条目声明的目录，
    /// 把其中的纯 C# 服务类生成为一份显式的静态安装器（<c>Install(ContainerBuilder)</c>），
    /// 替代手写 <c>builder.RegisterValue(new Xxx(), ...)</c> 样板——注册关系落在 .g.cs 里 git diff 可见可审，
    /// 运行时零反射扫描（AOT / 热更友好）。
    ///
    /// <para><b>扫描口径</b>（全部满足才入选）：目录下「文件名 = 类名」的顶层非抽象非泛型 class；
    /// 实现<b>恰一个</b>层标记（<c>IModel</c> / <c>ISystem</c> / <c>IUtility</c>）的派生接口体系；
    /// 非 <c>UnityEngine.Object</c> 派生（Mono 层走场景自动注册）；有公共无参构造；
    /// 未标 <see cref="ExcludeFromInstallerAttribute"/>。不满足的服务回落手写注册。</para>
    ///
    /// <para><b>契约口径与 Mono 路径一致</b>：具体类型 + 所有派生自对应层标记的接口（不含标记本身）。
    /// <c>IDisposable</c> 服务用 <c>RegisterOwned</c>（随 Context Dispose），其余 <c>RegisterValue</c>。
    /// 同一安装器内两个实现推导出同一接口契约 → 生成失败并列出冲突（构建期绑定是静默后覆盖先，生成场景不允许）。</para>
    ///
    /// <para>基于反射类型扫描（非语法树），须在编译通过后执行。内容不变时不写盘（无资产 diff、不触发重编译）。</para>
    /// </summary>
    public static class ServiceInstallerGenerator
    {
        internal readonly struct GenerationPrerequisiteReport
        {
            internal bool CanGenerate { get; }
            internal int ReadyEntryCount { get; }
            internal int TotalEntryCount { get; }
            internal string Message { get; }
            internal bool HasInvalidEntries => ReadyEntryCount < TotalEntryCount;

            internal GenerationPrerequisiteReport(
                bool canGenerate,
                int readyEntryCount,
                int totalEntryCount,
                string message)
            {
                CanGenerate = canGenerate;
                ReadyEntryCount = readyEntryCount;
                TotalEntryCount = totalEntryCount;
                Message = message;
            }
        }

        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static readonly Type[] LayerMarkers =
        {
            typeof(Game.Framework.Model.IModel),
            typeof(Game.Framework.Systems.ISystem),
            typeof(Game.Framework.Utility.IUtility),
        };

        /// <summary>
        /// 生成 profile 里全部条目。返回 (是否全部成功, 人类可读摘要)——交互外壳（菜单 / Inspector 按钮）
        /// 拿摘要展示，本方法不弹窗。输出路径无效、空条目或全局输出冲突属于写入安全问题，会在写盘前整批拒绝；
        /// 通过输出所有权预检后，命名空间、扫描目录或反射扫描等单条失败不阻断其余条目。
        /// </summary>
        public static (bool ok, string message) Generate(ServiceInstallerProfile profile)
        {
            if (profile == null) return (false, "ServiceInstallerProfile 不能为空。");
            var ownershipProfiles = ServiceInstallerProfile.ResolveAll().Concat(new[] { profile }).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ValidateOutputOwnership(ownershipProfiles);
            if (!ownershipOk) return (false, ownershipMessage);
            GenerationPrerequisiteReport prerequisites = InspectGenerationPrerequisites(profile);
            if (!prerequisites.CanGenerate) return (false, prerequisites.Message);

            return GenerateEntriesIndependently(profile.Installers, GenerateEntry);
        }

        /// <summary>
        /// 逐条调用生成 Implementation 并汇总结果。已知失败与未预期异常都只归属于当前条目，
        /// 后续条目仍会执行；这样一份 Profile 中的反射或 IO 故障不会让其他独立输出保持旧版本。
        /// </summary>
        internal static (bool ok, string message) GenerateEntriesIndependently(
            IReadOnlyList<ServiceInstallerProfile.InstallerEntry> entries,
            Func<ServiceInstallerProfile.InstallerEntry, (bool ok, string message)> generateEntry)
        {
            if (entries == null || entries.Count == 0)
                return (false, "没有可生成的安装器条目。");
            if (generateEntry == null)
                throw new ArgumentNullException(nameof(generateEntry));

            bool allOk = true;
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                bool ok;
                string message;
                try
                {
                    (ok, message) = generateEntry(entries[i]);
                }
                catch (Exception exception)
                {
                    ok = false;
                    string output = entries[i] == null || string.IsNullOrWhiteSpace(entries[i].OutputPath)
                        ? $"第 {i + 1} 条"
                        : entries[i].OutputPath;
                    message = $"{output}：发生未预期错误：{exception.GetType().Name}: {exception.Message}";
                }
                allOk &= ok;
                if (i > 0) sb.AppendLine();
                sb.Append(ok ? "✓ " : "✗ ").Append(message);
            }
            return (allOk, sb.ToString());
        }

        /// <summary>
        /// 组合 Inspector/总览按钮共同依赖的三项事实。这里只做 owner Module 内的廉价 UI evaluator；
        /// 点击后的动作层仍重新检查 Gate、输出所有权和条目输入。
        /// </summary>
        internal static bool CanStartGenerationAction(
            bool canWrite,
            bool ownershipOk,
            int readyWorkCount)
            => canWrite && ownershipOk && readyWorkCount > 0;

        /// <summary>
        /// 只读检查一份 Profile 是否具备开始扫描的廉价前置条件：至少有一个条目同时具备安全输出文件、
        /// 命名空间和有效文件夹资产。报告会保留已就绪/总条目数，使 UI 能提示“部分可生成”；
        /// 不会反射扫描类型、创建目录或写生成文件。
        /// </summary>
        internal static GenerationPrerequisiteReport InspectGenerationPrerequisites(
            ServiceInstallerProfile profile)
        {
            if (profile == null)
            {
                return new GenerationPrerequisiteReport(
                    canGenerate: false,
                    readyEntryCount: 0,
                    totalEntryCount: 0,
                    message: "ServiceInstallerProfile 不能为空。");
            }
            if (profile.Installers == null || profile.Installers.Count == 0)
            {
                return new GenerationPrerequisiteReport(
                    canGenerate: false,
                    readyEntryCount: 0,
                    totalEntryCount: 0,
                    message: $"profile「{profile.name}」没有任何安装器条目，无可生成。");
            }

            int readyCount = 0;
            string firstFailure = string.Empty;
            for (int i = 0; i < profile.Installers.Count; i++)
            {
                var (ok, message) = ValidateEntryPrerequisites(profile.Installers[i], i);
                if (ok)
                    readyCount++;
                else if (firstFailure.Length == 0)
                    firstFailure = message;
            }

            int totalCount = profile.Installers.Count;
            string summary = readyCount == totalCount
                ? $"{readyCount} 个安装器条目具备生成前置条件。"
                : readyCount == 0
                    ? $"没有具备生成前置条件的安装器条目。首项原因：{firstFailure}"
                    : $"{readyCount}/{totalCount} 个条目可生成；其余条目会逐条报告失败。首项原因：{firstFailure}";
            return new GenerationPrerequisiteReport(
                canGenerate: readyCount > 0,
                readyEntryCount: readyCount,
                totalEntryCount: totalCount,
                message: summary);
        }

        private static (bool ok, string message) ValidateEntryPrerequisites(
            ServiceInstallerProfile.InstallerEntry entry,
            int entryIndex)
        {
            string prefix = $"第 {entryIndex + 1} 条";
            if (entry == null) return (false, prefix + "安装器配置为空。");
            if (!FrameworkProjectPath.TryResolveAssetsFile(
                    entry.OutputPath, ".cs", out string outputPath, out _, out string outputError))
                return (false, $"{prefix}输出路径无效：{outputError}");
            if (string.IsNullOrWhiteSpace(entry.Namespace))
                return (false, $"{prefix}（{outputPath}）未配置命名空间。");
            if (!FrameworkCSharpSyntax.TryValidateNamespace(entry.Namespace.Trim(), out string namespaceError))
                return (false, $"{prefix}（{outputPath}）命名空间无效：{namespaceError}");

            bool hasValidFolder = (entry.ScanFolders ?? new List<DefaultAsset>())
                .Where(folder => folder != null)
                .Select(AssetDatabase.GetAssetPath)
                .Any(AssetDatabase.IsValidFolder);
            return hasValidFolder
                ? (true, string.Empty)
                : (false, $"{prefix}（{outputPath}）没有配置有效的扫描目录（文件夹资产）。");
        }

        /// <summary>
        /// 在批量写盘前验证所有安装器条目各自拥有唯一的规范化输出文件。两个配置即使通过 <c>..</c> 或不同分隔符
        /// 指向同一文件也会失败；校验阶段不扫描服务、不创建目录或文件。
        /// </summary>
        public static (bool ok, string message) ValidateOutputOwnership(
            IReadOnlyList<ServiceInstallerProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return (false, "没有可验证的 ServiceInstallerProfile。");

            var claims = new List<(ServiceInstallerProfile profile, int entryIndex, string assetPath, string absolutePath)>();
            foreach (ServiceInstallerProfile profile in profiles)
            {
                if (profile == null) return (false, "配置列表含已删除或空的 ServiceInstallerProfile。");
                if (profile.Installers == null) continue;
                for (int i = 0; i < profile.Installers.Count; i++)
                {
                    ServiceInstallerProfile.InstallerEntry entry = profile.Installers[i];
                    if (entry == null) return (false, $"【{profile.name}】第 {i + 1} 条安装器配置为空。");
                    if (!FrameworkProjectPath.TryResolveAssetsFile(
                            entry.OutputPath, ".cs", out string assetPath, out string absolutePath, out string error))
                        return (false, $"【{profile.name}】第 {i + 1} 条输出路径无效：{error}");
                    claims.Add((profile, i, assetPath, absolutePath));
                }
            }

            for (int i = 0; i < claims.Count; i++)
            for (int j = i + 1; j < claims.Count; j++)
            {
                var left = claims[i];
                var right = claims[j];
                if (!FrameworkProjectPath.PathsEqual(left.absolutePath, right.absolutePath)) continue;
                return (false,
                    $"安装器输出所有权冲突：【{left.profile.name}】第 {left.entryIndex + 1} 条与" +
                    $"【{right.profile.name}】第 {right.entryIndex + 1} 条都指向 {left.assetPath}。\n" +
                    "请为每个条目分配唯一 .g.cs 文件，避免后生成条目静默覆盖前一份。");
            }

            return (true, $"{claims.Count} 个安装器条目各自拥有唯一输出文件。");
        }

        /// <summary>
        /// 生成单个安装器条目。可预知的配置错误以结构化失败返回；反射、IO 或 Unity 导入的未预期异常可向上传播，
        /// Profile 级 <see cref="Generate"/> 会逐条捕获并继续生成后续独立条目。
        /// </summary>
        public static (bool ok, string message) GenerateEntry(ServiceInstallerProfile.InstallerEntry entry)
        {
            if (entry == null) return (false, "安装器条目不能为空。");
            if (!FrameworkProjectPath.TryResolveAssetsFile(
                    entry.OutputPath, ".cs", out string path, out string abs, out string pathError))
                return (false, "安装器输出路径无效：" + pathError);
            if (string.IsNullOrWhiteSpace(entry.Namespace))
                return (false, $"{path}：未配置命名空间。");
            if (!FrameworkCSharpSyntax.TryValidateNamespace(entry.Namespace.Trim(), out string namespaceError))
                return (false, $"{path}：命名空间无效：{namespaceError}");

            var folderPaths = (entry.ScanFolders ?? new List<DefaultAsset>())
                .Where(f => f != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(AssetDatabase.IsValidFolder)
                .Distinct()
                .ToArray();
            if (folderPaths.Length == 0)
                return (false, $"{path}：没有配置有效的扫描目录（文件夹资产）。");

            // 目录下逐脚本按「文件名 = 类名 + 所属程序集」精确定位类型，收集入选服务与跳过原因。
            var services = new List<ServiceInfo>();
            var notes = new List<string>();
            var seenTypes = new HashSet<Type>();
            var seenScripts = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:MonoScript", folderPaths))
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenScripts.Add(scriptPath)) continue; // 多个扫描目录重叠时去重

                var type = ResolveScriptType(scriptPath, notes);
                if (type == null || !seenTypes.Add(type)) continue;
                ClassifyType(type, services, notes);
            }

            services.Sort((a, b) => string.CompareOrdinal(a.Type.FullName, b.Type.FullName));

            // 生成期查重复契约：接口契约在同一安装器内只允许一个实现（构建期绑定静默后覆盖先，这里必须 fail-fast）。
            var byContract = new Dictionary<Type, Type>();
            var conflicts = new List<string>();
            foreach (var service in services)
                foreach (var contract in service.InterfaceContracts)
                {
                    if (byContract.TryGetValue(contract, out var existing))
                        conflicts.Add($"接口 {contract.FullName} 同时由 {existing.FullName} 与 {service.Type.FullName} 实现");
                    else
                        byContract[contract] = service.Type;
                }
            if (conflicts.Count > 0)
                return (false, $"{path}：重复契约，生成中止（对其一标 [ExcludeFromInstaller] 手写注册，或拆分扫描目录）：\n  " +
                               string.Join("\n  ", conflicts));

            string className = FrameworkCSharpSyntax.SanitizeIdentifier(DeriveClassName(path));
            string content = EmitInstaller(entry.Namespace.Trim(), className, services, folderPaths);

            bool upToDate = File.Exists(abs) && File.ReadAllText(abs) == content;
            if (!upToDate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllText(abs, content, Utf8NoBom);
                AssetDatabase.ImportAsset(path);
            }

            var summary = new StringBuilder(upToDate
                ? $"已是最新：{path}（{services.Count} 个服务，内容无变化未写盘）"
                : $"已生成 {path}（类 {entry.Namespace}.{className}，{services.Count} 个服务）");
            foreach (string note in notes)
                summary.Append("\n  ⚠ ").Append(note);
            return (true, summary.ToString());
        }

        private sealed class ServiceInfo
        {
            public Type Type;
            public List<Type> InterfaceContracts; // 派生自层标记的接口（不含标记本身），按全名排序
            public bool Disposable;
        }

        // 脚本路径 → 类型：文件名即类名（项目既定口径），所属程序集经编译管线精确定位，
        // 避免跨程序集 / 跨命名空间同短名误配。定位不到（无同名顶层类型的文件，如扩展方法集合）返回 null。
        private static Type ResolveScriptType(string scriptPath, List<string> notes)
        {
            string className = Path.GetFileNameWithoutExtension(scriptPath);
            string assemblyName = UnityEditor.Compilation.CompilationPipeline.GetAssemblyNameFromScriptPath(scriptPath);
            if (string.IsNullOrEmpty(assemblyName)) return null;
            if (assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                assemblyName = assemblyName[..^4];

            var assembly = FindAssembly(assemblyName);
            if (assembly == null) return null;

            Type match = null;
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsNested || type.Name != className) continue;
                if (match != null)
                {
                    notes.Add($"{scriptPath}：程序集 {assemblyName} 内有多个同名类型（不同命名空间），无法判定文件归属，跳过。");
                    return null;
                }
                match = type;
            }
            return match;
        }

        // 类型过滤 + 契约推导。静默跳过 = 明显不是服务（接口 / 枚举 / 静态类 / Mono / 无层标记）；
        // 记 note = 像服务但不满足生成前提，提醒用户手写注册或修正。
        private static void ClassifyType(Type type, List<ServiceInfo> services, List<string> notes)
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) return;
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return;

            Type layer = null;
            int layerCount = 0;
            foreach (var marker in LayerMarkers)
                if (marker.IsAssignableFrom(type))
                {
                    layer = marker;
                    layerCount++;
                }
            if (layerCount == 0) return;
            if (layerCount > 1)
            {
                notes.Add($"{type.FullName}：同时实现多个层标记（Model/System/Utility），设计错误，跳过。");
                return;
            }

            if (type.GetCustomAttribute<ExcludeFromInstallerAttribute>() != null)
            {
                notes.Add($"{type.FullName}：标了 [ExcludeFromInstaller]，跳过（注册自管）。");
                return;
            }
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                notes.Add($"{type.FullName}：没有公共无参构造，跳过（用 [ExcludeFromInstaller] 消除本提示并手写 RegisterFactory）。");
                return;
            }

            // 契约与 Mono 路径 RegisterFor 同口径：具体类型 + 派生自本层标记的接口（不含标记本身）。
            var interfaces = type.GetInterfaces()
                .Where(i => layer.IsAssignableFrom(i) && i != layer)
                .OrderBy(i => i.FullName, StringComparer.Ordinal)
                .ToList();

            services.Add(new ServiceInfo
            {
                Type = type,
                InterfaceContracts = interfaces,
                Disposable = typeof(IDisposable).IsAssignableFrom(type),
            });
        }

        private static string EmitInstaller(
            string ns, string className, List<ServiceInfo> services, string[] folderPaths)
        {
            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     由 ServiceInstallerGenerator 扫描以下目录生成，勿手改；重新生成会覆盖：");
            foreach (string folder in folderPaths)
                sb.AppendLine($"//         {folder}");
            sb.AppendLine("//     增删服务类后到 SSFramework/代码生成/服务安装器 工作台重新生成。");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// 服务安装器（生成代码）：把扫描目录下的纯 C# 服务注册进容器。");
            sb.AppendLine("    /// 由 Context 的 <c>InstallBindings</c> 调用；装进哪个 Context 由调用方决定。");
            sb.AppendLine("    /// 值绑定实例在 Context 构造时自动完成 [Inject] 注入与 GameContext 附着（ADR-0019）。");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public static class {className}");
            sb.AppendLine("    {");
            sb.AppendLine("        public static void Install(global::Game.Framework.Context.ContainerBuilder builder)");
            sb.AppendLine("        {");
            foreach (var service in services)
            {
                string method = service.Disposable ? "RegisterOwned" : "RegisterValue";
                var contracts = new List<string> { TypeOf(service.Type) };
                contracts.AddRange(service.InterfaceContracts.Select(TypeOf));
                sb.AppendLine($"            builder.{method}(new {FullName(service.Type)}(), {string.Join(", ", contracts)});");
            }
            if (services.Count == 0)
                sb.AppendLine("            // 扫描目录下暂无符合口径的服务类。");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string FullName(Type type) => "global::" + type.FullName;
        private static string TypeOf(Type type) => $"typeof({FullName(type)})";

        // 类名 = 文件名去掉 .g.cs / .cs 后缀（与包名常量 / UI 绑定生成「文件名即类名」口径一致）。
        private static string DeriveClassName(string path)
        {
            string file = Path.GetFileName(path);
            if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return file[..^5];
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return file[..^3];
            return file;
        }

        private static Assembly FindAssembly(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (!assembly.IsDynamic && assembly.GetName().Name == name)
                    return assembly;
            return null;
        }
    }
}
