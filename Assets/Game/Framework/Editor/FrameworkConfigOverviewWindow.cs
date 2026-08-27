using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「框架配置中心」hub：把框架各模块的配置 profile 资产聚合到一页——
    /// 每类配置一节，列出找到的资产（点路径定位选中）、标注单份 / 多份语义并做健康检查（单例类找到多份黄条警告），
    /// 并提供跳转到模块专用工作台的按钮。解决「配置资产散落各目录、不知道有哪些 / 在哪」的问题；
    /// 生成 / 构建等操作不在这里做，仍在各 Module 的工作台里。
    /// </summary>
    /// <remarks>
    /// 各 Editor Module 通过 <see cref="FrameworkConfigRegistry"/> 登记自己拥有的配置类型、数量语义与工作台；
    /// 本窗口不编译期引用可选 Module，也不维护程序集限定类型名。删除 Module 后注册自然消失，新增 Profile 只改所属 Module。
    /// </remarks>
    public sealed class FrameworkConfigOverviewWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.Configuration, priority = 1)]
        public static void Open() => GetWindow<FrameworkConfigOverviewWindow>("SSFramework 配置中心").Show();

        private Vector2 _scroll;

        private void OnEnable()
        {
            minSize = new Vector2(280, 320);
            FrameworkConfigRegistry.Changed += Repaint;
        }

        private void OnDisable() => FrameworkConfigRegistry.Changed -= Repaint;

        private void OnGUI()
        {
            bool compact = position.width < 380f;
            EditorGUILayout.Space(4);
            if (compact)
            {
                EditorGUILayout.LabelField("框架配置 · 全模块总览", EditorStyles.boldLabel);
                if (GUILayout.Button("刷新")) Repaint();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("框架配置 · 全模块总览", EditorStyles.boldLabel);
                    if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
                }
            }
            EditorGUILayout.HelpBox(
                "框架各模块配置 profile 的一页清单：点路径定位选中资产；单例类找到多份会警告。\n" +
                "生成 / 构建等操作在各自工作台里，这里只回答「有哪些配置、在哪、健康与否」。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var configurations = FrameworkConfigRegistry.Snapshot();
            if (configurations.Count == 0)
                EditorGUILayout.HelpBox(
                    "当前没有已登记的配置 Module。若工程本应包含配置工具，请先检查 Console 编译错误。",
                    MessageType.Warning);
            foreach (var section in configurations)
                DrawSection(section, compact);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawSection(FrameworkConfigDescriptor section, bool compact)
        {
            var paths = FindPaths(section.ProfileType);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (compact)
                {
                    EditorGUILayout.LabelField($"{section.Title} — {paths.Count} 份", EditorStyles.boldLabel);
                    DrawJumpButton(section, expand: true);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"{section.Title} — {paths.Count} 份", EditorStyles.boldLabel);
                        DrawJumpButton(section, expand: false);
                    }
                }
                GUILayout.Label(section.Note, EditorStyles.wordWrappedMiniLabel);

                if (section.Singleton && paths.Count > 1)
                    EditorGUILayout.HelpBox("找到多份，仅第一份生效——请删到只剩一份。", MessageType.Warning);

                if (paths.Count == 0)
                    EditorGUILayout.LabelField("（未找到）", EditorStyles.miniLabel);
                foreach (string path in paths)
                    DrawAssetRow(path);

                if (section.SecondaryProfileType != null)
                {
                    var subPaths = FindPaths(section.SecondaryProfileType);
                    EditorGUILayout.LabelField($"{section.SecondaryLabel}（{subPaths.Count}）", EditorStyles.miniBoldLabel);
                    foreach (string path in subPaths)
                        DrawAssetRow(path);
                }
            }
            EditorGUILayout.Space(4);
        }

        private static void DrawJumpButton(FrameworkConfigDescriptor section, bool expand)
        {
            bool clicked = expand
                ? GUI.Button(EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight), section.MenuLabel)
                : GUILayout.Button(section.MenuLabel, GUILayout.Width(90));
            if (clicked && !EditorApplication.ExecuteMenuItem(section.MenuPath))
                FrameworkEditorFeedback.Warn(
                    "配置工作台入口失效",
                    $"影响：没有打开“{section.Title}”工作台。\n原因：找不到菜单 {section.MenuPath}。\n" +
                    "下一步：确认所属可选 Module 已安装，并检查 Console 编译错误。");
        }

        private static List<string> FindPaths(Type profileType) =>
            AssetDatabase.FindAssets("t:" + profileType.Name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => AssetDatabase.LoadAssetAtPath(path, profileType) != null)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        private static void DrawAssetRow(string path)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            if (GUI.Button(rect, new GUIContent(path, path + "\n点击定位并选中"), EditorStyles.objectField))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
        }
    }
}
