using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// SSFramework 人工工具的总入口。这里按使用意图解释每个工具，再导航到 Module 自己的工作台；
    /// 不在中央窗口复制构建或生成 Implementation，以保持可选 Module 的删除边界。
    /// </summary>
    public sealed class FrameworkToolsWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.Tools, priority = 0)]
        public static void Open() => GetWindow<FrameworkToolsWindow>("SSFramework 工具中心").Show();

        private Vector2 _scroll;

        private void OnEnable()
        {
            minSize = new Vector2(300, 360);
            FrameworkToolRegistry.Changed += Repaint;
        }

        private void OnDisable() => FrameworkToolRegistry.Changed -= Repaint;

        private void OnGUI()
        {
            bool compact = position.width < 460f;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("SSFramework · 工具中心", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "菜单只负责带你来到正确的工作台。会生成、构建、清理或修改设置的动作，都在窗口里先说明用途、前置条件与影响，再由你确认点击。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("配置中心", compact ? GUILayout.ExpandWidth(true) : GUILayout.Width(110)))
                    OpenMenu(FrameworkMenuPaths.Configuration);
                if (GUILayout.Button(new GUIContent("刷新显示", "重新绘制当前已登记的 Module 卡片。"),
                        compact ? GUILayout.ExpandWidth(true) : GUILayout.Width(110)))
                    Repaint();
                if (!compact) GUILayout.FlexibleSpace();
            }

            var tools = FrameworkToolRegistry.Snapshot();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (FrameworkToolCategory category in Enum.GetValues(typeof(FrameworkToolCategory)))
            {
                var section = tools.Where(tool => tool.Category == category).ToArray();
                if (section.Length == 0) continue;
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(CategoryLabel(category), EditorStyles.boldLabel);
                foreach (var tool in section) DrawTool(tool, compact);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawTool(FrameworkToolDescriptor tool, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(tool.Title, EditorStyles.boldLabel);
                GUILayout.Label(tool.Summary, EditorStyles.wordWrappedMiniLabel);
                if (compact)
                {
                    if (GUILayout.Button("打开工作台")) OpenMenu(tool.MenuPath);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("打开工作台", GUILayout.Width(110))) OpenMenu(tool.MenuPath);
                    }
                }
            }
        }

        private static string CategoryLabel(FrameworkToolCategory category) => category switch
        {
            FrameworkToolCategory.BuildAndRelease => "构建与发布",
            FrameworkToolCategory.CodeGeneration => "代码与数据生成",
            FrameworkToolCategory.Diagnostics => "诊断与分析",
            FrameworkToolCategory.Development => "开发辅助",
            _ => category.ToString(),
        };

        private static void OpenMenu(string menuPath)
        {
            if (!EditorApplication.ExecuteMenuItem(menuPath))
                FrameworkEditorFeedback.Warn(
                    "工具未安装或入口已失效",
                    $"影响：没有打开窗口。\n原因：找不到菜单 {menuPath}。\n下一步：确认对应可选 Module 已安装，并检查 Console 编译错误。");
        }
    }
}
