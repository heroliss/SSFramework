using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YooAsset;        // EBundleType / EFileNameStyle / EBundledCopyOption 等运行时枚举
using YooAsset.Editor; // 构建管线与收集器：ScriptableBuildParameters / EBuildPipeline / ECompressOption / BundleBuilderHelper / BundleCollectorSettingData / DefaultBundlePackRule

namespace Game.Framework.Build
{
    /// <summary>
    /// 生产用资源构建实现——**全工程唯一的构建/部署逻辑**：资源构建工作台（见
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
    /// <para>有真失败时以非 0 退出码结束（batchmode 下 CI 据此判定失败）。RawFile 包（收集器用 <c>PackRawFile</c>）需另走
    /// RawFileBuildPipeline、不在本入口范围——构建前逐包预检，命中直接计失败并说明应关闭本 Profile 的参与构建开关，
    /// 再交给拥有该 RawFile 配方的独立 Module。</para>
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
            if (!FrameworkAssetBuildProfile.TryResolve(out var profile))
            {
                Debug.LogError("[AssetBuilder] 构建未启动：工程里没有 FrameworkAssetBuildProfile。" +
                               "CI 不会代替项目创建发布配置；请先在资源构建工作台明确创建、复核并提交 Profile。");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

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

            // 本机构建过程开关（不进产物）：CI 用「存在即开」的开关式参数（-clearBuildCache / -useAssetDependencyDB），不需要带值。
            bool clearBuildCache = HasFlag("-clearBuildCache");
            bool useAssetDependencyDB = HasFlag("-useAssetDependencyDB");

            var (ok, message) = Build(profile, packages, version, clearBuildCache, useAssetDependencyDB);

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
            if (!FrameworkEditorOperationGate.EnsureCanStart("资源构建预检")) return false;
            // 弹窗让用户保存已修改的场景；用户取消则中止构建。
            bool mayContinue = EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            if (!mayContinue)
                FrameworkEditorFeedback.Info("资源构建已取消", "没有启动构建；当前场景改动保持不变。");
            return mayContinue;
        }

        /// <summary>
        /// 逐包构建（只产 YooAsset 原生输出，不部署），**逐包容错、不因单包失败中断整批**。
        /// 返回 (是否无真失败, 多行汇总)；空包/无内置 shader 自动处理（见类型 remarks）。每包参数取自 <paramref name="profile"/>。
        /// ⚠ 仅 Edit 模式（SBP 不能在 Play 跑）。
        /// <para><paramref name="clearBuildCache"/> / <paramref name="useAssetDependencyDB"/> 是「本机构建过程」开关、不进产物、不入 profile：
        /// 前者清 SBP 增量缓存强制全量重建（排障用，平时关＝走增量更快）；后者用资源依赖缓存数据库加速收集阶段。
        /// 它们由菜单 / CI 入口按需传入（见 <see cref="AssetBuildMenu"/> 与 <see cref="BuildAll"/>）。</para>
        /// </summary>
        public static (bool ok, string message) Build(
            FrameworkAssetBuildProfile profile, IReadOnlyList<string> packages, string version,
            bool clearBuildCache = false, bool useAssetDependencyDB = false)
        {
            try
            {
                if (packages == null || packages.Count == 0)
                    return (false, "没有可构建的包：profile 未启用任何包，或传入列表为空。");
                if (!TryNormalizePackageNames(packages, out var normalizedPackages, out string packageError))
                    return (false, "构建包名预检失败：" + packageError);
                if (!FrameworkBuildArtifactPath.TryNormalizeSegment(
                        version, "资源版本号", out string normalizedVersion, out string versionError))
                    return (false, "构建版本预检失败：" + versionError);
                packages = normalizedPackages;
                version = normalizedVersion;

                // 自定义加密与偏移加密互斥：二者都配时以自定义为准、偏移被忽略，提醒去把 FileOffset 置 0 以免误解。
                if (GameAssetEncryption.CustomBundleEncryptor != null && profile != null && profile.FileOffset > 0)
                    Debug.LogWarning("[AssetBuilder] 同时配置了自定义加密器(GameAssetEncryption.CustomBundleEncryptor)与偏移加密(profile.FileOffset>0)，" +
                                     "本次以自定义加密为准、偏移被忽略。建议把 profile 的 FileOffset 置 0。");

                var target = EditorUserBuildSettings.activeBuildTarget;
                bool requiresOrdinaryAssetBundle = RequiresGeneratedAssetBundleConstants(packages);
                if (requiresOrdinaryAssetBundle)
                {
                    string offsetError = ValidateBuiltInFileOffset(
                        profile, GameAssetEncryption.CustomBundleEncryptor != null);
                    if (offsetError != null) return (false, offsetError);
                }

                // 生成物同时冻结包名与普通 AssetBundle 的引导期 FileOffset。只要本次包含普通 AB，构建前必须验证它；
                // RawFile / CodePackage 属于独立 Module，不读取这个 offset，也不能被本门禁误拦。
                if (profile != null && !string.IsNullOrEmpty(profile.PackageConstantsPath) &&
                    requiresOrdinaryAssetBundle)
                {
                    var freshness = AssetPackageConstantsGenerator.ValidateFreshness(profile);
                    if (!freshness.ok)
                        return (false, "资源构建常量预检失败：" + freshness.message);
                }

                var built = new List<string>();    // 正常构建
                var skipped = new List<string>();  // 空包，跳过
                var failed = new List<string>();   // 真失败

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

                    // 预检：RawFile 包不属于本 Module——SBP + AssetBundle 类型构建出的产物与 RawFile 运行时通道不兼容，
                    // 放任构建要么半路崩、要么产出错误产物，失败信息也不指向真正原因，这里直接报明话指路。
                    if (UsesRawFilePackRule(pkg))
                    {
                        failed.Add($"{pkg}：收集器使用 RawFile 打包规则（PackRawFile），本入口只构建普通 AssetBundle 包——" +
                                   "请在资源构建 Profile 关闭该包的“参与构建”，再使用拥有对应 RawFile 配方的独立构建模块。");
                        continue;
                    }

                    var result = BuildPackage(pkg, entry, profile, version, target, clearBuildCache, useAssetDependencyDB);
                    if (result.Success)
                    {
                        var (bundledOk, bundledError) = ValidateBundledOutput(pkg, entry, version);
                        if (!bundledOk)
                        {
                            failed.Add($"{pkg}：构建管线报告成功，但首包产物校验失败：{bundledError}");
                            continue;
                        }

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
                return (false, "资源包构建过程抛出未处理异常；本次操作未完整成功。\n" + e);
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
                if (!TryNormalizePackageNames(packages, out var normalizedPackages, out string packageError))
                    return (false, "部署包名预检失败：" + packageError);
                if (string.IsNullOrWhiteSpace(cdnRoot))
                    return (false, "部署根目录不能为空。");
                cdnRoot = Path.GetFullPath(cdnRoot);
                foreach (string packageName in normalizedPackages)
                    if (!FrameworkBuildArtifactPath.TryResolveChildDirectory(
                            cdnRoot, packageName, "资源包名", out _, out string childError))
                        return (false, "部署目录预检失败：" + childError);
                packages = normalizedPackages;

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
                return (false, "部署过程抛出未处理异常；目标目录可能只有部分文件，请修复后重新部署。\n" + e);
            }
        }

        private static BuildResult BuildPackage(string packageName, PackageBuildEntry entry, FrameworkAssetBuildProfile profile, string version, BuildTarget target,
            bool clearBuildCache, bool useAssetDependencyDB)
        {
            // 缺配置时回退到「真实包」默认（开 shader 包 / 按 tag 拷首包 / 不内置）。
            bool genShaderBundle = entry?.GenerateBuiltinShaderBundle ?? true;
            var builtinCopy = entry?.BuiltinCopy ?? EBundledCopyOption.ClearAndCopyByTags;
            string builtinTags = entry?.BuiltinTags ?? "";
            if (entry == null)
                Debug.LogWarning($"[AssetBuilder] 包 '{packageName}' 不在构建 profile 中，使用默认参数。建议把它加进 profile（或用「同步收集器包列表」）。");

            // 全局构建设置（压缩 / 文件名风格 / 偏移加密）取自 profile，无 profile 时回退常量默认；其余（构建管线 / bundle 类型 / 输出路径）是框架不变量，写死不开放。
            var compress = profile != null ? profile.Compression : Compress;
            var fileNameStyle = profile != null ? profile.FileNameStyle : FileNameStyle;
            // 加密器选择（优先级：项目自定义 > 偏移 > 不加密）：
            //   ① 项目设了自定义加密器（GameAssetEncryption.CustomBundleEncryptor，XOR/AES 等）→ 用它；
            //   ② 否则 profile 偏移加密（FileOffset>0）→ 每个 bundle 头插入 N 字节，运行时按相同 N 跳过（GameBundleOffsetDecryptor）；
            //   ③ 都没有 → null（不加密，YooAsset 跳过加密任务）。
            // 运行时解密由 Game.Framework.GameAssetDecryption 配对；内容加密详见 docs/asset-encryption.md。
            ulong fileOffset = profile != null ? profile.FileOffset : 0;
            IBundleEncryptor bundleEncryptor = GameAssetEncryption.CustomBundleEncryptor
                ?? (fileOffset > 0 ? new GameBundleOffsetEncryptor(fileOffset) : null);

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
                FileNameStyle = fileNameStyle,
                CompressOption = compress,
                BundleEncryptor = bundleEncryptor,
                // 清单加密（可选）：仅当项目设了自定义清单加/解密器时生效；偏移加密不碰清单（保持明文）。null = 不加密清单。
                ManifestEncryptor = GameAssetEncryption.CustomManifestEncryptor,
                ManifestDecryptor = GameAssetEncryption.CustomManifestDecryptor,
                // 本机构建过程开关（不进产物）：清缓存=强制全量重建；依赖 DB=加速资源收集。
                ClearBuildCacheFiles = clearBuildCache,
                UseAssetDependencyDB = useAssetDependencyDB,
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

        // 收集器里这个包是否用了 RawFile 打包规则。RawFile 包要求每文件独立 bundle、走 RawFileBuildPipeline
        // 构建（bundle 类型是包级二选一），与本类的 SBP + AssetBundle 参数互斥——任一 collector 用了即视为 RawFile 包。
        private static bool UsesRawFilePackRule(string packageName)
        {
            foreach (var p in BundleCollectorSettingData.Setting.Packages)
            {
                if (p.PackageName != packageName) continue;
                if (p.Groups == null) return false;
                foreach (var g in p.Groups)
                {
                    if (g.Collectors == null) continue;
                    foreach (var c in g.Collectors)
                        if (c.PackRuleName == nameof(PackRawFile)) return true;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// 本次请求是否包含由本 Module 构建的普通 AssetBundle。
        /// 空包不会产出，RawFile 包归独立构建 Module；只有剩余包才消费生成的 AssetBundleFileOffset。
        /// </summary>
        internal static bool RequiresGeneratedAssetBundleConstants(IReadOnlyList<string> packages)
            => RequiresGeneratedAssetBundleConstants(packages, IsCollectorEmpty, UsesRawFilePackRule);

        internal static bool RequiresGeneratedAssetBundleConstants(
            IReadOnlyList<string> packages,
            Func<string, bool> isCollectorEmpty,
            Func<string, bool> usesRawFilePackRule)
        {
            if (packages == null) return false;
            foreach (string packageName in packages)
            {
                if (string.IsNullOrWhiteSpace(packageName)) continue;
                string normalized = packageName.Trim();
                if (!isCollectorEmpty(normalized) && !usesRawFilePackRule(normalized))
                    return true;
            }
            return false;
        }

        /// <summary>校验内置偏移加密的现实资源上限；项目自定义加密接管时本字段不生效。</summary>
        internal static string ValidateBuiltInFileOffset(
            FrameworkAssetBuildProfile profile,
            bool customEncryptorConfigured)
        {
            if (customEncryptorConfigured || profile == null || profile.FileOffset == 0) return null;
            if (profile.FileOffset > AssetProviderConfig.MaxBuiltInFileOffset)
                return $"文件头偏移 {profile.FileOffset} 超出内置偏移加/解密器支持范围 " +
                       $"0..{AssetProviderConfig.MaxBuiltInFileOffset}；更大的弱混淆头不会增加实际安全性，" +
                       "只会放大每个 bundle 的磁盘与构建内存成本。";
            return null;
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

        /// <summary>
        /// YooAsset 的 <see cref="BuildResult.Success"/> 只表示构建任务没有抛错，不代表首包目录真的满足 profile。
        /// 发包前在框架侧再核对一次，避免出现“清单能加载、bundle 却仍访问 CDN”的半成品 Player。
        /// </summary>
        private static (bool ok, string error) ValidateBundledOutput(
            string packageName, PackageBuildEntry entry, string version)
        {
            var copyOption = entry?.BuiltinCopy ?? EBundledCopyOption.ClearAndCopyByTags;
            if (copyOption == EBundledCopyOption.None)
                return (true, null);

            if (!FrameworkBuildArtifactPath.TryResolveChildDirectory(
                    PackageOutputRoot(packageName), version, "资源版本号", out string outputDir, out string versionError))
                return (false, versionError);
            if (!FrameworkBuildArtifactPath.TryResolveChildDirectory(
                    BundleBuilderHelper.GetStreamingAssetsRoot(), packageName, "资源包名", out string bundledDir, out string packageError))
                return (false, packageError);
            if (!Directory.Exists(outputDir))
                return (false, $"找不到版本输出目录：{outputDir}");
            if (!Directory.Exists(bundledDir))
                return (false, $"找不到 StreamingAssets 包目录：{bundledDir}");

            string[] requiredPipelineFiles =
            {
                YooAssetConfiguration.GetManifestBinaryFileName(packageName, version),
                YooAssetConfiguration.GetPackageHashFileName(packageName, version),
                YooAssetConfiguration.GetPackageVersionFileName(packageName),
                "BuiltinCatalog.bytes",
            };
            var missingPipelineFiles = requiredPipelineFiles
                .Where(file => !File.Exists(Path.Combine(bundledDir, file)))
                .ToArray();
            if (missingPipelineFiles.Length > 0)
                return (false, $"缺少内置清单文件：{string.Join(", ", missingPipelineFiles)}");

            var outputBundles = Directory.GetFiles(outputDir, "*.bundle", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            var bundledBundles = new HashSet<string>(
                Directory.GetFiles(bundledDir, "*.bundle", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName),
                StringComparer.Ordinal);

            if (copyOption == EBundledCopyOption.ClearAndCopyAll ||
                copyOption == EBundledCopyOption.OnlyCopyAll)
            {
                var missingBundles = outputBundles.Where(file => !bundledBundles.Contains(file)).ToArray();
                if (outputBundles.Length == 0)
                    return (false, $"版本输出目录没有 bundle：{outputDir}");
                if (missingBundles.Length > 0)
                    return (false, $"配置要求全部内置，但缺少 {missingBundles.Length}/{outputBundles.Length} 个 bundle：" +
                                   string.Join(", ", missingBundles.Take(5)) +
                                   (missingBundles.Length > 5 ? "…" : ""));
            }

            bool copyByTags = copyOption == EBundledCopyOption.ClearAndCopyByTags ||
                              copyOption == EBundledCopyOption.OnlyCopyByTags;
            if (copyByTags && !string.IsNullOrWhiteSpace(entry?.BuiltinTags))
            {
                string reportPath = Path.Combine(
                    outputDir, YooAssetConfiguration.GetBuildReportFileName(packageName, version));
                if (!File.Exists(reportPath))
                    return (false, $"按标签校验需要构建报告，但文件不存在：{reportPath}");

                BuildReport report;
                try
                {
                    report = BuildReport.Deserialize(File.ReadAllText(reportPath, Encoding.UTF8));
                }
                catch (Exception e)
                {
                    return (false, $"构建报告无法解析：{reportPath}（{e.Message}）");
                }

                var requestedTags = new HashSet<string>(
                    entry.BuiltinTags.Split(';').Select(tag => tag.Trim()).Where(tag => tag.Length > 0),
                    StringComparer.Ordinal);
                var expectedBundles = (report?.BundleInfos ?? new List<ReportBundleInfo>())
                    .Where(bundle => bundle.Tags != null && bundle.Tags.Any(requestedTags.Contains))
                    .Select(bundle => bundle.FileName)
                    .Where(file => !string.IsNullOrEmpty(file))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (expectedBundles.Length == 0)
                    return (false, $"配置了首包标签 '{entry.BuiltinTags}'，但本次构建报告中没有任何 bundle 命中；请检查收集器标签是否匹配。");

                var missingTaggedBundles = expectedBundles.Where(file => !bundledBundles.Contains(file)).ToArray();
                if (missingTaggedBundles.Length > 0)
                    return (false, $"配置要求按标签内置，但缺少 {missingTaggedBundles.Length}/{expectedBundles.Length} 个本次应复制的 bundle：" +
                                   string.Join(", ", missingTaggedBundles.Take(5)) +
                                   (missingTaggedBundles.Length > 5 ? "…" : ""));
            }

            return (true, null);
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
            string platformRoot = Path.Combine(AssetBuildLayout.BundlesRoot, target.ToString());
            if (!FrameworkBuildArtifactPath.TryResolveChildDirectory(
                    platformRoot, packageName, "资源包名", out string outputRoot, out string error))
                throw new InvalidOperationException(error);
            return outputRoot;
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
            if (!FrameworkBuildArtifactPath.TryResolveChildDirectory(
                    cdnRoot, packageName, "资源包名", out string pkgDir, out string error))
                throw new InvalidOperationException(error);
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

        private static bool TryNormalizePackageNames(
            IReadOnlyList<string> packages,
            out List<string> normalizedPackages,
            out string error)
        {
            normalizedPackages = new List<string>(packages?.Count ?? 0);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packages == null)
            {
                error = "包列表不能为空。";
                return false;
            }

            for (int i = 0; i < packages.Count; i++)
            {
                if (!FrameworkBuildArtifactPath.TryNormalizeSegment(
                        packages[i], $"第 {i + 1} 个资源包名", out string normalized, out error))
                    return false;
                if (!seen.Add(normalized))
                {
                    error = $"包名重复或仅大小写不同：{normalized}。不同平台会把它们映射到同一部署目录。";
                    return false;
                }
                normalizedPackages.Add(normalized);
            }

            error = string.Empty;
            return true;
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

        // 开关式命令行参数（存在即为真，不带值）——用于布尔构建开关（-clearBuildCache / -useAssetDependencyDB）。
        private static bool HasFlag(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
