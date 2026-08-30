using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;
using YooAsset.Editor; // BundleCollectorSettingData

namespace Game.Framework.Build
{
    /// <summary>
    /// 「包名与资源构建常量」生成器：把 YooAsset 收集器里的包名生成为业务层的 <c>const string</c>，并从
    /// <see cref="FrameworkAssetBuildProfile"/> 派生普通 AssetBundle 的 <c>AssetBundleFileOffset</c>，
    /// 让加载 API 的 <c>packageName</c> 参数不再写裸字符串——收集器改名 / 删包后重新生成，
    /// 引用处直接编译报错，替代「运行时才发现 typo / 包已改名」。
    ///
    /// <para>包名真源与 <see cref="AssetPackageNameProviderInstaller"/> 一致：收集器
    /// （<c>BundleCollectorSettingData</c>），生成收集器里的<b>全部</b>包（含未参与资源构建的，如代码包）。
    /// 输出路径 / 命名空间在 <see cref="FrameworkAssetBuildProfile"/> 配置，类名 = 输出文件名去掉 <c>.g.cs</c>。
    /// FileOffset 常量供首场景代码引导在场景内 AssetUtility 出现前读取；它不作用于 RawFile / CodePackage。
    /// 内容不变时不写盘（无资产 diff、不触发重编译），可放心重复执行。</para>
    /// </summary>
    public static class AssetPackageConstantsGenerator
    {
        internal const string OutputClaimSourceId = "asset-package-constants";

        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private sealed class GeneratedSource
        {
            public string ClassName;
            public string Content;
            public int EmittedPackageCount;
            public List<string> SkippedPackages;
        }

        /// <summary>
        /// 按 profile 配置生成（或刷新）包名常量文件。返回 (是否成功, 人类可读摘要)——
        /// 交互外壳（菜单 / Inspector 按钮）拿摘要展示，本方法不弹窗。
        /// </summary>
        public static (bool ok, string message) Generate(FrameworkAssetBuildProfile profile)
        {
            string configuredPath = profile.PackageConstantsPath;
            if (string.IsNullOrEmpty(configuredPath))
                return (false, "构建 profile 未配置「包名与构建常量输出路径」（留空 = 不使用此功能）。");
            if (!FrameworkProjectPath.TryResolveAssetsFile(
                    configuredPath, ".cs", out string path, out string abs, out string pathError))
                return (false, "包名与构建常量输出路径无效：" + pathError);

            string ns = profile.PackageConstantsNamespace;
            if (string.IsNullOrEmpty(ns))
                return (false, "构建 profile 未配置「包名与构建常量命名空间」。");
            if (!FrameworkCSharpSyntax.TryValidateNamespace(ns, out string namespaceError))
                return (false, "包名与构建常量命名空间无效：" + namespaceError);
            FrameworkGeneratedOutputClaim outputClaim = CreateOutputClaim(profile, path, abs);
            if (!FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                    OutputClaimSourceId, new[] { outputClaim }, out string ownershipMessage))
                return (false, ownershipMessage);

            if (!TryRender(profile, path, ns, out GeneratedSource source, out string renderError))
                return (false, renderError);

            if (File.Exists(abs) && File.ReadAllText(abs) == source.Content)
                return (true, $"已是最新：{path}（{source.EmittedPackageCount} 个包，内容无变化未写盘）。");

            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, source.Content, Utf8NoBom);
            AssetDatabase.ImportAsset(path);

            var summary = new StringBuilder($"已生成 {path}（类 {ns}.{source.ClassName}，{source.EmittedPackageCount} 个包常量）。");
            if (source.SkippedPackages.Count > 0)
                summary.Append($"\n⚠ 跳过 {source.SkippedPackages.Count} 个：{string.Join("；", source.SkippedPackages)}");
            return (true, summary.ToString());
        }

        /// <summary>
        /// 只读校验入库生成物是否与当前收集器及构建 Profile 完全一致。
        /// 不按时间戳猜测，也不在构建流程里自动写代码：若内容陈旧，调用方应先显式生成并等待 Unity 编译完成，
        /// 避免域重载前的旧程序集继续执行本次构建。
        /// </summary>
        public static (bool ok, string message) ValidateFreshness(FrameworkAssetBuildProfile profile)
        {
            if (profile == null)
                return (false, "没有可校验的 FrameworkAssetBuildProfile。");

            string configuredPath = profile.PackageConstantsPath;
            if (string.IsNullOrEmpty(configuredPath))
                return (false, "构建 profile 未配置「包名与资源构建常量输出路径」。");
            if (!FrameworkProjectPath.TryResolveAssetsFile(
                    configuredPath, ".cs", out string path, out string abs, out string pathError))
                return (false, "资源构建常量输出路径无效：" + pathError);

            string ns = profile.PackageConstantsNamespace;
            if (string.IsNullOrEmpty(ns))
                return (false, "构建 profile 未配置「资源构建常量命名空间」。");
            if (!FrameworkCSharpSyntax.TryValidateNamespace(ns, out string namespaceError))
                return (false, "资源构建常量命名空间无效：" + namespaceError);
            if (!TryRender(profile, path, ns, out GeneratedSource expected, out string renderError))
                return (false, renderError);

            if (!File.Exists(abs))
                return (false,
                    $"资源构建常量尚未生成：{path}。请先在资源构建工作台点击「生成包名与构建常量」，等待 Unity 编译完成后再构建。");

            string actual = File.ReadAllText(abs);
            if (!string.Equals(actual, expected.Content, StringComparison.Ordinal))
                return (false,
                    $"资源构建常量已过期：{path} 与当前收集器 / FrameworkAssetBuildProfile 不一致。" +
                    "请先点击「生成包名与构建常量」，等待 Unity 编译完成后再构建；构建过程不会边写代码边继续使用旧程序集。");

            return (true,
                $"资源构建常量已是最新：{path}（{expected.EmittedPackageCount} 个包，AssetBundleFileOffset={profile.FileOffset}）。");
        }

        private static bool TryRender(
            FrameworkAssetBuildProfile profile,
            string path,
            string ns,
            out GeneratedSource source,
            out string error)
        {
            var packages = BundleCollectorSettingData.Setting.Packages
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.PackageName))
                .Select(p => (Name: p.PackageName.Trim(), Desc: p.PackageDesc?.Trim()))
                .ToList();
            return TryRender(profile, path, ns, packages, out source, out error);
        }

        private static bool TryRender(
            FrameworkAssetBuildProfile profile,
            string path,
            string ns,
            IReadOnlyList<(string Name, string Desc)> packages,
            out GeneratedSource source,
            out string error)
        {
            source = null;
            error = null;
            if (packages.Count == 0)
            {
                error = "收集器（AssetBundleCollector）里没有任何包，无可生成。";
                return false;
            }

            string className = FrameworkCSharpSyntax.SanitizeIdentifier(DeriveClassName(path));

            // AssetBundleFileOffset 与包名常量共用一个生成物，但不代表全部包都使用偏移：
            // 它只描述 FrameworkAssetBuilder 的普通 AssetBundle 格式，RawFile / CodePackage 保持独立 Module 契约。
            var usedIdentifiers = new HashSet<string> { className, "AssetBundleFileOffset" };
            var skipped = new List<string>();
            var body = new StringBuilder();
            int emitted = 0;
            foreach (var (name, desc) in packages)
            {
                string id = FrameworkCSharpSyntax.SanitizeIdentifier(name);
                if (!usedIdentifiers.Add(id))
                {
                    skipped.Add($"{name}（清洗后与已有生成成员重名：{id}）");
                    continue;
                }
                if (emitted > 0) body.AppendLine();
                if (!string.IsNullOrEmpty(desc))
                    body.AppendLine($"        /// <summary>{EscapeXml(desc)}</summary>");
                body.AppendLine($"        public const string {id} = \"{EscapeString(name)}\";");
                emitted++;
            }
            if (emitted == 0)
            {
                error = "所有包名清洗后都与已有生成成员冲突，未生成任何包常量（请检查收集器包名）。";
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     由 AssetPackageConstantsGenerator 从 YooAsset 收集器与资源构建 Profile 生成，勿手改；重新生成会覆盖。");
            sb.AppendLine("//     收集器包名或 AssetBundle FileOffset 变化后，到 SSFramework/构建与发布/资源构建 工作台重新生成。");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>资源包名与普通 AssetBundle 构建格式常量（生成代码）。</summary>");
            sb.AppendLine($"    public static class {className}");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// FrameworkAssetBuilder 构建普通 AssetBundle 时使用的文件头偏移；引导期加载首场景必须传入同一值。");
            sb.AppendLine("        /// 这不是密钥，也不适用于 RawFile / CodePackage；修改后必须重新生成并重编承载本文件的业务程序集 / Player。");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine($"        public const ulong AssetBundleFileOffset = {profile.FileOffset}UL;");
            sb.AppendLine();
            sb.Append(body);
            sb.AppendLine("    }");
            sb.AppendLine("}");

            source = new GeneratedSource
            {
                ClassName = className,
                // 生成物会入库，不能跟随 Editor 所在 OS 改换行。仓库固定 LF；否则 Windows 渲染 CRLF、
                // Git 检出 LF 后，逐字 freshness 校验会让同一份语义内容永久显示“已过期”。
                Content = NormalizeLineEndings(sb.ToString()),
                EmittedPackageCount = emitted,
                SkippedPackages = skipped,
            };
            return true;
        }

        /// <summary>测试用纯渲染接缝：不访问 AssetDatabase、不导入临时脚本，也不触发 Domain Reload。</summary>
        internal static (bool ok, string content, string error) RenderForTests(
            FrameworkAssetBuildProfile profile,
            string path,
            string ns,
            IReadOnlyList<(string Name, string Desc)> packages)
        {
            bool ok = TryRender(profile, path, ns, packages, out GeneratedSource source, out string error);
            return (ok, source?.Content, error);
        }

        internal static string NormalizeLineEndings(string content) =>
            content.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>供共享 Catalog 读取当前构建 Profile 已声明的包名与普通 AssetBundle 构建常量文件。</summary>
        internal static IReadOnlyList<FrameworkGeneratedOutputClaim> CollectRegisteredOutputClaims()
        {
            if (!FrameworkAssetBuildProfile.TryResolve(out FrameworkAssetBuildProfile profile) ||
                !FrameworkProjectPath.TryResolveAssetsFile(
                    profile.PackageConstantsPath, ".cs", out string assetPath, out string absolutePath, out _))
                return Array.Empty<FrameworkGeneratedOutputClaim>();
            return new[] { CreateOutputClaim(profile, assetPath, absolutePath) };
        }

        private static FrameworkGeneratedOutputClaim CreateOutputClaim(
            FrameworkAssetBuildProfile profile,
            string assetPath,
            string absolutePath)
        {
            string profilePath = AssetDatabase.GetAssetPath(profile);
            string claimId = string.IsNullOrEmpty(profilePath)
                ? $"transient:{profile.name}:{profile.GetInstanceID()}:package-constants"
                : profilePath + ":package-constants";
            return FrameworkGeneratedOutputClaim.ExactFile(
                claimId,
                $"资源包名与构建常量【{profile.name}】",
                assetPath,
                absolutePath);
        }

        // 类名 = 文件名去掉 .g.cs / .cs 后缀（与 UI 绑定生成「文件名即类名」口径一致）。
        private static string DeriveClassName(string path)
        {
            string file = Path.GetFileName(path);
            if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return file[..^5];
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return file[..^3];
            return file;
        }

        private static string EscapeString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string EscapeXml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
