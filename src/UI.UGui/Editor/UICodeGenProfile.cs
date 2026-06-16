using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UGUI 节点绑定生成配置（编辑器资产）——「生成的代码落哪、什么命名空间、默认绑哪个组件」的单一真源。
    /// <see cref="UIBindingCodeGenerator"/> 只读本资产；换项目 / 换目录改 Inspector 即可，不动代码。
    /// </summary>
    /// <remarks>
    /// 仓库内默认指向 demo 路径（demo 是框架的活样板消费方，同 <c>LubanConfigProfile</c> 的约定）；真实项目改 Inspector。
    /// </remarks>
    [CreateAssetMenu(fileName = "UICodeGenProfile", menuName = "SSFramework/UI 绑定生成配置 (UI CodeGen Profile)")]
    public sealed class UICodeGenProfile : ScriptableObject
    {
        [Header("产物输出")]
        [Tooltip("手写窗口逻辑 <Name>.cs 的输出目录（相对工程根的 Assets 路径，须在目标业务程序集范围内，生成的窗口才能引用框架类型）。仅在文件不存在时创建一次。")]
        [SerializeField] private string _outputCodeDir = "Assets/Game/Framework/Demo/Scripts/Modules";

        [Tooltip("生成的节点绑定 <Name>.nodes.g.cs 的输出目录（每次覆盖）。单独放子目录把自动产物与手写代码分开，目录整洁；须与逻辑代码在同一业务程序集内（partial 才链得上）。")]
        [SerializeField] private string _generatedCodeDir = "Assets/Game/Framework/Demo/Scripts/Modules/Generated";

        [Tooltip("生成代码的命名空间。")]
        [SerializeField] private string _namespaceRoot = "Game.Framework.Demo.Modules";

        [Header("默认组件优先级（标记节点时用；自定义脚本恒高于本列表）")]
        [Tooltip("一个节点有多个内置组件时，按本列表从上到下取第一个命中的作为默认绑定组件（按 FullName 或简单名匹配）。\n" +
                 "节点上若有用户自定义脚本，则优先于本列表任何项。要绑多个组件在绑定编辑窗口里加。")]
        [SerializeField]
        private List<string> _builtinComponentPriority = new()
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

        public string OutputCodeDir => _outputCodeDir.Trim().TrimEnd('/', '\\');
        public string GeneratedCodeDir => _generatedCodeDir.Trim().TrimEnd('/', '\\');
        public string NamespaceRoot => _namespaceRoot.Trim();
        public IReadOnlyList<string> BuiltinComponentPriority => _builtinComponentPriority;

        /// <summary>解析全工程唯一的 profile：找已有（多个取第一个并警告），没有就按默认布局自动建一个。</summary>
        public static UICodeGenProfile Resolve()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(UICodeGenProfile));
            if (guids.Length > 0)
            {
                if (guids.Length > 1)
                {
                    var paths = guids.Select(AssetDatabase.GUIDToAssetPath);
                    Debug.LogWarning("[UI 绑定] 找到多个 UICodeGenProfile，仅第一个生效，请删到只剩一个：\n  " +
                                     string.Join("\n  ", paths));
                }
                return AssetDatabase.LoadAssetAtPath<UICodeGenProfile>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            var profile = CreateInstance<UICodeGenProfile>();
            const string path = "Assets/Game/Framework/UI.UGui/Editor/UICodeGenProfile.asset";
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[UI 绑定] 未找到 UICodeGenProfile，已按默认布局自动创建：{path}");
            return profile;
        }
    }
}
