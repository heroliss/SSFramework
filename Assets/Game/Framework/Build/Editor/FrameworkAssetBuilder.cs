using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YooAsset;        // EBundleType / EFileNameStyle / EBundledCopyOption 等运行时枚举
using YooAsset.Editor; // 构建管线与收集器：ScriptableBuildParameters / EBuildPipeline / ECompressOption / BundleBuilderHelper / BundleCollectorSettingData / DefaultBundlePackRule

namespace Game.Framework.Build
{
    /// <summary>
    /// 生产用资源构建实现——**全工程唯一的构建/部署逻辑**：统一构建菜单（<c>SSFramework/资源构建/*</c>，见
    /// <c>AssetBuildMenu</c>）和 CI（<see cref="BuildAll"/>）都复用这里，构建逻辑不再有第二份。
    ///
    /// 职责按「构建 / 部署」拆开（目录名见 <see cref="AssetBuildLayout"/>）：
    /// <list type="bullet">
    ///   <item><see cref="Build"/>：逐包跑 SBP，只产 YooAsset 原生输出（<c>AssetBuild/Bundles/&lt;平台&gt;/&lt;包&gt;/&lt;版本&gt;</c> + 内置首包写 StreamingAssets），构建后清理旧版本。</item>
    ///   <item><see cref="Deploy"/>：把某次构建产物平铺成「每包一个子目录」的待发布结构（统一目录 <c>AssetBuild/Deploy</c>：本地 python 伺服 + CI 上传共用）。</item>
    /// </list>
    /// 「打哪些包 + 每包参数」全部读 <see cref="FrameworkAssetBuildProfile"/>。<b>上传 CDN 交给 CI</b>，本类不绑定任何 CDN 厂商。
    ///
    /// <para><b>逐包容错：</b>一个包出问题只跳过/记录、继续构建其余包，最后汇总「构建/跳过/失败」。空包（无可收集资源）自动跳过；
    /// 勾了内置 shader 包但包里无内置 shader（YooAsset obsolete 任务崩）→ 失败时清晰提示去配置关掉该开关（<b>不自动重试</b>，避免无脑重建浪费）。</para>
    ///
    /// <para><b>只用 SBP（ScriptableBuildPipeline），不提供 Legacy。</b>「为什么窗口构建会崩、我们却能跑通」「内置 shader 包开关」见
    /// <see cref="ResolveBuiltinShaderBundleName"/> 上的长注释。</para>
    ///
    /// <para><b>CI 调用（headless）：</b></para>
    /// <code><![CDATA[
    /// Unity -batchmode -quit -nographics -projectPath . -buildTarget Android \
    ///       -executeMethod Game.Framework.Build.FrameworkAssetBuilder.BuildAll \
    ///       -version 1.2.3 [-output ./AssetBuild/Deploy] [-packages DefaultPackage,DLCPackage]
    /// ]]></code>
    /// <para>有真失败时以非 0 退出码结束（batchmode 下 CI 据此判定失败）。RawFile 包需另走 RawFileBuildPipeline，不在本入口范围。</para>
    /// </summary>
    public static class FrameworkAssetBuilder
    {
        // ── 构建参数（生产默认；按项目需要改这里。首包策略/内置 shader 包按包配置见 FrameworkAssetBuildProfile）──
        // 真实 AssetBundle 只用 SBP（现代推荐管线，增量/确定性好）。不提供 Legacy 路径。
        private const EBuildPipeline Pipeline = EBuildPipeline.ScriptableBuildPipeline;
        private const EFileNameStyle FileNameStyle = EFileNameStyle.HashName;
        private const ECompressOption Compress = ECompressOption.LZ4;

        // YooAsset 构建管线的临时输出子目录名（与包版本目录同级），部署/清理时要跳过它。
        // 对应 YooAssetSettings.OutputFolderName（internal，不能直接引用，故内联此常量）。
        private const string OutputCacheFolderName = "OutputCache";

        // ── CI 入口（-executeMethod 调用）──
        public static void BuildAll()
        {
            var profile = FrameworkAssetBuildProfile.Resolve();

            string version = GetArg("-version");
            if (string.IsNullOrEmpty(version))
            {
                version = profile.ResolveVersionNow();
                Debug.LogWarning("[AssetBuilder] 未传 -version，回退到时间戳；生产应由 CI 显式传入可追溯版本号。");
            }

            // -packages 显式传入则用它（逗号分隔），否则用 profile 里启用的包。
            string csv = GetArg("-packages");
            var packages = string.IsNullOrEmpty(csv)
                ? profile.EnabledPackageNames.ToList()
                : csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var (ok, message) = Build(profile, packages, version);

            // 构建无真失败后整理成待上传结构（CI 把该目录整目录同步上 CDN）。-output 缺省到统一 Deploy 目录。
            if (ok)
            {
                string cdnOutput = GetArg("-output");
                if (string.IsNullOrEmpty(cdnOutput)) cdnOutput = AssetBuildLayout.DeployRoot;
                var (deployOk, deployMsg) = Deploy(packages, cdnOutput);
                ok &= deployOk;
                message += "\n" + deployMsg;
            }

            if (ok) Debug.Log("[AssetBuilder] 构建成功：\n" + message);
            else Debug.LogError("[AssetBuilder] 构建失败：" + message);

            // batchmode 下用退出码告知 CI 成败；编辑器内手动调用不退出编辑器。
            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// 构建前置检查（交互式，供编辑器菜单复用）：必须 Edit 模式 + 所有打开场景已保存。
        /// 返回 false 表示不该继续（在 Play 模式 / 用户取消保存）。
        /// <para>为什么要存场景：SBP 第一步 TaskPrepare 会拒绝「有未保存场景」（ErrorCode101 Found unsaved scene），
        /// 不先存就会构建到一半才报错。</para>
        /// </summary>
        public static bool EnsureReadyToBuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("需退出 Play 模式", "AssetBundle 构建管线不能在 Play 模式运行。", "好");
                return false;
            }
            // 弹窗让用户保存已修改的场景；用户取消则中止构建。
            return EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
        }

        /// <summary>
        /// 逐包构建（只产 YooAsset 原生输出，不部署），**逐包容错、不因单包失败中断整批**。
        /// 返回 (是否无真失败, 多行汇总)；空包/无内置 shader 自动处理（见类型 remarks）。每包参数取自 <paramref name="profile"/>。
        /// ⚠ 仅 Edit 模式（SBP 不能在 Play 跑）。
        /// </summary>
        public static (bool ok, string message) Build(
            FrameworkAssetBuildProfile profile, IReadOnlyList<string> packages, string version)
        {
            try
            {
                if (packages == null || packages.Count == 0)
                    return (false, "没有可构建的包：profile 未启用任何包，或传入列表为空。");

                var target = EditorUserBuildSettings.activeBuildTarget;
                var built = new List<string>();   // 正常构建
                var skipped = new List<string>(); // 空包，跳过
                var failed = new List<string>();  // 真失败

                foreach (var pkg in packages)
                {
                    var entry = profile != null ? profile.GetEntry(pkg) : null;

                    // 预检：收集器里这个包没有任何 collector → 空包，直接跳过，不浪费一次构建尝试。
                    if (IsCollectorEmpty(pkg))
                    {
                        skipped.Add(pkg);
                        Debug.LogWarning($"[AssetBuilder] 包 '{pkg}' 在收集器里没有任何收集规则（空包），已跳过。");
                        continue;
                    }

                    var result = BuildPackage(pkg, entry, version, target);
                    if (result.Success)
                    {
                        built.Add(pkg);
                        CleanupOldVersions(pkg, profile);
                        continue;
                    }

                    // 有 collector 但收集到 0 资源 → YooAsset 在 TaskGetBuildMap 早早抛 "Pack asset list is empty"
                    //（在昂贵的打包阶段之前，很快、不浪费）→ 当空包跳过。
                    if (IsEmptyAssetError(result))
                    {
                        skipped.Add(pkg);
                        Debug.LogWarning($"[AssetBuilder] 包 '{pkg}' 收集到 0 个资源（空包），已跳过。");
                        continue;
                    }

                    // 真失败：记录、继续构建其余包（不中断整批）。**不自动重试**——失败原因多种多样，无脑重建只是浪费一次构建。
                    // 仅当开了内置 shader 包时附一句最常见坑的精准提示，让用户一次性去配置关掉（而非每次先失败再重试）。
                    string fail = $"{pkg}：[{result.FailedTask}] {result.ErrorInfo}";
                    if (entry == null || entry.GenerateBuiltinShaderBundle)
                        fail += "\n    ↳ 若控制台有 IBundleExplictObjectLayout / CreateBuiltInShadersBundle 字样：本包可能没有内置 shader，去构建配置关掉该包的「内置 shader 包」开关再构建。";
                    failed.Add(fail);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"平台 {target} · 版本 {version}");
                if (built.Count > 0) sb.AppendLine($"✓ 构建 {built.Count}：{string.Join(", ", built)}");
                if (skipped.Count > 0) sb.AppendLine($"⊘ 跳过（空包）{skipped.Count}：{string.Join(", ", skipped)}");
                if (failed.Count > 0) sb.AppendLine($"✗ 失败 {failed.Count}：\n  {string.Join("\n  ", failed)}");
                if (built.Count == 0 && failed.Count == 0)
                    sb.AppendLine("（没有实际产出：启用的包全是空包）");

                return (failed.Count == 0, sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return (false, e.Message + "（详见 Console）");
            }
        }

        /// <summary>
        /// 把每个包**最近一次构建**的产物平铺到「<paramref name="cdnRoot"/>/包名」子目录
        /// （与运行时 <c>GameRemoteService</c> 的 <c>{CDN}/{包名}/{文件}</c> 取址对齐）。
        /// 本地联调与生产用同一个 <c>AssetBuild/Deploy</c>。某包没有产物（空包/没构建）则跳过该包、不报错。
        /// </summary>
        public static (bool ok, string message) Deploy(IReadOnlyList<string> packages, string cdnRoot)
        {
            try
            {
                if (packages == null || packages.Count == 0)
                    return (false, "没有可部署的包。");

                var sb = new StringBuilder();
                int deployed = 0;
                foreach (var pkg in packages)
                {
                    string latest = FindLatestVersionDir(pkg);
                    if (latest == null)
                    {
                        sb.AppendLine($"⊘ {pkg}：无构建产物，跳过（空包或未构建）。");
                        continue;
                    }

                    int copied = FlattenToCdnDir(latest, cdnRoot, pkg);
                    sb.AppendLine($"✓ {pkg} → {Path.Combine(cdnRoot, pkg)}（{copied} 个文件，源 {Path.GetFileName(latest)}）");
                    deployed++;
                }
                if (deployed == 0) sb.AppendLine("（没有任何包被部署：都没有可用产物）");
                return (true, sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return (false, e.Message + "（详见 Console）");
            }
        }

        private static BuildResult BuildPackage(string packageName, PackageBuildEntry entry, string version, BuildTarget target)
        {
            // 缺配置时回退到「真实包」默认（开 shader 包 / 按 tag 拷首包 / 不内置）。
            bool genShaderBundle = entry?.GenerateBuiltinShaderBundle ?? true;
            var builtinCopy = entry?.BuiltinCopy ?? EBundledCopyOption.ClearAndCopyByTags;
            string builtinTags = entry?.BuiltinTags ?? "";
            if (entry == null)
                Debug.LogWarning($"[AssetBuilder] 包 '{packageName}' 不在构建 profile 中，使用默认参数。建议把它加进 profile（或用「同步收集器包列表」）。");

            var buildParameters = new ScriptableBuildParameters
            {
                BuildOutputRoot = AssetBuildLayout.BundlesRoot,             // 我们的目录，而非 YooAsset 默认 项目根/Bundles
                BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(), // 内置首包必须留在 StreamingAssets（随包走）
                BuildPipeline = Pipeline.ToString(),
                BuildBundleType = (int)EBundleType.AssetBundle,
                BuildTarget = target,
                PackageName = packageName,
                PackageVersion = version,
                EnableSharePackRule = true,
                VerifyBuildingResult = true,
                FileNameStyle = FileNameStyle,
                CompressOption = Compress,
                BundledCopyOption = builtinCopy,
                // tags 为空 = 零内置：传一个不会命中任何 bundle 的占位 tag 显式表达，不依赖 YooAsset 对空串按 ';' 切分的行为。
                BundledCopyParams = string.IsNullOrEmpty(builtinTags) ? "__builtin_none__" : builtinTags,
                BuiltinShadersBundleName = ResolveBuiltinShaderBundleName(packageName, genShaderBundle),
                // MonoScriptsBundleName 保持空：与 YooAsset 窗口默认一致，避免同类 obsolete 任务（CreateMonoScriptBundle）。
            };
            return new ScriptableBuildPipeline().Run(buildParameters, true);
        }

        /// <summary>
        /// 计算「Unity 内置 shader 包」名称。<paramref name="generate"/> 为 false 时返回空串 = 不打内置 shader 包。
        ///
        /// <para><b>背景（一定要懂，否则会被坑）：</b>YooAsset 的 Bundle Builder <b>窗口</b>总把内置 shader 包名设成非空，
        /// 于是 SBP 加入 <c>CreateBuiltInShadersBundle</c> 任务——而该任务在当前 SBP 版本已 <c>[Obsolete]</c>、转调
        /// <c>CreateBuiltInBundle</c>；当包里【没有任何】引用 Unity 内置资源（<c>unity_builtin_extra</c>，含内置 shader）的资产时，
        /// 它收集到 0 个内置对象、把 layout 置 null，而 obsolete 包装任务不判 null、硬取 <c>IBundleExplictObjectLayout</c> →
        /// 抛 "was not available"。这正是「窗口构建零 shader 的包会崩」的根因，<b>不是 SBP 在新 Unity 上整体坏了</b>。</para>
        ///
        /// <para><b>结论：</b>真实有材质/UI/模型的包【开】→ 收集得到内置对象、正常构建且内置 shader 正确去重；
        /// 零内置 shader 的包（纯 Sprite / 纯数据）【关】→ 跳过该任务、不崩。开关按包配在 <see cref="FrameworkAssetBuildProfile"/>；
        /// 忘了关时 <see cref="Build"/> 会在失败信息里提示去关（不自动重试）。彻底修复应由 YooAsset 改用非 obsolete 的
        /// <c>CreateBuiltInBundle</c>，我们不 fork 第三方库。包名沿用 YooAsset 默认 shader 包规则（与窗口一致）。
        /// 完整踩坑记录 + 何时可删本规避见 <c>docs/yooasset-pitfalls.md</c>（坑 1）。</para>
        /// </summary>
        private static string ResolveBuiltinShaderBundleName(string packageName, bool generate)
        {
            if (!generate) return "";
            return DefaultBundlePackRule.CreateShadersPackRuleResult()
                .GetBundleName(packageName, BundleCollectorSettingData.Setting.UniqueBundleName);
        }

        // 收集器里这个包是否没有任何收集规则（空包）。包不在收集器、或没有 group / collector 都算空。
        private static bool IsCollectorEmpty(string packageName)
        {
            foreach (var p in BundleCollectorSettingData.Setting.Packages)
            {
                if (p.PackageName != packageName) continue;
                if (p.Groups == null || p.Groups.Count == 0) return true;
                foreach (var g in p.Groups)
                    if (g.Collectors != null && g.Collectors.Count > 0) return false;
                return true; // 有 group 但没有 collector
            }
            return true; // 包不在收集器里
        }

        // 构建失败是否为「收集到 0 资源」——YooAsset TaskGetBuildMap 对空资源列表抛
        // ErrorCode.PackAssetListIsEmpty "Pack asset list is empty."（依赖该英文串，YooAsset 升级时留意）。
        private static bool IsEmptyAssetError(BuildResult r)
        {
            string info = $"{r.ErrorInfo} {r.FailedTask}";
            return info.IndexOf("asset list is empty", StringComparison.OrdinalIgnoreCase) >= 0
                || info.IndexOf("PackAssetListIsEmpty", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 构建成功后清理旧版本目录，只保留最近 N 个（按写入时间），跳过 OutputCache 临时目录。
        private static void CleanupOldVersions(string packageName, FrameworkAssetBuildProfile profile)
        {
            int keep = Math.Max(1, profile != null ? profile.BundleVersionsToKeep : 2);
            string root = PackageOutputRoot(packageName);
            if (!Directory.Exists(root)) return;

            var versionDirs = Directory.GetDirectories(root)
                .Where(d => !string.Equals(Path.GetFileName(d), OutputCacheFolderName, StringComparison.Ordinal))
                .OrderByDescending(Directory.GetLastWriteTime)
                .ToList();
            for (int i = keep; i < versionDirs.Count; i++)
            {
                try { Directory.Delete(versionDirs[i], true); }
                catch (Exception e) { Debug.LogWarning($"[AssetBuilder] 清理旧版本失败 {versionDirs[i]}：{e.Message}"); }
            }
        }

        // 某包的构建输出根：AssetBuild/Bundles/<平台>/<包名>。其下每个版本一个子目录 + 一个 OutputCache 临时目录。
        private static string PackageOutputRoot(string packageName)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            return $"{AssetBuildLayout.BundlesRoot}/{target}/{packageName}";
        }

        // 找某包最近一次构建的版本目录（按修改时间），跳过 YooAsset 的 OutputCache 临时目录。
        private static string FindLatestVersionDir(string packageName)
        {
            string root = PackageOutputRoot(packageName);
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root)
                .Where(d => !string.Equals(Path.GetFileName(d), OutputCacheFolderName, StringComparison.Ordinal))
                .OrderByDescending(Directory.GetLastWriteTime)
                .FirstOrDefault();
        }

        // 把一个版本目录平铺到「cdnRoot/包名」子目录。只重建本包子目录，不动其它包；CI 把整个 cdnRoot 同步上 CDN 即可。
        private static int FlattenToCdnDir(string versionDir, string cdnRoot, string packageName)
        {
            string pkgDir = Path.Combine(cdnRoot, packageName);
            if (Directory.Exists(pkgDir)) Directory.Delete(pkgDir, true);
            Directory.CreateDirectory(pkgDir);

            int count = 0;
            foreach (var file in Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(pkgDir, Path.GetFileName(file)), true);
                count++;
            }
            return count;
        }

        // 从 Unity 启动命令行读取 -name value 形式的参数（CI 通过 -executeMethod 后追加）。
        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return args[i + 1];
            return null;
        }
    }
}
