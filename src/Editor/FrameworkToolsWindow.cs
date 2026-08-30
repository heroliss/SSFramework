using System;
using System.Collections.Generic;
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
        private GUIStyle _heroTitleStyle;
        private GUIStyle _heroSubtitleStyle;
        private GUIStyle _categoryStyle;
        private GUIStyle _cardTitleStyle;
        private GUIStyle _cardSummaryStyle;
        private bool _stylesForProSkin;

        private void OnEnable()
        {
            minSize = new Vector2(300, 360);
            FrameworkToolRegistry.Changed += Repaint;
        }

        private void OnDisable() => FrameworkToolRegistry.Changed -= Repaint;

        private void OnGUI()
        {
            EnsureStyles();
            bool compact = position.width < 460f;
            IReadOnlyList<FrameworkToolDescriptor> tools = FrameworkToolRegistry.Snapshot();
            DrawHero();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("打开配置中心", EditorStyles.toolbarButton,
                        compact ? GUILayout.ExpandWidth(true) : GUILayout.Width(128)))
                    OpenMenu(FrameworkMenuPaths.Configuration);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{tools.Count} 个工作台", EditorStyles.miniLabel);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (FrameworkToolCategory category in Enum.GetValues(typeof(FrameworkToolCategory)))
            {
                var section = tools.Where(tool => tool.Category == category).ToArray();
                if (section.Length == 0) continue;
                DrawCategoryHeader(CategoryLabel(category), section.Length, CategoryColor(category));
                foreach (var tool in section) DrawTool(tool, compact);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawHero()
        {
            const string subtitle =
                "按意图找到所属工作台。生成、构建、清理和设置修改会先在窗口中说明影响；这里本身只负责导航。";
            float contentWidth = Mathf.Max(120f, position.width - 28f);
            float subtitleHeight = Mathf.Max(
                30f,
                _heroSubtitleStyle.CalcHeight(new GUIContent(subtitle), contentWidth));
            float heroHeight = 47f + subtitleHeight;
            Rect rect = GUILayoutUtility.GetRect(0f, heroHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin
                ? new Color(0.12f, 0.145f, 0.18f, 1f)
                : new Color(0.91f, 0.94f, 0.98f, 1f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 4f, rect.height),
                FrameworkEditorVisuals.ActiveColor);

            var titleRect = new Rect(rect.x + 14f, rect.y + 9f, rect.width - 26f, 26f);
            GUI.Label(titleRect, "SSFramework · 工具中心", _heroTitleStyle);
            var subtitleRect = new Rect(
                rect.x + 14f, rect.y + 37f, rect.width - 26f, subtitleHeight);
            GUI.Label(subtitleRect, subtitle, _heroSubtitleStyle);
        }

        private void DrawCategoryHeader(string title, int count, Color color)
        {
            EditorGUILayout.Space(9);
            Rect rect = GUILayoutUtility.GetRect(0f, 24f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(new Rect(rect.x + 2f, rect.y + 4f, 3f, rect.height - 8f), color);
            GUI.Label(new Rect(rect.x + 11f, rect.y, rect.width - 70f, rect.height), title, _categoryStyle);
            GUI.Label(new Rect(rect.xMax - 56f, rect.y, 52f, rect.height), count + " 项", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawTool(FrameworkToolDescriptor tool, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (compact)
                {
                    EditorGUILayout.LabelField(tool.Title, _cardTitleStyle);
                    GUILayout.Label(tool.Summary, _cardSummaryStyle);
                    if (GUILayout.Button("打开工作台", GUILayout.MinHeight(25))) OpenMenu(tool.MenuPath);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(tool.Title, _cardTitleStyle);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("打开", GUILayout.Width(82), GUILayout.MinHeight(23)))
                            OpenMenu(tool.MenuPath);
                    }
                    GUILayout.Label(tool.Summary, _cardSummaryStyle);
                }
            }
        }

        private void EnsureStyles()
        {
            bool proSkin = EditorGUIUtility.isProSkin;
            if (_heroTitleStyle != null && _stylesForProSkin == proSkin) return;
            _stylesForProSkin = proSkin;
            _heroTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 20,
                alignment = TextAnchor.MiddleLeft,
            };
            _heroSubtitleStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 11,
                normal = { textColor = FrameworkEditorVisuals.MutedTextColor },
            };
            _categoryStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
            };
            _cardTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
            };
            _cardSummaryStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 11,
                normal = { textColor = FrameworkEditorVisuals.MutedTextColor },
                margin = new RectOffset(2, 2, 2, 4),
            };
        }

        private static Color CategoryColor(FrameworkToolCategory category) => category switch
        {
            FrameworkToolCategory.BuildAndRelease => FrameworkEditorVisuals.WarningColor,
            FrameworkToolCategory.CodeGeneration => new Color(0.56f, 0.43f, 0.86f, 1f),
            FrameworkToolCategory.Diagnostics => FrameworkEditorVisuals.ActiveColor,
            FrameworkToolCategory.Development => FrameworkEditorVisuals.HealthyColor,
            _ => FrameworkEditorVisuals.BorderColor,
        };

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
