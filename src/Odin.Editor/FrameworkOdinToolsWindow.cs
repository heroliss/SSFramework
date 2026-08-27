using Game.Framework.Editor;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Odin.Editor
{
    /// <summary>解释 Odin 可选 Adapter 边界，并提供显式的内存映射恢复入口。</summary>
    public sealed class FrameworkOdinToolsWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.OdinAdapter, priority = 62)]
        public static void Open() => GetWindow<FrameworkOdinToolsWindow>("SSFramework Odin 适配").Show();

        [InitializeOnLoadMethod]
        private static void RegisterTool() => FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
            "odin-adapter", FrameworkToolCategory.Development, 30,
            "Odin Inspector 适配", "检查可选 Odin Adapter 的职责与恢复方式；删除本 Editor Module 后自动回退原生 Inspector。",
            FrameworkMenuPaths.OdinAdapter));

        private void OnEnable() => minSize = new Vector2(320, 340);

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Odin Inspector 可选适配", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Odin 不是运行时 Core 的必需依赖。本 Module 只在 Editor 中把 Odin 字段绘制与 Framework 运行时诊断组合起来；删除 Game.Framework.Odin.Editor 后，组件数据和 Player 行为都不变。",
                MessageType.Info);

            InspectorConfig config = InspectorConfig.Instance;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("当前状态", EditorStyles.boldLabel);
                if (config == null)
                    EditorGUILayout.HelpBox("没有取得 Odin InspectorConfig，无法应用 Adapter。", MessageType.Warning);
                else
                {
                    GUILayout.Label(
                        config.EnableOdinInInspector
                            ? "Odin Inspector 已启用；Adapter 会尊重 Odin 的程序集分类与逐类型覆盖。"
                            : "Odin Inspector 当前被全局关闭；Framework 会使用原生 fallback Inspector。",
                        EditorStyles.wordWrappedMiniLabel);
                    string path = AssetDatabase.GetAssetPath(config);
                    GUILayout.Label("配置：" + (string.IsNullOrEmpty(path) ? "（内置或未落盘）" : path),
                        EditorStyles.wordWrappedMiniLabel);
                    if (!string.IsNullOrEmpty(path) && GUILayout.Button("定位 Odin 配置"))
                    {
                        Selection.activeObject = config;
                        EditorGUIUtility.PingObject(config);
                    }
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("恢复 Editor 映射", EditorStyles.boldLabel);
                GUILayout.Label(
                    "域重载和 Odin 配置保存后通常会自动重应用。只有 Inspector 显示与设置不一致时才需要手动执行；它只重建内存映射，不修改序列化资产。",
                    EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(config == null))
                    if (GUILayout.Button("重新应用 Odin Adapter", GUILayout.Height(28)))
                        FrameworkOdinEditorRegistration.RegisterWithFeedback();
            }
        }
    }
}
