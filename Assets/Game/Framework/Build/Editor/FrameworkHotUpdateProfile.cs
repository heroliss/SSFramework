using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// 资产入库（放在 <c>Assets/Game/Framework/Build/</c>）；<see cref="Resolve"/> 找不到时按默认档位
    /// （内核 + Asset.Yoo 热更，见 ADR-0008 §2）自动创建，找到多个时取第一个并警告。
    /// 字段只读暴露：修改只经 Inspector，保证「资产 = 唯一真源」不被代码旁路改写。
    /// </summary>
    [CreateAssetMenu(fileName = "FrameworkHotUpdateProfile", menuName = "SSFramework/热更构建配置 (HotUpdate Profile)")]
    public sealed class FrameworkHotUpdateProfile : ScriptableObject
    {
        [Tooltip("热更程序集（asmdef 引用）。在列表 = 热更（运行时从代码包加载），不在 = AOT（随安装包固化）。\n" +
                 "铁律：谁被热更，引用它的程序集必须也在列表里（AOT 不能引用热更）——同步/构建时自动校验拦截。\n" +
                 "顺序随意：实际加载顺序按 asmdef 引用图拓扑排序自动生成，不需要人排。")]
        [SerializeField] private List<AssemblyDefinitionAsset> _hotUpdateAssemblies = new();

        [Tooltip("代码包名：装热更 DLL + AOT 补元数据 DLL + 清单的 YooAsset RawFile 包。\n" +
                 "归 Boot 引导器管，与业务资源包彻底分家（互不知晓、互不初始化）。")]
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

        /// <summary>
        /// 解析全工程唯一的热更 profile：先找已有资产（找到多个 → 取第一个并警告），
        /// 没有就按默认档位（内核 + Asset.Yoo 热更）自动建一个（落在 <c>Assets/Game/Framework/Build/</c>）。
        /// </summary>
        public static FrameworkHotUpdateProfile Resolve()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(FrameworkHotUpdateProfile));
            if (guids.Length > 0)
            {
                if (guids.Length > 1)
                {
                    var paths = guids.Select(AssetDatabase.GUIDToAssetPath);
                    Debug.LogWarning("[热更构建] 找到多个热更 profile，仅第一个生效，请删到只剩一个：\n  " +
                                     string.Join("\n  ", paths));
                }
                return AssetDatabase.LoadAssetAtPath<FrameworkHotUpdateProfile>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            var profile = CreateInstance<FrameworkHotUpdateProfile>();
            // 默认档位（ADR-0008 §2）：内核 + YooAsset 适配模块热更；业务程序集出现后由项目自行加进列表。
            TryAddDefault(profile, "Assets/Game/Framework/Scripts/Game.Framework.asmdef");
            TryAddDefault(profile, "Assets/Game/Framework/Asset.Yoo/Game.Framework.Asset.Yoo.asmdef");

            const string dir = "Assets/Game/Framework/Build";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets/Game/Framework", "Build");
            string path = dir + "/FrameworkHotUpdateProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[热更构建] 未找到热更 profile，已按默认档位（内核 + Asset.Yoo 热更）自动创建：{path}");
            return profile;
        }

        private static void TryAddDefault(FrameworkHotUpdateProfile profile, string asmdefPath)
        {
            var asmdef = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(asmdefPath);
            if (asmdef != null) profile._hotUpdateAssemblies.Add(asmdef);
            else Debug.LogWarning($"[热更构建] 默认热更程序集未找到（已跳过）：{asmdefPath}");
        }
    }
}
