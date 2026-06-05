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
    /// 职责按「构建 / 部署」拆开：
    /// <list type="bullet">
    ///   <item><see cref="Build"/>：逐包跑 SBP，只产 YooAsset 原生输出（<c>Bundles/&lt;平台&gt;/&lt;包&gt;/&lt;版本&gt;</c> + 内置首包写 StreamingAssets）。</item>
    ///   <item><see cref="Deploy"/>：把某次构建产物平铺成「每包一个子目录」的 CDN 待上传结构（本地联调 → 项目根/CDN；生产 → BuildOutput/CDN 给 CI 上传）。</item>
    /// </list>
    /// 「打哪些包 + 每包参数」全部读 <see cref="FrameworkAssetBuildProfile"/>（单一配置源）。<b>上传 CDN 交给 CI</b>，本类不绑定任何 CDN 厂商。
    ///
    /// <para><b>只用 SBP（ScriptableBuildPipeline），不提供 Legacy。</b> 关于「为什么窗口构建会崩、我们却能跑通」「内置 shader 包开关」见
    /// <see cref="ResolveBuiltinShaderBundleName"/> 上的长注释。</para>
    ///
    /// <para><b>CI 调用（headless）：</b></para>
    /// <code><![CDATA[
    /// Unity -batchmode -quit -nographics -projectPath . -buildTarget Android \
    ///       -executeMethod Game.Framework.Build.FrameworkAssetBuilder.BuildAll \
    ///       -version 1.2.3 -output ./BuildOutput/CDN [-packages DefaultPackage,DLCPackage]
    /// ]]></code>
    /// <para>构建失败时以非 0 退出码结束（batchmode 下 CI 据此判定失败）。RawFile 包需另走 RawFileBuildPipeline，不在本入口范围。</para>
    /// </summary>
    public static class FrameworkAssetBuilder
    {
        // ── 构建参数（生产默认；按项目需要改这里。首包策略/内置 shader 包按包配置见 FrameworkAssetBuildProfile）──
        // 真实 AssetBundle 只用 SBP（现代推荐管线，增量/确定性好）。不提供 Legacy 路径。
        private const EBuildPipeline Pipeline = EBuildPipeline.ScriptableBuildPipeline;
        private const EFileNameStyle FileNameStyle = EFileNameStyle.HashName;
        private const ECompressOption Compress = ECompressOption.LZ4;

        // YooAsset 构建管线的临时输出子目录名（与包版本目录同级），部署时要跳过它。
        // 对应 YooAssetSettings.OutputFolderName（internal，不能直接引用，故内联此常量）。
        private const string OutputCacheFolderName = "OutputCache";

        // ── CI 入口（-executeMethod 调用）──
        public static void BuildAll()
        {
            var profile = FrameworkAssetBuildProfile.Resolve();

            string version = GetArg("-version");
            if (string.IsNullOrEmpty(version))
            {
                version = DateTime.Now.ToString(profile.VersionFormat);
                Debug.LogWarning("[AssetBuilder] 未传 -version，回退到时间戳；生产应由 CI 显式传入可追溯版本号。");
            }

            // -packages 显式传入则用它（逗号分隔），否则用 profile 里启用的包。
            string csv = GetArg("-packages");
            var packages = string.IsNullOrEmpty(csv)
                ? profile.EnabledPackageNames.ToList()
                : csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var (ok, message) = Build(profile, packages, version);

            // 构建成功且指定了 -output：整理成 CDN 待上传结构（CI 把该目录整目录同步上 CDN）。
            string cdnOutput = GetArg("-output");
            if (ok && !string.IsNullOrEmpty(cdnOutput))
            {
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
        /// 逐包构建（只产 YooAsset 原生输出，不部署）。**全工程唯一的构建实现**，供菜单 / CI 复用。
        /// 不抛异常、不退出编辑器——返回 (是否成功, 多行结果或失败原因) 由调用方决定如何上报。
        /// 每包参数取自 <paramref name="profile"/>（首包策略 / 内置 shader 包开关）。⚠ 仅 Edit 模式（SBP 不能在 Play 跑）。
        /// </summary>
        public static (bool ok, string message) Build(
            FrameworkAssetBuildProfile profile, IReadOnlyList<string> packages, string version)
        {
            try
            {
                if (packages == null || packages.Count == 0)
                    return (false, "没有可构建的包：profile 未启用任何包，或传入列表为空。");

                var target = EditorUserBuildSettings.activeBuildTarget;
                var sb = new StringBuilder();
                sb.AppendLine($"平台 {target} · 版本 {version} · 包 [{string.Join(", ", packages)}]");

                foreach (var pkg in packages)
                {
                    var entry = profile != null ? profile.GetEntry(pkg) : null;
                    var result = BuildPackage(pkg, entry, version, target);
                    if (!result.Success)
                        return (false, $"包 '{pkg}' 构建失败：[{result.FailedTask}] {result.ErrorInfo}");

                    sb.AppendLine($"✓ {pkg} v{version} → {result.OutputPackageDirectory}");
                }
                return (true, sb.ToString().TrimEnd());
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
        /// 本地联调传项目根/CDN；生产传 BuildOutput/CDN（交给 CI 上传）。找不到产物即失败（提示先构建）。
        /// </summary>
        public static (bool ok, string message) Deploy(IReadOnlyList<string> packages, string cdnRoot)
        {
            try
            {
                if (packages == null || packages.Count == 0)
                    return (false, "没有可部署的包。");

                var sb = new StringBuilder();
                foreach (var pkg in packages)
                {
                    string latest = FindLatestVersionDir(pkg);
                    if (latest == null)
                        return (false, $"包 '{pkg}' 没有可部署的构建产物（先执行「构建资源包」）。");

                    int copied = FlattenToCdnDir(latest, cdnRoot, pkg);
                    sb.AppendLine($"✓ {pkg} → {Path.Combine(cdnRoot, pkg)}（{copied} 个文件，源 {Path.GetFileName(latest)}）");
                }
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
                Debug.LogWarning($"[AssetBuilder] 包 '{packageName}' 不在构建 profile 中，使用默认参数（含内置 shader 包；零 shader 的包请加进 profile 并关掉开关）。");

            var buildParameters = new ScriptableBuildParameters
            {
                BuildOutputRoot = BundleBuilderHelper.GetDefaultBuildOutputRoot(),
                BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(),
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
        /// 零内置 shader 的包（纯 Sprite / 纯数据，如 demo 样例包）【关】→ 跳过该任务、不崩，且本就没东西可去重
        /// （此时 YooAsset 那条 "resource redundancy" warning 是误报）。开关按包配在
        /// <see cref="FrameworkAssetBuildProfile"/>。彻底的修复应由 YooAsset 改用非 obsolete 的 <c>CreateBuiltInBundle</c>，
        /// 我们不 fork 第三方库。包名沿用 YooAsset 默认 shader 包规则（与窗口一致）。</para>
        /// </summary>
        private static string ResolveBuiltinShaderBundleName(string packageName, bool generate)
        {
            if (!generate) return "";
            return DefaultBundlePackRule.CreateShadersPackRuleResult()
                .GetBundleName(packageName, BundleCollectorSettingData.Setting.UniqueBundleName);
        }

        // 某包的构建输出根：Bundles/<平台>/<包名>。其下每个版本一个子目录 + 一个 OutputCache 临时目录。
        private static string PackageOutputRoot(string packageName)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            return $"{BundleBuilderHelper.GetDefaultBuildOutputRoot()}/{target}/{packageName}";
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
