using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 热更构建配置（编辑器资产）——「哪些程序集热更 + 代码包名」的**单一真源**（ADR-0008）。
    ///
    /// 所有热更相关的派生物都从这里出，不做第二份人工维护：
    /// <list type="bullet">
    ///   <item><see cref="SyncToHybridCLRSettings"/> 把列表写入 <c>HybridCLRSettings.hotUpdateAssemblyDefinitions</c>
    ///         （HybridCLR 的 Generate / CompileDll / 打包剔除都读它）——**不要再去 HybridCLR Settings 里手填**。</item>
    ///   <item>热更构建管线据此编译/拷贝 DLL 并生成运行时清单（hotupdate manifest，含拓扑排序的加载顺序）。</item>
    ///   <item>运行时引导器只读随代码包下发的 manifest，不读本资产（清单本身因此可热更）。</item>
    /// </list>
    ///
    /// 列表顺序无所谓——加载顺序由 asmdef 引用图拓扑排序自动生成；列表合法性（AOT 不得引用热更）
    /// 在同步与构建时经 <see cref="HotUpdateAssemblyGraph"/> 自动校验，违规拦下并指出元凶。
    ///
    /// 这是**项目配置实例**，默认新建到 <c>Assets/Settings/SSFramework/</c>（不在 <c>Framework/</c> 内，ADR-0010/0011）；
    /// <see cref="TryResolve"/> 无副作用定位；工作台明确创建时才调用 <see cref="Resolve"/>，按默认候选（内核 + Asset.Yoo，见 ADR-0008 §2）建资产。
    /// 字段只读暴露：修改只经 Inspector，保证「资产 = 唯一真源」不被代码旁路改写。
    /// </summary>
    [CreateAssetMenu(fileName = "FrameworkHotUpdateProfile", menuName = "SSFramework/热更构建配置 (HotUpdate Profile)")]
    public sealed class FrameworkHotUpdateProfile : ScriptableObject
    {
        private static int _duplicateWarningRevision = -1;

        [Tooltip("热更程序集（asmdef 引用）。在列表 = 热更（运行时从代码包加载），不在 = AOT（随安装包固化）。\n" +
                 "铁律：谁被热更，引用它的程序集必须也在列表里（AOT 不能引用热更）——同步/构建时自动校验拦截。\n" +
                 "顺序随意：实际加载顺序按 asmdef 引用图拓扑排序自动生成，不需要人排。")]
        [InspectorName("热更新程序集（asmdef）")]
        [SerializeField] private List<AssemblyDefinitionAsset> _hotUpdateAssemblies = new();

        [Tooltip("代码包名：装热更 DLL + AOT 补元数据 DLL + 清单的 YooAsset RawFile 包。\n" +
                 "归 Boot 引导器管，与业务资源包彻底分家（互不知晓、互不初始化）。名称需与 YooAsset 收集器一致，" +
                 "并能作为单一跨平台目录名；不能含空白、路径分隔符或 URL 结构字符。")]
        [InspectorName("热更新代码包名")]
        [SerializeField] private string _codePackageName = "CodePackage";

        /// <summary>代码包名（YooAsset RawFile 包）。空白时回退默认名，构建/引导两侧共用。</summary>
        public string CodePackageName => string.IsNullOrWhiteSpace(_codePackageName) ? "CodePackage" : _codePackageName.Trim();

        /// <summary>列表中的非空 asmdef 条目（Inspector 留下的空槽位被过滤）。</summary>
        public IEnumerable<AssemblyDefinitionAsset> HotUpdateAssemblies => _hotUpdateAssemblies.Where(a => a != null);

        /// <summary>热更程序集名（从 asmdef 内容解析，已去重）。</summary>
        public List<string> HotUpdateAssemblyNames
            => HotUpdateAssemblies.Select(GetAssemblyName)
                                  .Where(n => !string.IsNullOrEmpty(n))
                                  .Distinct()
                                  .ToList();

        // asmdef 的资产文件名可以与程序集名不同（文件随便改名），程序集名以 JSON 里的 name 字段为准。
        private static string GetAssemblyName(AssemblyDefinitionAsset asmdef)
        {
            try
            {
                return JsonUtility.FromJson<AsmdefJson>(asmdef.text)?.name;
            }
            catch (Exception)
            {
                Debug.LogError($"[热更构建] 解析 asmdef '{asmdef.name}' 失败（JSON 损坏？），已跳过该条目。");
                return null;
            }
        }

        [Serializable]
        private sealed class AsmdefJson
        {
            public string name;
        }

        /// <summary>
        /// 校验列表合法性并写入 <c>HybridCLRSettings.hotUpdateAssemblyDefinitions</c>；违规时**不写入**（保护 HybridCLR 配置不被污染）。
        /// 返回人类可读摘要。改了列表之后必须重新同步——HybridCLR 的 Generate / CompileDll 读的是 settings，不是本资产。
        /// </summary>
        public string SyncToHybridCLRSettings()
        {
            var names = HotUpdateAssemblyNames;
            var (ok, validation) = HotUpdateAssemblyGraph.Validate(names);

            var sb = new StringBuilder();
            sb.AppendLine(validation);
            if (!ok)
            {
                sb.AppendLine("✗ 存在违规，未写入 HybridCLRSettings。修正列表后重新同步。");
                return sb.ToString().TrimEnd();
            }

            var settings = HybridCLRSettings.Instance;
            settings.hotUpdateAssemblyDefinitions = HotUpdateAssemblies.ToArray();
            HybridCLRSettings.Save();
            sb.AppendLine($"✓ 已写入 HybridCLRSettings.hotUpdateAssemblyDefinitions（{names.Count} 个）：{string.Join(", ", names)}");

            // 我们只接管 asmdef 列表；HybridCLR 还有一个字符串名列表，两者取并集生效——
            // 有人手填过字符串列表会造成「这个程序集怎么也被剔除了」的暗坑，提醒清理而不擅自清空（可能是刻意配置）。
            if (settings.hotUpdateAssemblies != null && settings.hotUpdateAssemblies.Length > 0)
                sb.AppendLine($"⚠ HybridCLRSettings.hotUpdateAssemblies（字符串名列表）非空：{string.Join(", ", settings.hotUpdateAssemblies)}" +
                              "——它与 asmdef 列表取并集生效。本框架以 asmdef 列表为单一真源，若非刻意为之请清空该字段。");
            return sb.ToString().TrimEnd();
        }

        /// <summary>无副作用查找全工程唯一的热更 profile；不存在时返回 <c>false</c>。</summary>
        public static bool TryResolve(out FrameworkHotUpdateProfile profile)
        {
            if (!FrameworkEditorProfileCatalog.TryResolveFirst(out profile, out IReadOnlyList<string> paths))
            {
                return false;
            }
            int revision = FrameworkEditorProfileCatalog.Revision;
            if (paths.Count > 1 && _duplicateWarningRevision != revision)
            {
                _duplicateWarningRevision = revision;
                Debug.LogWarning("[热更构建] 找到多个热更 profile，仅第一个生效，请删到只剩一个：\n  " +
                                 string.Join("\n  ", paths));
            }
            return true;
        }

        /// <summary>
        /// 解析全工程唯一的热更 profile：先找已有资产（找到多个 → 取第一个并警告），
        /// 没有就按默认档位（内核 + Asset.Yoo 热更）自动建一个（落在通用项目配置目录）。
        /// </summary>
        public static FrameworkHotUpdateProfile Resolve()
        {
            if (TryResolve(out FrameworkHotUpdateProfile existing)) return existing;

            FrameworkEditorProfileCatalog.Refresh(typeof(FrameworkHotUpdateProfile));
            if (TryResolve(out existing)) return existing;
            string dir = FrameworkProjectSettingsLocation.EnsureDirectory();
            string path = dir + "/FrameworkHotUpdateProfile.asset";
            existing = FrameworkProjectSettingsLocation
                .GetExistingProfileOrThrow<FrameworkHotUpdateProfile>(path);
            if (existing != null) return existing;

            var profile = CreateInstance<FrameworkHotUpdateProfile>();
            // 默认档位（ADR-0008 §2）：内核 + YooAsset 适配模块热更；业务程序集出现后由项目自行加进列表。
            TryAddDefault(profile, "Game.Framework");
            TryAddDefault(profile, "Game.Framework.Asset.Yoo");

            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            FrameworkEditorProfileCatalog.Refresh(typeof(FrameworkHotUpdateProfile));
            if (!TryResolve(out FrameworkHotUpdateProfile effective) || effective != profile)
                throw new InvalidOperationException(
                    $"热更 profile 已写入但未成为稳定排序后的生效项：{path}。请检查重复配置后重试。");
            Debug.Log($"[热更构建] 已按用户请求创建默认热更 profile（内核 + Asset.Yoo 候选）：{path}");
            return effective;
        }

        private static void TryAddDefault(FrameworkHotUpdateProfile profile, string assemblyName)
        {
            var matches = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(path => (path, asset: AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(path)))
                .Where(item => item.asset != null &&
                               string.Equals(GetAssemblyName(item.asset), assemblyName, StringComparison.Ordinal))
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0)
            {
                Debug.LogWarning($"[热更构建] 默认热更程序集未安装（已跳过）：{assemblyName}");
                return;
            }
            if (matches.Length > 1)
                Debug.LogWarning($"[热更构建] 程序集名 {assemblyName} 对应多个 asmdef，默认采用按路径排序第一项：" +
                                 matches[0].path);
            profile._hotUpdateAssemblies.Add(matches[0].asset);
        }
    }
}
