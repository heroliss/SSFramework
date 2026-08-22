using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Game.Framework.Boot;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.Installer;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEngine;
using YooAsset;        // EBundleType / EFileNameStyle / EBundledCopyOption
using YooAsset.Editor; // RawFileBuildParameters / RawFileBuildPipeline / 收集器配置

namespace Game.Framework.Build
{
    /// <summary>
    /// 热更代码构建实现——把「C# 源码改动」变成「CDN 上可下发的代码包」的全部编辑器侧逻辑，
    /// 菜单（<c>SSFramework/热更构建/*</c>）与将来的 CI 都只调这里。两个入口对应两种频率的工作：
    /// <list type="bullet">
    ///   <item><see cref="Generate"/>：包装 HybridCLR 的 <c>Generate/All</c>（Il2CppDef / link.xml / 裁剪 AOT DLL /
    ///         桥接函数 / AOT 泛型引用）。**慢**（内部跑一次迷你构建产裁剪 DLL）。首次接入、Unity/HybridCLR 升级、
    ///         AOT 程序集集合变化（增删第三方库、改热更列表档位）后必须重跑；日常只改热更代码不用。</item>
    ///   <item><see cref="BuildCodePackage"/>：日常热更迭代的一条龙——同步校验 → CompileDll →
    ///         拷热更 DLL + AOT 补元数据 DLL 进中转目录（.bytes）→ 生成 <see cref="HotUpdateManifest"/> →
    ///         确保收集器 → <see cref="RawFileBuildPipeline"/> 打包。产物落 <c>AssetBuild/Bundles</c>，
    ///         部署复用 <see cref="FrameworkAssetBuilder.Deploy"/>（与资源包同一套 Deploy/CDN 流程）。</item>
    /// </list>
    ///
    /// <para><b>中转目录 <see cref="DllAssetDir"/></b>：YooAsset 收集器只认 Assets 内资产，DLL 须先拷进来改名
    /// <c>.bytes</c>（地址规则 AddressByFileName 去扩展名 → location 形如 <c>Game.Framework.dll</c>）。
    /// 整个目录是构建产物（已 gitignore），每次构建全量重建，不要手放东西。</para>
    ///
    /// <para><b>AOT 补元数据清单</b>来自 Generate 产出的 <c>AOTGenericReferences.cs</c>（解析其中
    /// <c>PatchedAOTAssemblyList</c>），对应裁剪 DLL 取自 <c>AssembliesPostIl2CppStrip</c>——
    /// 没跑过 Generate，或生成时的 Unity / HybridCLR / 平台 / Development / 热更列表任一项变化，构建会在产出代码包前停止；
    /// 编辑器联调会旁路 DLL 加载，不能作为生成物新鲜度的依据。</para>
    /// </summary>
    public static class FrameworkHotUpdateBuilder
    {
        /// <summary>DLL/清单中转目录（构建产物，gitignore，收集器自动指向这里）。</summary>
        public const string DllAssetDir = "Assets/HotUpdateDlls";

        // 自动创建的收集器组名——只接管这个组：项目在代码包里手工加的其他组/收集器一概不动。
        private const string CodeGroupName = "HotUpdateCode";

        // YooAsset 构建管线的临时输出子目录名（同 FrameworkAssetBuilder 内联常量，清理版本时跳过）。
        private const string OutputCacheFolderName = "OutputCache";

        // Generate 的结果同时依赖这些编辑器状态。stamp 放在 HybridCLRData（本地生成物目录）里，
        // BuildCodePackage 据此拒绝消费旧版本/旧平台的桥接和裁剪产物。
        private static string GenerationStampPath =>
            Path.Combine(SettingsUtil.HybridCLRDataDir, "SSFramework", "generation-stamp.json");

        /// <summary>包装 HybridCLR <c>Generate/All</c>（先同步校验热更列表，违规即停）。耗时分钟级，见类型注释的触发时机。</summary>
        public static (bool ok, string message) Generate(FrameworkHotUpdateProfile profile)
        {
            try
            {
                string sync = profile.SyncToHybridCLRSettings();
                if (sync.Contains("✗")) return (false, sync);

                var (installerOk, installerMessage) = ValidateHybridClrInstaller();
                if (!installerOk) return (false, sync + "\n" + installerMessage);

                // Generate 失败时不能继续沿用旧 stamp，否则下一次日常构建会把旧产物误判为新鲜。
                InvalidateGenerationStamp();

                PrebuildCommand.GenerateAll();
                AssetDatabase.Refresh(); // link.xml / AOTGenericReferences.cs 落在 Assets 下，刷新入库
                WriteGenerationStamp(profile);

                return (true, sync + "\n" + installerMessage +
                    "\n✓ Generate/All 完成并记录生成环境（Il2CppDef / link.xml / 裁剪 AOT DLL / 桥接函数 / AOT 泛型引用）。");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return (false, "Generate/All 失败：" + e.Message + "（详见 Console）");
            }
        }

        /// <summary>
        /// 构建代码包：同步校验 → CompileDll → 重建中转目录 → 生成清单 → RawFile 打包 → 清理旧版本。
        /// 热更列表为空也合法（产出只含空清单的包，引导器读到后直接走入口）。
        /// ⚠ 仅 Edit 模式（构建管线限制，调用方先过 <see cref="FrameworkAssetBuilder.EnsureReadyToBuild"/>）。
        /// </summary>
        public static (bool ok, string message) BuildCodePackage(FrameworkHotUpdateProfile profile, string version)
        {
            try
            {
                var sb = new StringBuilder();
                var target = EditorUserBuildSettings.activeBuildTarget;
                string packageName = profile.CodePackageName;

                // 1. 同步 + 校验：失败即停，不产出与配置不一致的包。
                string sync = profile.SyncToHybridCLRSettings();
                sb.AppendLine(sync);
                if (sync.Contains("✗")) return (false, sb.ToString().TrimEnd());

                var (installerOk, installerMessage) = ValidateHybridClrInstaller();
                sb.AppendLine(installerMessage);
                if (!installerOk) return (false, sb.ToString().TrimEnd());

                // 2. 编译热更 DLL（读刚同步过的 HybridCLRSettings）。
                var hotNames = profile.HotUpdateAssemblyNames;
                if (hotNames.Count > 0)
                {
                    var (fresh, freshnessMessage) = ValidateGenerationStamp(profile);
                    sb.AppendLine(freshnessMessage);
                    if (!fresh) return (false, sb.ToString().TrimEnd());
                }
                if (hotNames.Count > 0)
                    CompileDllCommand.CompileDll(target, EditorUserBuildSettings.development);

                // 3. 重建中转目录：热更 DLL（拓扑序）+ AOT 补元数据 DLL + 清单。
                RebuildDllAssetDir();
                var manifest = new HotUpdateManifest { Version = version };

                string hotDllDir = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target);
                foreach (var name in HotUpdateAssemblyGraph.SortByDependency(hotNames))
                {
                    string src = Path.Combine(hotDllDir, name + ".dll");
                    if (!File.Exists(src))
                        return (false, sb.AppendLine($"✗ 热更 DLL 缺失：{src}（CompileDll 未产出该程序集？）").ToString().TrimEnd());
                    File.Copy(src, Path.Combine(DllAssetDir, name + ".dll.bytes"), true);
                    manifest.HotUpdateDlls.Add(name + ".dll");
                }

                var (aotCopied, aotWarning) = CopyAotMetadataDlls(target, manifest, requireComplete: hotNames.Count > 0);
                if (aotWarning != null) sb.AppendLine(aotWarning);

                File.WriteAllText(Path.Combine(DllAssetDir, HotUpdateManifest.Location + ".bytes"), manifest.ToJson(prettyPrint: true));
                AssetDatabase.Refresh();

                // 4. 确保收集器后走 RawFile 管线打包。
                EnsureCollector(packageName);
                var buildParameters = new RawFileBuildParameters
                {
                    BuildOutputRoot = AssetBuildLayout.BundlesRoot,
                    BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(),
                    BuildPipeline = nameof(RawFileBuildPipeline),
                    BuildBundleType = (int)EBundleType.RawBundle,
                    BuildTarget = target,
                    PackageName = packageName,
                    PackageVersion = version,
                    VerifyBuildingResult = true,
                    FileNameStyle = EFileNameStyle.HashName,
                    // 代码包整包内置进 StreamingAssets：DLL 体量小，换来首启零下载（Host 只拉版本号+清单即可起）。
                    BundledCopyOption = EBundledCopyOption.ClearAndCopyAll,
                };
                var result = new RawFileBuildPipeline().Run(buildParameters, true);
                if (!result.Success)
                    return (false, sb.AppendLine($"✗ 代码包打包失败：[{result.FailedTask}] {result.ErrorInfo}").ToString().TrimEnd());

                CleanupOldVersions(packageName, target);

                sb.AppendLine($"✓ 代码包 '{packageName}' 构建完成 · 平台 {target} · 版本 {version}");
                sb.AppendLine($"    热更 DLL {manifest.HotUpdateDlls.Count} 个（拓扑序）：{string.Join(" → ", manifest.HotUpdateDlls)}");
                sb.AppendLine($"    AOT 补元数据 DLL {aotCopied} 个");
                return (true, sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return (false, e.Message + "（详见 Console）");
            }
        }

        // 中转目录全量重建：整目录都是生成物（含 .meta），残留旧 DLL 会被收集器一并打进包。
        private static void RebuildDllAssetDir()
        {
            if (Directory.Exists(DllAssetDir))
            {
                foreach (var file in Directory.GetFiles(DllAssetDir))
                    File.Delete(file);
            }
            else
            {
                Directory.CreateDirectory(DllAssetDir);
            }
        }

        // 按 AOTGenericReferences.PatchedAOTAssemblyList 拷裁剪后的 AOT DLL；返回 (拷贝数, 警告或 null)。
        // 有热更程序集时缺生成清单/裁剪 DLL 必须失败：继续打包只会把错误推迟到 IL2CPP 真机启动。
        private static (int copied, string warning) CopyAotMetadataDlls(
            BuildTarget target,
            HotUpdateManifest manifest,
            bool requireComplete)
        {
            List<string> patched;
            try
            {
                patched = ReadPatchedAotList();
            }
            catch (InvalidDataException e)
            {
                string message = "AOTGenericReferences.cs 格式异常，无法确认补元数据程序集清单：" + e.Message +
                    "。请重新执行「2. 生成桥接与裁剪文件」；若仍失败，请检查 HybridCLR 新版本的生成格式。";
                if (requireComplete) throw new InvalidOperationException(message, e);
                return (0, "⚠ " + message);
            }

            if (patched == null)
            {
                const string message = "未找到 AOTGenericReferences.cs（没跑过 Generate/All？）——" +
                    "IL2CPP 真机运行热更代码会缺泛型元数据。先执行「2. 生成桥接与裁剪文件」。";
                if (requireComplete) throw new InvalidOperationException(message);
                return (0, "⚠ " + message);
            }

            if (patched.Count == 0 && requireComplete)
                throw new InvalidOperationException(
                    "AOTGenericReferences.cs 的 PatchedAOTAssemblyList 合法但为空；当前存在热更程序集，" +
                    "无法确认 IL2CPP 真机所需泛型元数据。请重新执行「2. 生成桥接与裁剪文件」。");

            string stripDir = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
            var missing = new List<string>();
            int copied = 0;
            foreach (var dllName in patched) // 形如 "mscorlib.dll"
            {
                string src = Path.Combine(stripDir, dllName);
                if (!File.Exists(src))
                {
                    missing.Add(dllName);
                    continue;
                }
                File.Copy(src, Path.Combine(DllAssetDir, dllName + ".bytes"), true);
                manifest.AotMetadataDlls.Add(dllName);
                copied++;
            }

            if (missing.Count > 0 && requireComplete)
                throw new InvalidOperationException(
                    $"裁剪 AOT DLL 缺失 {missing.Count} 个（{string.Join(", ", missing)}）。" +
                    $"重跑「2. 生成桥接与裁剪文件」后再构建（来源：{stripDir}）。");

            string warning = missing.Count == 0
                ? null
                : $"⚠ 裁剪 AOT DLL 缺失 {missing.Count} 个（{string.Join(", ", missing)}）；当前没有热更程序集，已跳过。";
            return (copied, warning);
        }

        /// <summary>
        /// HybridCLR Package 与 Installer 写入的本地 libil2cpp 必须同版；只升级 UPM 包不会自动替换 C++ Runtime。
        /// 把官方 Player 构建期检查前移到 Generate/代码包入口，错误能在耗时工作开始前暴露。
        /// </summary>
        private static (bool ok, string message) ValidateHybridClrInstaller()
        {
            var installer = new InstallerController();
            if (!installer.HasInstalledHybridCLR())
                return (false, "✗ HybridCLR Runtime 尚未安装。请先执行 HybridCLR/Installer...");
            if (!string.Equals(installer.PackageVersion, installer.InstalledLibil2cppVersion, StringComparison.Ordinal))
                return (false, $"✗ HybridCLR Package {installer.PackageVersion} 与已安装 Runtime " +
                    $"{installer.InstalledLibil2cppVersion ?? "<无>"} 不一致。请重新执行 HybridCLR/Installer...");
            return (true, $"✓ HybridCLR Package / Runtime 均为 {installer.PackageVersion}");
        }

        private static void InvalidateGenerationStamp()
        {
            if (File.Exists(GenerationStampPath))
                File.Delete(GenerationStampPath);
        }

        private static void WriteGenerationStamp(FrameworkHotUpdateProfile profile)
        {
            var installer = new InstallerController();
            var stamp = new GenerationStamp
            {
                FormatVersion = 2,
                UnityVersion = Application.unityVersion,
                HybridClrVersion = installer.PackageVersion,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                Development = EditorUserBuildSettings.development,
                HotUpdateAssemblies = GetSortedHotUpdateAssemblyNames(profile),
                PackageLockSha256 = HashRequiredProjectFile("Packages/packages-lock.json"),
                NuGetPackagesSha256 = HashRequiredProjectFile("Packages/nuget-packages/packages.config"),
                HybridClrSettingsSha256 = HashRequiredProjectFile("ProjectSettings/HybridCLRSettings.asset"),
                PlayerBuildSettings = GetPlayerBuildSettingsFingerprint(),
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            };
            string dir = Path.GetDirectoryName(GenerationStampPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(GenerationStampPath, JsonUtility.ToJson(stamp, prettyPrint: true), Encoding.UTF8);
        }

        private static (bool ok, string message) ValidateGenerationStamp(FrameworkHotUpdateProfile profile)
        {
            if (!File.Exists(GenerationStampPath))
                return (false, "✗ 未找到 Generate/All 环境记录。请先执行「2. 生成桥接与裁剪文件」。");

            GenerationStamp stamp;
            try
            {
                stamp = JsonUtility.FromJson<GenerationStamp>(File.ReadAllText(GenerationStampPath, Encoding.UTF8));
            }
            catch (Exception e)
            {
                return (false, "✗ Generate/All 环境记录损坏：" + e.Message + "。请重新执行生成。");
            }

            if (stamp == null || stamp.FormatVersion != 2)
                return (false, "✗ Generate/All 环境记录版本不兼容。请重新执行生成。");

            var installer = new InstallerController();
            var mismatches = new List<string>();
            if (!string.Equals(stamp.UnityVersion, Application.unityVersion, StringComparison.Ordinal))
                mismatches.Add($"Unity {stamp.UnityVersion} → {Application.unityVersion}");
            if (!string.Equals(stamp.HybridClrVersion, installer.PackageVersion, StringComparison.Ordinal))
                mismatches.Add($"HybridCLR {stamp.HybridClrVersion} → {installer.PackageVersion}");
            string target = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!string.Equals(stamp.BuildTarget, target, StringComparison.Ordinal))
                mismatches.Add($"平台 {stamp.BuildTarget} → {target}");
            if (stamp.Development != EditorUserBuildSettings.development)
                mismatches.Add($"Development {stamp.Development} → {EditorUserBuildSettings.development}");
            if (!(stamp.HotUpdateAssemblies ?? new List<string>()).SequenceEqual(GetSortedHotUpdateAssemblyNames(profile)))
                mismatches.Add("热更程序集列表已变化");
            if (!string.Equals(stamp.PackageLockSha256, HashRequiredProjectFile("Packages/packages-lock.json"), StringComparison.Ordinal))
                mismatches.Add("Packages/packages-lock.json 已变化");
            if (!string.Equals(stamp.NuGetPackagesSha256, HashRequiredProjectFile("Packages/nuget-packages/packages.config"), StringComparison.Ordinal))
                mismatches.Add("NuGet packages.config 已变化");
            if (!string.Equals(stamp.HybridClrSettingsSha256, HashRequiredProjectFile("ProjectSettings/HybridCLRSettings.asset"), StringComparison.Ordinal))
                mismatches.Add("HybridCLRSettings 已变化");
            string playerSettings = GetPlayerBuildSettingsFingerprint();
            if (!string.Equals(stamp.PlayerBuildSettings, playerSettings, StringComparison.Ordinal))
                mismatches.Add($"AOT PlayerSettings {stamp.PlayerBuildSettings} → {playerSettings}");

            return mismatches.Count == 0
                ? (true, $"✓ Generate/All 产物与当前环境一致（{stamp.GeneratedAtUtc}）")
                : (false, "✗ Generate/All 产物已过期：" + string.Join("；", mismatches) +
                    "。请重新执行「2. 生成桥接与裁剪文件」。");
        }

        private static List<string> GetSortedHotUpdateAssemblyNames(FrameworkHotUpdateProfile profile)
            => profile.HotUpdateAssemblyNames.OrderBy(name => name, StringComparer.Ordinal).ToList();

        // UPM 包锁和 NuGet 清单决定实际进入 AOT 世界的第三方程序集版本；HybridCLRSettings 决定桥接、裁剪与生成策略。
        // 只看“版本显示值”不够，直接记内容哈希能覆盖 lock 重解析、显式 DLL 依赖与设置新增字段等变化。
        private static string HashRequiredProjectFile(string relativePath)
        {
            string path = Path.Combine(AssetBuildLayout.ProjectRoot, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"生成环境输入文件不存在：{relativePath}", path);
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // 只记录确实影响 AOT 裁剪/代码生成的 PlayerSettings，避免产品名、图标等无关改动让日常热更被迫重跑 Generate。
        private static string GetPlayerBuildSettingsFingerprint()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group);
            return string.Join("|", new[]
            {
                PlayerSettings.GetScriptingBackend(namedTarget).ToString(),
                PlayerSettings.GetApiCompatibilityLevel(namedTarget).ToString(),
                PlayerSettings.GetManagedStrippingLevel(namedTarget).ToString(),
                PlayerSettings.GetIl2CppCompilerConfiguration(namedTarget).ToString(),
                $"StripEngineCode={PlayerSettings.stripEngineCode}",
            });
        }

        [Serializable]
        private sealed class GenerationStamp
        {
            public int FormatVersion;
            public string UnityVersion;
            public string HybridClrVersion;
            public string BuildTarget;
            public bool Development;
            public List<string> HotUpdateAssemblies;
            public string PackageLockSha256;
            public string NuGetPackagesSha256;
            public string HybridClrSettingsSha256;
            public string PlayerBuildSettings;
            public string GeneratedAtUtc;
        }

        // 解析 Generate 产物 AOTGenericReferences.cs 里的 PatchedAOTAssemblyList。
        // 走文本解析而非反射读编译产物：不依赖 Assembly-CSharp 的编译状态（生成文件刚落盘还没编译时也能读）。
        private static List<string> ReadPatchedAotList()
        {
            string path = "Assets/" + HybridCLRSettings.Instance.outputAOTGenericReferenceFile;
            if (!File.Exists(path)) return null;

            string text = File.ReadAllText(path);
            int start = text.IndexOf("PatchedAOTAssemblyList", StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidDataException("找不到 PatchedAOTAssemblyList 标记");
            int blockStart = text.IndexOf('{', start);
            if (blockStart < 0)
                throw new InvalidDataException("PatchedAOTAssemblyList 缺少列表起始符 '{'");
            int end = text.IndexOf("};", blockStart, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidDataException("PatchedAOTAssemblyList 缺少列表结束符 '};'");
            string block = text.Substring(blockStart, end - blockStart);
            return Regex.Matches(block, "\"([^\"]+\\.dll)\"")
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToList();
        }

        // 确保收集器里有「代码包 + 自动组 + 指向中转目录的 RawFile 收集器」。幂等；只接管自动组，不动项目手工配置。
        private static void EnsureCollector(string packageName)
        {
            var setting = BundleCollectorSettingData.Setting;
            var pkg = setting.Packages.FirstOrDefault(p => p.PackageName == packageName);
            if (pkg == null)
            {
                pkg = new BundleCollectorPackage
                {
                    PackageName = packageName,
                    PackageDesc = "热更代码包（框架热更构建管线自动创建）",
                    AutoCollectShaders = false, // 纯 DLL 包，无 shader 可收
                };
                setting.Packages.Add(pkg);
            }

            // EnableAddressable 必须为 true：AddressByFileName 生成的短地址（"hotupdate_manifest"、"Game.Framework.dll" 等）
            // 只有在此选项开启时才会写入 YooAsset location 字典；关闭时只能用完整 AssetPath 查询，
            // 运行时 LoadAssetAsync("hotupdate_manifest") 就会报 "Location is invalid"。
            // 对已有 package 也强制更新（幂等），避免历史存档未开启时遗留旧包失效。
            pkg.EnableAddressable = true;

            var group = pkg.Groups.FirstOrDefault(g => g.GroupName == CodeGroupName);
            if (group == null)
            {
                group = new BundleCollectorGroup { GroupName = CodeGroupName, GroupDesc = "热更 DLL + 清单（自动维护，勿手动改）" };
                pkg.Groups.Add(group);
            }

            if (group.Collectors.All(c => c.CollectPath != DllAssetDir))
            {
                group.Collectors.Add(new BundleCollector
                {
                    CollectPath = DllAssetDir,
                    CollectorGUID = AssetDatabase.AssetPathToGUID(DllAssetDir),
                    CollectorType = ECollectorType.MainAssetCollector,
                    AddressRuleName = nameof(AddressByFileName), // 去扩展名：xxx.dll.bytes → location "xxx.dll"
                    PackRuleName = nameof(PackRawFile),          // RawFile 包要求每文件独立 bundle
                    FilterRuleName = nameof(CollectAll),
                });
            }
            BundleCollectorSettingData.SaveFile();
        }

        // 只保留最近 2 个版本目录（与 FrameworkAssetBuilder.CleanupOldVersions 同思路；代码包不读资源 profile 的保留数）。
        private static void CleanupOldVersions(string packageName, BuildTarget target)
        {
            string root = $"{AssetBuildLayout.BundlesRoot}/{target}/{packageName}";
            if (!Directory.Exists(root)) return;

            var versionDirs = Directory.GetDirectories(root)
                .Where(d => !string.Equals(Path.GetFileName(d), OutputCacheFolderName, StringComparison.Ordinal))
                .OrderByDescending(Directory.GetLastWriteTime)
                .ToList();
            for (int i = 2; i < versionDirs.Count; i++)
            {
                try { Directory.Delete(versionDirs[i], true); }
                catch (Exception e) { Debug.LogWarning($"[热更构建] 清理旧版本失败 {versionDirs[i]}：{e.Message}"); }
            }
        }
    }
}
