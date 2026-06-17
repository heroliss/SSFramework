using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 目录级生成配置（编辑器资产）——按字段覆盖「命名空间 / 逻辑目录 / 生成目录 / 文件名(=类名)」四值，承接「按模块分目录 / 分命名空间」需求。
    /// </summary>
    /// <remarks>
    /// 解析按 <b>prefab 所在目录</b>（非生成代码目录）向上走目录树生效，逐字段取最近一个设了该字段的配置；
    /// 完整覆盖链见 <see cref="UIBindingUtil.ResolveNamespace"/>：<c>prefab 覆盖 → 本目录配置 → 父目录配置 → … → 全工程 UICodeGenProfile</c>。
    /// 每字段「空 = 继承上层」；约定类设置（组件优先级 / 别名 / 字段名模板 / 自动挂脚本）不在此、恒由全工程 <see cref="UICodeGenProfile"/> 提供。
    /// 一个目录放多个本配置时仅第一个生效（会警告）。
    /// </remarks>
    [InitializeOnLoad]
    [CreateAssetMenu(fileName = "UICodeGenDirConfig", menuName = "SSFramework/UI 绑定目录配置 (UI CodeGen Dir Config)")]
    public sealed class UICodeGenDirConfig : ScriptableObject
    {
        [Tooltip("覆盖本目录（及子目录）下 prefab 的生成命名空间。留空 = 继承父目录配置 / 全工程默认。")]
        [SerializeField] private string _namespaceOverride;

        [Tooltip("覆盖手写逻辑 <Name>.cs 的输出目录（工程相对 Assets 路径）。留空 = 继承。")]
        [SerializeField] private string _outputDirOverride;

        [Tooltip("覆盖生成的 <Name>.nodes.g.cs 的输出目录（工程相对 Assets 路径）。留空 = 继承。")]
        [SerializeField] private string _generatedDirOverride;

        [Tooltip("覆盖生成文件名 / 类名模板（含 {PrefabName} 等占位符，不含扩展名）。留空 = 继承。")]
        [SerializeField] private string _fileNameOverride;

        /// <summary>本配置设定的命名空间（trim 后；未设则 <c>null</c>）。</summary>
        public string NamespaceOrNull => Norm(_namespaceOverride);
        /// <summary>本配置设定的逻辑目录（trim + 去尾斜杠；未设则 <c>null</c>）。</summary>
        public string OutputDirOrNull => NormDir(_outputDirOverride);
        /// <summary>本配置设定的生成目录（trim + 去尾斜杠；未设则 <c>null</c>）。</summary>
        public string GeneratedDirOrNull => NormDir(_generatedDirOverride);
        /// <summary>本配置设定的文件名 / 类名模板（trim 后；未设则 <c>null</c>）。</summary>
        public string FileNameOrNull => Norm(_fileNameOverride);

        private static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string NormDir(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('/', '\\');

        // ───────────── 目录 → 配置映射（按 prefab 目录向上解析用） ─────────────
        // 缓存全工程的「目录 → 配置」映射：覆盖链解析会在生成面板每帧调用，逐次 FindAssets 是工程级扫描、拖慢面板。
        // 资产增删改经 projectChanged 置脏、下次访问重建；配置引用由 Unity fake-null 失效保护，不会指向已删资产。
        private static Dictionary<string, UICodeGenDirConfig> _byFolder;
        private static bool _dirty = true;

        static UICodeGenDirConfig()
        {
            EditorApplication.projectChanged += () => _dirty = true;
        }

        /// <summary>
        /// 按 <paramref name="prefabPath"/> 所在目录向上返回**最近优先**的目录配置链（含该目录自身的配置）。
        /// 覆盖链解析逐字段遍历本列表取首个设了该字段的配置。
        /// </summary>
        public static IReadOnlyList<UICodeGenDirConfig> ChainFor(string prefabPath)
        {
            var chain = new List<UICodeGenDirConfig>();
            if (string.IsNullOrEmpty(prefabPath)) return chain;

            EnsureMap();
            for (string dir = ParentFolder(prefabPath.Replace('\\', '/')); !string.IsNullOrEmpty(dir); dir = ParentFolder(dir))
            {
                if (_byFolder.TryGetValue(dir, out var cfg) && cfg != null) chain.Add(cfg);
                if (dir == "Assets") break;
            }
            return chain;
        }

        private static void EnsureMap()
        {
            if (!_dirty && _byFolder != null) return;

            _byFolder = new Dictionary<string, UICodeGenDirConfig>();
            var byFolderPaths = new Dictionary<string, List<string>>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(UICodeGenDirConfig)))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                string folder = ParentFolder(p);
                if (!byFolderPaths.TryGetValue(folder, out var list)) byFolderPaths[folder] = list = new List<string>();
                list.Add(p);
            }

            foreach (var kv in byFolderPaths)
            {
                if (kv.Value.Count > 1)
                    Debug.LogWarning($"[UI 绑定] 目录 {kv.Key} 下有多个 UICodeGenDirConfig，仅第一个生效，请删到只剩一个：\n  " +
                                     string.Join("\n  ", kv.Value));
                _byFolder[kv.Key] = AssetDatabase.LoadAssetAtPath<UICodeGenDirConfig>(kv.Value[0]);
            }
            _dirty = false;
        }

        // 取工程相对路径的父目录（去掉最后一段）；已到 "Assets" 之上返回空串，作为 walk-up 终止条件。
        private static string ParentFolder(string path)
        {
            int i = path.LastIndexOf('/');
            return i <= 0 ? string.Empty : path.Substring(0, i);
        }
    }
}
