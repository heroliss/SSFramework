using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using YooAsset;        // EBundledCopyOption
using YooAsset.Editor; // BundleCollectorSettingData

namespace Game.Framework.Build
{
    /// <summary>
    /// 资源构建配置（编辑器资产）——「打哪些包 + 每包构建参数」的**单一配置源**：
    /// 统一构建菜单（<c>SSFramework/资源构建/*</c>）、CI 入口（<see cref="FrameworkAssetBuilder.BuildAll"/>）、
    /// 以及将来的构建窗口都读这一个 profile。
    ///
    /// 资产入库（放在 <c>Assets/Game/Framework/Build/</c>），随项目版本控制；<see cref="Resolve"/> 找不到时自动建一个默认 profile。
    /// 这是**编辑器构建配置**，不是运行时数据（运行时资源行为看 <c>AssetSystemConfigModel</c>），故只存在于 Editor 程序集。
    /// </summary>
    [CreateAssetMenu(fileName = "FrameworkAssetBuildProfile", menuName = "SSFramework/资源构建配置 (Build Profile)")]
    public sealed class FrameworkAssetBuildProfile : ScriptableObject
    {
        [Tooltip("逐包构建配置。包名需与 YooAsset 收集器（Bundle Collector）里的包一致。")]
        public List<PackageBuildEntry> Packages = new();

        [Header("本地联调（不入库，仅本机测 Host）")]
        [Tooltip("本地 CDN 部署目录名（项目根下，已加进 .gitignore）。「部署到本地 CDN」把构建产物平铺到这里。")]
        public string LocalCdnDirName = "CDN";

        [Tooltip("本地 CDN 服务端口（python -m http.server）。⚠ 必须与场景 AssetSystemConfigModel.MainCdnUrl 的端口一致，Host 才能下到东西。")]
        [Min(1)] public int LocalServePort = 8080;

        [Header("生产产物")]
        [Tooltip("生产构建整理目录（项目根下，已 gitignore）。CI 把这里整目录同步上真实 CDN。")]
        public string ProductionOutputDir = "BuildOutput/CDN";

        [Tooltip("默认版本号格式（DateTime.ToString 格式串）。CI 用 -version 显式覆盖以保证可追溯。")]
        public string VersionFormat = "yyyyMMddHHmmss";

        /// <summary>本 profile 中所有「参与构建」的包名（供构建器逐包构建）。</summary>
        public IEnumerable<string> EnabledPackageNames
            => Packages.Where(p => p != null && p.BuildEnabled && !string.IsNullOrWhiteSpace(p.PackageName))
                       .Select(p => p.PackageName);

        /// <summary>取某个包的构建参数；找不到返回 null（构建器据此回退到默认参数 + 警告）。</summary>
        public PackageBuildEntry GetEntry(string packageName)
            => Packages.FirstOrDefault(p => p != null && p.PackageName == packageName);

        /// <summary>
        /// 按 YooAsset 收集器对账包列表（单向：collector → profile），用于「误删条目恢复」「收集器新增包」。
        /// <list type="bullet">
        ///   <item>收集器有、profile 没有的包 → 按默认参数<b>补上</b>（恢复误删）。</item>
        ///   <item>profile 已有的条目 → <b>原样保留</b>，不覆盖你调过的每包设置（幂等）。</item>
        ///   <item>profile 有、收集器已没有的包（改名/删包）→ 标记<b>孤儿仅警告，不自动删</b>（避免误删配置）。</item>
        /// </list>
        /// 注意：排除某个包请用 <see cref="PackageBuildEntry.BuildEnabled"/>=false，<b>不要删条目</b>——删了会被本对账补回。
        /// 返回人类可读摘要；会写脏 + 保存资产。
        /// </summary>
        public string SyncFromCollector()
        {
            var collectorNames = BundleCollectorSettingData.Setting.Packages
                .Select(p => p.PackageName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var existing = new HashSet<string>(
                Packages.Where(p => p != null && !string.IsNullOrWhiteSpace(p.PackageName)).Select(p => p.PackageName));

            var added = new List<string>();
            foreach (var name in collectorNames)
            {
                if (existing.Contains(name)) continue;
                Packages.Add(new PackageBuildEntry { PackageName = name });
                added.Add(name);
            }

            var orphans = Packages
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.PackageName) && !collectorNames.Contains(p.PackageName))
                .Select(p => p.PackageName)
                .ToList();

            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();

            var sb = new StringBuilder();
            sb.AppendLine($"对账完成：收集器共 {collectorNames.Count} 个包。");
            sb.AppendLine(added.Count > 0 ? $"新增（恢复）{added.Count} 个：{string.Join(", ", added)}" : "新增（恢复）：无");
            if (orphans.Count > 0)
                sb.AppendLine($"⚠ 孤儿条目 {orphans.Count} 个（收集器已无此包，未自动删，请确认是否手动移除）：{string.Join(", ", orphans)}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 解析全工程唯一的构建 profile：先找已有资产，没有就按收集器的包列表自动建一个默认 profile（落在 <c>Assets/Game/Framework/Build/</c>）。
        /// 用 <c>AssetDatabase.CreateAsset</c> 程序化创建，不手改 YAML。
        /// </summary>
        public static FrameworkAssetBuildProfile Resolve()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(FrameworkAssetBuildProfile));
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<FrameworkAssetBuildProfile>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));

            var profile = CreateInstance<FrameworkAssetBuildProfile>();
            foreach (var pkg in BundleCollectorSettingData.Setting.Packages)
            {
                if (string.IsNullOrWhiteSpace(pkg.PackageName)) continue;
                profile.Packages.Add(new PackageBuildEntry { PackageName = pkg.PackageName });
            }

            const string dir = "Assets/Game/Framework/Build";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/Game/Framework", "Build");
            string path = dir + "/FrameworkAssetBuildProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AssetBuilder] 未找到构建 profile，已按收集器包列表自动创建：{path}");
            return profile;
        }
    }

    /// <summary>单个资源包的构建参数。内置 shader 包开关的细节见 <see cref="GenerateBuiltinShaderBundle"/>。</summary>
    [System.Serializable]
    public sealed class PackageBuildEntry
    {
        [Tooltip("资源包名称，需与 YooAsset 收集器里的包一致。")]
        public string PackageName;

        [Tooltip("是否参与「构建资源包」。关掉则该包不被构建（保留配置备用）。")]
        public bool BuildEnabled = true;

        [Tooltip("首包策略：哪些 bundle 随安装包进 StreamingAssets。\n" +
                 "ClearAndCopyByTags = 清空后按 Tags 拷贝（最常用）；其余见 YooAsset EBundledCopyOption。")]
        public EBundledCopyOption BuiltinCopy = EBundledCopyOption.ClearAndCopyByTags;

        [Tooltip("首包 tag（多 tag 用分号 ; 分隔，YooAsset 内部按 ; 切）。\n" +
                 "留空 = 不内置任何 bundle、只出内置清单（全部运行时从 CDN 下）。")]
        public string BuiltinTags = "";

        [Tooltip("是否生成「Unity 内置 shader 包」（把引擎内置 shader 提取成一个共享包，避免每个 bundle 各自内嵌一份）。\n\n" +
                 "⚠ 重要：包里若【没有任何】引用内置 shader 的资产（如纯 Sprite / 纯数据包），必须【关】——\n" +
                 "否则 SBP 的 obsolete 任务 CreateBuiltInShadersBundle 取空 layout 会崩（IBundleExplictObjectLayout was not available）。\n" +
                 "真实有材质/UI/模型的包【开】：内置 shader 正确去重。这是 YooAsset 仍用 obsolete 任务的已知坑，详见 FrameworkAssetBuilder 注释。")]
        public bool GenerateBuiltinShaderBundle = true;
    }
}
