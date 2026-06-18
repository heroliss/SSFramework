using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using YooAsset.Editor; // BundleCollectorSettingData / EBundledCopyOption

namespace Game.Framework.Build
{
    /// <summary>
    /// 资源构建配置（编辑器资产）——「打哪些包 + 每包构建参数」的**单一配置源**：
    /// 统一构建菜单（<c>SSFramework/资源构建/*</c>）、CI 入口（<see cref="FrameworkAssetBuilder.BuildAll"/>）、
    /// 以及将来的构建窗口都读这一个 profile。
    ///
    /// 这是**项目配置实例**，不是框架代码——资产入库放在项目配置位 <c>Assets/Game/Settings/</c>（不在 <c>Framework/</c> 内，
    /// 框架抽 UPM 包时项目配置不该进包，ADR-0010/0011）；<see cref="Resolve"/> 按类型扫描定位（不认路径），
    /// 找不到时在该位自动建一个默认 profile，找到多个时取第一个并警告（全工程应只保留一个）。
    /// 这是**编辑器构建配置**，不是运行时数据（运行时资源行为看 <c>AssetSystemConfigModel</c>），故只存在于 Editor 程序集。
    /// 字段只读暴露：修改只经 Inspector / <see cref="SyncFromCollector"/>，保证「资产 = 唯一真源」不被代码旁路改写。
    /// </summary>
    [CreateAssetMenu(fileName = "FrameworkAssetBuildProfile", menuName = "SSFramework/资源构建配置 (Build Profile)")]
    public sealed class FrameworkAssetBuildProfile : ScriptableObject
    {
        private const string DefaultVersionFormat = "yyyyMMddHHmmss";

        [Tooltip("逐包构建配置。包名需与 YooAsset 收集器（Bundle Collector）里的包一致。")]
        [FormerlySerializedAs("Packages")]
        [SerializeField] private List<PackageBuildEntry> _packages = new();

        [Header("构建")]
        [Tooltip("默认版本号格式（DateTime.ToString 格式串）。CI 用 -version 显式覆盖以保证可追溯。\n格式串非法时构建不中断：自动回退默认格式并报错提示修正。")]
        [FormerlySerializedAs("VersionFormat")]
        [SerializeField] private string _versionFormat = DefaultVersionFormat;

        [Tooltip("Bundles 每个包保留最近几个版本——更旧的版本目录构建后自动删，省空间。要回滚/对比可调大。")]
        [FormerlySerializedAs("BundleVersionsToKeep")]
        [Min(1)] [SerializeField] private int _bundleVersionsToKeep = 2;

        [Header("本地联调（不入库，仅本机测 Host）")]
        [Tooltip("本地 CDN 服务端口（python http 服务，伺服 AssetBuild/Deploy）。⚠ 必须与场景 AssetSystemConfigModel.CdnUrls 第一条（主）的端口一致，Host 才能下到东西。")]
        [FormerlySerializedAs("LocalServePort")]
        [Min(1)] [SerializeField] private int _localServePort = 8080;

        [Tooltip("本地 CDN 服务限速（KB/s，模拟弱网测下载/进度 UI）。0 = 不限速。\n" +
                 "注意：限速是【每连接】的；实际总带宽 ≈ 该值 × 并发数（AssetSystemConfigModel.DownloadingMaxNumber）。\n" +
                 "想精确模拟把并发设 1。改了限速要先关掉已开的服务再重启才生效。")]
        [FormerlySerializedAs("LocalServeThrottleKBps")]
        [Min(0)] [SerializeField] private int _localServeThrottleKBps = 0;

        /// <summary>Bundles 每个包保留的最近版本数（构建后清理更旧的版本目录）。</summary>
        public int BundleVersionsToKeep => _bundleVersionsToKeep;

        /// <summary>本地 CDN 服务端口（联调专用，须与 AssetSystemConfigModel.CdnUrls 主地址端口一致）。</summary>
        public int LocalServePort => _localServePort;

        /// <summary>本地 CDN 服务每连接限速（KB/s）；0 = 不限速。</summary>
        public int LocalServeThrottleKBps => _localServeThrottleKBps;

        /// <summary>
        /// 本 profile 中所有「参与构建」的包名（供构建器逐包构建）。
        /// 已 Trim + 去重：Inspector 手填的首尾空白、误加的重名条目不会造成「找不到包 / 同包重复构建」。
        /// </summary>
        public IEnumerable<string> EnabledPackageNames
            => _packages.Where(p => p != null && p.BuildEnabled && !string.IsNullOrWhiteSpace(p.PackageName))
                        .Select(p => p.PackageName.Trim())
                        .Distinct();

        /// <summary>取某个包的构建参数（按 Trim 后的包名匹配）；找不到返回 null（构建器据此回退到默认参数 + 警告）。</summary>
        public PackageBuildEntry GetEntry(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return null;
            packageName = packageName.Trim();
            return _packages.FirstOrDefault(p => p != null && p.PackageName != null && p.PackageName.Trim() == packageName);
        }

        /// <summary>
        /// 生成「当前时间」版本号（菜单构建 / CI 未传 -version 时的回退）。
        /// 格式串非法时不中断构建：回退默认格式并 LogError 提示修正——版本号容错收口在这一处，调用方不必各自 try/catch。
        /// </summary>
        public string ResolveVersionNow()
        {
            try
            {
                return DateTime.Now.ToString(_versionFormat);
            }
            catch (FormatException)
            {
                Debug.LogError($"[AssetBuilder] VersionFormat '{_versionFormat}' 不是合法的 DateTime 格式串，" +
                               $"本次已回退 '{DefaultVersionFormat}'。请到构建 profile 修正。");
                return DateTime.Now.ToString(DefaultVersionFormat);
            }
        }

        /// <summary>
        /// 按 YooAsset 收集器对账包列表（单向：collector → profile），用于「误删条目恢复」「收集器新增包」。
        /// <list type="bullet">
        ///   <item>收集器有、profile 没有的包 → 按默认参数<b>补上</b>（恢复误删）。</item>
        ///   <item>profile 已有的条目 → <b>原样保留</b>，不覆盖你调过的每包设置（幂等；无变化时不写脏资产）。</item>
        ///   <item>profile 有、收集器已没有的包（改名/删包）→ 标记<b>孤儿仅警告，不自动删</b>（避免误删配置）。</item>
        ///   <item>profile 内同名重复条目 → 仅警告（构建以第一条为准，请手动合并）。</item>
        /// </list>
        /// 注意：排除某个包请用 <see cref="PackageBuildEntry.BuildEnabled"/>=false，<b>不要删条目</b>——删了会被本对账补回。
        /// 返回人类可读摘要。
        /// </summary>
        public string SyncFromCollector()
        {
            var collectorNames = BundleCollectorSettingData.Setting.Packages
                .Select(p => p.PackageName?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            // 现有条目名（Trim 后）；顺带找出 profile 内部的同名重复（GetEntry/构建只认第一条，重复条目是配置错误）。
            var existing = new HashSet<string>();
            var duplicates = new List<string>();
            foreach (var p in _packages)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.PackageName)) continue;
                var name = p.PackageName.Trim();
                if (!existing.Add(name)) duplicates.Add(name);
            }

            var added = new List<string>();
            foreach (var name in collectorNames)
            {
                if (existing.Contains(name)) continue;
                _packages.Add(new PackageBuildEntry(name));
                existing.Add(name);
                added.Add(name);
            }

            var orphans = existing.Where(n => !collectorNames.Contains(n)).ToList();

            // 只有真改了（补了条目）才写脏保存——无变化的对账不产生资产 diff。
            if (added.Count > 0)
            {
                EditorUtility.SetDirty(this);
                AssetDatabase.SaveAssets();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"对账完成：收集器共 {collectorNames.Count} 个包。");
            sb.AppendLine(added.Count > 0 ? $"新增（恢复）{added.Count} 个：{string.Join(", ", added)}" : "新增（恢复）：无");
            if (duplicates.Count > 0)
                sb.AppendLine($"⚠ 重复条目 {duplicates.Count} 个（构建只认第一条，请手动合并）：{string.Join(", ", duplicates.Distinct())}");
            if (orphans.Count > 0)
                sb.AppendLine($"⚠ 孤儿条目 {orphans.Count} 个（收集器已无此包，未自动删，请确认是否手动移除）：{string.Join(", ", orphans)}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 解析全工程唯一的构建 profile：先找已有资产（找到多个 → 取第一个并警告），
        /// 没有就按收集器的包列表自动建一个默认 profile（落在 <c>Assets/Game/Framework/Build/</c>）。
        /// 用 <c>AssetDatabase.CreateAsset</c> 程序化创建，不手改 YAML。
        /// </summary>
        public static FrameworkAssetBuildProfile Resolve()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(FrameworkAssetBuildProfile));
            if (guids.Length > 0)
            {
                // CreateAssetMenu 暴露在外，误建第二个 profile 时取哪个是 GUID 顺序的偶然——明确警告，避免「改了配置不生效」难排查。
                if (guids.Length > 1)
                {
                    var paths = guids.Select(AssetDatabase.GUIDToAssetPath);
                    Debug.LogWarning("[AssetBuilder] 找到多个构建 profile，仅第一个生效，请删到只剩一个：\n  " +
                                     string.Join("\n  ", paths));
                }
                return AssetDatabase.LoadAssetAtPath<FrameworkAssetBuildProfile>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            var profile = CreateInstance<FrameworkAssetBuildProfile>();
            foreach (var pkg in BundleCollectorSettingData.Setting.Packages)
            {
                if (string.IsNullOrWhiteSpace(pkg.PackageName)) continue;
                profile._packages.Add(new PackageBuildEntry(pkg.PackageName.Trim()));
            }

            const string dir = "Assets/Game/Settings"; // 项目配置位，不在 Framework/ 内（ADR-0011）
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/Game", "Settings");
            string path = dir + "/FrameworkAssetBuildProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AssetBuilder] 未找到构建 profile，已按收集器包列表自动创建：{path}");
            return profile;
        }
    }

    /// <summary>
    /// 单个资源包的构建参数。字段只读暴露（修改只经 Inspector），与 <c>AssetPackageConfig</c>（运行时包配置）同风格。
    /// 内置 shader 包开关的细节见 <see cref="GenerateBuiltinShaderBundle"/>。
    /// </summary>
    [Serializable]
    public sealed class PackageBuildEntry
    {
        [Tooltip("资源包名称，需与 YooAsset 收集器里的包一致。")]
        [FormerlySerializedAs("PackageName")]
        [SerializeField] private string _packageName;

        [Tooltip("是否参与「构建资源包」。关掉则该包不被构建（保留配置备用）。")]
        [FormerlySerializedAs("BuildEnabled")]
        [SerializeField] private bool _buildEnabled = true;

        [Tooltip("首包策略：哪些 bundle 随安装包进 StreamingAssets。\n" +
                 "ClearAndCopyByTags = 清空后按 Tags 拷贝（最常用）；其余见 YooAsset EBundledCopyOption。")]
        [FormerlySerializedAs("BuiltinCopy")]
        [SerializeField] private EBundledCopyOption _builtinCopy = EBundledCopyOption.ClearAndCopyByTags;

        [Tooltip("首包 tag（多 tag 用分号 ; 分隔，YooAsset 内部按 ; 切）。\n" +
                 "留空 = 不内置任何 bundle、只出内置清单（全部运行时从 CDN 下）。")]
        [FormerlySerializedAs("BuiltinTags")]
        [SerializeField] private string _builtinTags = "";

        [Tooltip("是否生成「Unity 内置 shader 包」（把引擎内置 shader 提取成一个共享包，避免每个 bundle 各自内嵌一份）。\n\n" +
                 "⚠ 重要：包里若【没有任何】引用内置 shader 的资产（如纯 Sprite / 纯数据包），必须【关】——\n" +
                 "否则 SBP 的 obsolete 任务 CreateBuiltInShadersBundle 取空 layout 会崩（IBundleExplictObjectLayout was not available）。\n" +
                 "真实有材质/UI/模型的包【开】：内置 shader 正确去重。这是 YooAsset 仍用 obsolete 任务的已知坑，详见 FrameworkAssetBuilder 注释。")]
        [FormerlySerializedAs("GenerateBuiltinShaderBundle")]
        [SerializeField] private bool _generateBuiltinShaderBundle = true;

        // Unity 序列化需要无参构造；代码路径（对账补条目 / 自动建 profile）用带名构造。
        public PackageBuildEntry() { }

        public PackageBuildEntry(string packageName) => _packageName = packageName;

        public string PackageName => _packageName;

        /// <summary>是否参与构建；false = 跳过但保留配置（排除包用它，别删条目——会被对账补回）。</summary>
        public bool BuildEnabled => _buildEnabled;

        /// <summary>首包拷贝策略（哪些 bundle 随安装包进 StreamingAssets）。</summary>
        public EBundledCopyOption BuiltinCopy => _builtinCopy;

        /// <summary>首包 tag（分号分隔）；空 = 不内置任何 bundle、只出内置清单。</summary>
        public string BuiltinTags => _builtinTags;

        /// <summary>是否生成内置 shader 包：有材质/UI/模型的包开（正确去重）；零内置 shader 的包必须关（SBP obsolete 任务会崩）。</summary>
        public bool GenerateBuiltinShaderBundle => _generateBuiltinShaderBundle;
    }
}
