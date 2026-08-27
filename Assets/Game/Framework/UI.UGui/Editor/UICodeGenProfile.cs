using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UGUI 节点绑定生成配置（编辑器资产）——「生成的代码落哪、什么命名空间、默认绑哪个组件」的单一真源。
    /// <see cref="UIBindingCodeGenerator"/> 只读本资产；换项目 / 换目录改 Inspector 即可，不动代码。
    /// </summary>
    /// <remarks>业务程序集与目录无法由框架推导，新配置的三个根目标留空；现有项目配置按类型发现，不会被覆盖。</remarks>
    [InitializeOnLoad]
    [CreateAssetMenu(fileName = "UICodeGenProfile", menuName = "SSFramework/UI 绑定生成配置 (UI CodeGen Profile)")]
    public sealed class UICodeGenProfile : ScriptableObject
    {
        internal const string DefaultFileNameTemplate = "{PrefabName}";
        internal const string DefaultFieldNameTemplate = "{node}{Component}";
        internal const bool DefaultOmitComponentTokenWhenContained = true;

        private static readonly IReadOnlyList<string> DefaultBuiltinComponentPriority = CreateDefaultBuiltinComponentPriority();
        private static readonly IReadOnlyList<ComponentAlias> DefaultComponentAliases = CreateDefaultComponentAliases();

        [Tooltip("生成代码的命名空间。支持 {PrefabName} / {DirectoryName} / {ParentDirectoryName}；占位符值会被清洗成合法标识符段。")]
        [SerializeField] private string _namespaceRoot = "";

        [Tooltip("手写窗口逻辑 <Name>.cs 的输出目录（工程相对 Assets 路径，须在目标业务程序集范围内）。仅在文件不存在时创建一次。")]
        [SerializeField] private string _outputCodeDir = "";

        [Tooltip("生成的节点绑定 <Name>.nodes.g.cs 的输出目录（每次覆盖）。须与逻辑代码在同一业务程序集内（partial 才链得上）。")]
        [SerializeField] private string _generatedCodeDir = "";

        [Tooltip("生成的文件名（= 生成的 partial 类名），不含扩展名。默认 {PrefabName} = prefab 文件名。注意：[UIWindow(Asset=...)] 的加载地址恒 = prefab 文件名，不受本项影响。")]
        [SerializeField] private string _fileNameTemplate = DefaultFileNameTemplate;

        [Header("默认组件优先级（标记节点时用；自定义脚本恒高于本列表）")]
        [Tooltip("一个节点有多个内置组件时，按本列表从上到下取第一个命中的作为默认绑定组件（按 FullName 或简单名匹配）。\n" +
                 "节点上若有用户自定义脚本，则优先于本列表任何项。要绑多个组件在绑定编辑窗口里加。")]
        [SerializeField]
        private List<string> _builtinComponentPriority = CreateDefaultBuiltinComponentPriority();

        [Header("字段命名（未手动设字段名的条目走此规则）")]
        [Tooltip("字段名模板。占位符：{node} 节点名 / {Node} 首字母大写；{Component} 组件全名(如 Button) / {component} 首字母小写；" +
                 "{alias} 组件别名(见下，如 btn) / {Alias} 首字母大写 / {ALIAS} 全大写。\n" +
                 "例：{node}{Component} → AddButton（后缀）；{alias}_{node} → btn_Add（前缀分组，让节点字段在 this. 补全里聚成一片）。")]
        [SerializeField] private string _fieldNameTemplate = DefaultFieldNameTemplate;

        [Tooltip("勾上：节点名已包含组件名/别名(大小写不敏感)时，省略模板里的组件/别名占位符——避免 ScoreText(Text)→ScoreTextText。\n" +
                 "做前缀分组(如 btn_)想保留前缀就取消勾选。")]
        [SerializeField] private bool _omitComponentTokenWhenContained = DefaultOmitComponentTokenWhenContained;

        [Tooltip("组件类型 → 简写别名（供模板 {alias} 用）。按组件简单名或全名匹配；未配的组件 {alias} 退回组件全名。")]
        [SerializeField]
        private List<ComponentAlias> _componentAliases = CreateDefaultComponentAliases();

        [Header("脚本自动挂")]
        [Tooltip("勾上：窗口生成后自动把窗口脚本挂到 prefab 根——缺则挂上、类名改了则把旧脚本换成新的（变体则是把继承的基脚本换成变体脚本）。默认开。\n" +
                 "首次生成时窗口类还没编译完，会提示「编译后再次生成即自动挂」；类已存在时即自动挂。该 prefab 正在编辑模式打开时会跳过、避免冲突。")]
        [FormerlySerializedAs("_autoAssignVariantScript")]
        [SerializeField] private bool _autoAssignWindowScript = true;

        public string OutputCodeDir => _outputCodeDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string GeneratedCodeDir => _generatedCodeDir?.Trim().TrimEnd('/', '\\') ?? "";
        public string NamespaceRoot => _namespaceRoot?.Trim() ?? "";
        public string FileNameTemplate => string.IsNullOrWhiteSpace(_fileNameTemplate) ? DefaultFileNameTemplate : _fileNameTemplate.Trim();
        public IReadOnlyList<string> BuiltinComponentPriority => _builtinComponentPriority ?? DefaultBuiltinComponentPriority;
        // 兼容旧 Profile：历史上手动清空模板表示只用节点名；新建 Profile 的序列化默认仍是 {node}{Component}。
        public string FieldNameTemplate => string.IsNullOrWhiteSpace(_fieldNameTemplate) ? "{node}" : _fieldNameTemplate.Trim();
        public bool OmitComponentTokenWhenContained => _omitComponentTokenWhenContained;
        public IReadOnlyList<ComponentAlias> ComponentAliases => _componentAliases ?? DefaultComponentAliases;
        public bool AutoAssignWindowScript => _autoAssignWindowScript;

        // 解析结果缓存：TryResolve 被 GUI（弹窗生成面板）每帧调用，FindAssets 是工程级扫描，逐帧跑会拖慢（拖拽时尤其卡）。
        // 缓存资产引用即可——持的是 ScriptableObject 引用，字段编辑实时反映；资产增删改（新建第二个 Profile / 移动 / 删除）
        // 经 projectChanged 清缓存、下次重解析（与 UICodeGenDirConfig 的失效口径一致，避免「换了 Profile 资产但缓存仍指旧的」）。
        private static UICodeGenProfile _cached;
        private static bool _cacheInitialized;

        static UICodeGenProfile()
        {
            EditorApplication.projectChanged += () =>
            {
                _cached = null;
                _cacheInitialized = false;
            };
        }

        /// <summary>无副作用查找全工程唯一 Profile；用于 Inspector 预览等只读入口。</summary>
        internal static bool TryResolve(out UICodeGenProfile profile)
        {
            if (_cacheInitialized)
            {
                profile = _cached;
                return profile != null;
            }

            var paths = AssetDatabase.FindAssets("t:" + nameof(UICodeGenProfile))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, System.StringComparer.Ordinal)
                .ToArray();
            if (paths.Length > 0)
            {
                if (paths.Length > 1)
                {
                    Debug.LogWarning("[UI 绑定] 找到多个 UICodeGenProfile，仅第一个生效，请删到只剩一个：\n  " +
                                     string.Join("\n  ", paths));
                }
                _cached = AssetDatabase.LoadAssetAtPath<UICodeGenProfile>(paths[0]);
            }
            _cacheInitialized = true;
            profile = _cached;
            return profile != null;
        }

        /// <summary>
        /// 解析全工程唯一 Profile；没有时显式创建待配置的空资产。只应由用户点击“创建配置”等明确入口调用，
        /// 普通 Inspector 重绘、绑定编辑和代码生成动作使用 <see cref="TryResolve"/>；缺配置时应解释并引导，不得暗中写项目。
        /// </summary>
        public static UICodeGenProfile Resolve()
        {
            if (TryResolve(out UICodeGenProfile existing)) return existing;

            var profile = CreateInstance<UICodeGenProfile>();
            // 落在项目配置位（与构建 profile / 收集器设置同住），不在 Framework/ 内——这是项目配置实例，
            // 不该随框架进 UPM 包（ADR-0010/0011）；Resolve 按类型扫描定位，不认路径。
            string path = Game.Framework.Editor.FrameworkProjectSettingsLocation.EnsureDirectory() +
                          "/UICodeGenProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[UI 绑定] 未找到 UICodeGenProfile，已创建空配置：{path}。" +
                      "请先填写目标业务程序集的命名空间、逻辑目录与生成目录。");
            _cached = profile;
            _cacheInitialized = true;
            return profile;
        }

        internal static IReadOnlyList<string> BuiltinComponentPriorityOrDefault(UICodeGenProfile profile)
            => profile != null ? profile.BuiltinComponentPriority : DefaultBuiltinComponentPriority;

        internal static IReadOnlyList<ComponentAlias> ComponentAliasesOrDefault(UICodeGenProfile profile)
            => profile != null ? profile.ComponentAliases : DefaultComponentAliases;

        private static List<string> CreateDefaultBuiltinComponentPriority() => new()
        {
            "UnityEngine.UI.Button",
            "UnityEngine.UI.Toggle",
            "UnityEngine.UI.Slider",
            "UnityEngine.UI.Scrollbar",
            "UnityEngine.UI.Dropdown",
            "UnityEngine.UI.InputField",
            "UnityEngine.UI.ScrollRect",
            "TMPro.TMP_Dropdown",
            "TMPro.TMP_InputField",
            "TMPro.TextMeshProUGUI",
            "UnityEngine.UI.Text",
            "UnityEngine.UI.RawImage",
            "UnityEngine.UI.Image",
            "UnityEngine.CanvasGroup",
        };

        private static List<ComponentAlias> CreateDefaultComponentAliases() => new()
        {
            new("Button", "btn"), new("Text", "txt"), new("TextMeshProUGUI", "txt"), new("TMP_Text", "txt"),
            new("Image", "img"), new("RawImage", "rawImg"), new("Toggle", "tgl"), new("Slider", "sld"),
            new("Scrollbar", "scrollbar"), new("ScrollRect", "scroll"), new("Dropdown", "dd"), new("TMP_Dropdown", "dd"),
            new("InputField", "input"), new("TMP_InputField", "input"), new("CanvasGroup", "cg"),
        };
    }

    /// <summary>组件类型 → 字段名模板里 <c>{alias}</c> 用的简写映射的一条。</summary>
    [System.Serializable]
    public sealed class ComponentAlias
    {
        [Tooltip("组件简单名(如 Button)或全名(如 UnityEngine.UI.Button)。")]
        public string Component;
        [Tooltip("生成字段名里 {alias} 用的简写(如 btn)。")]
        public string Alias;

        public ComponentAlias() { }
        public ComponentAlias(string component, string alias) { Component = component; Alias = alias; }
    }
}
