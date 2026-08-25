using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UI 代码生成配置的 Unity 原生 Inspector。目录选择、分组与说明属于框架的基础可用性，
    /// 不应要求项目购买 Inspector 插件才能编辑。
    /// </summary>
    [CustomEditor(typeof(UICodeGenProfile))]
    public sealed class UICodeGenProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            UICodeGenEditorGUI.DrawScript(serializedObject);

            EditorGUILayout.HelpBox(
                "这里定义全工程默认生成规则。业务模块有不同目录或命名空间时，在对应 prefab 目录创建 UICodeGenDirConfig 就近覆盖。",
                MessageType.Info);

            EditorGUILayout.LabelField("生成目标（根默认）", EditorStyles.boldLabel);
            UICodeGenEditorGUI.DrawProperty(serializedObject.FindProperty("_namespaceRoot"), "命名空间");
            UICodeGenEditorGUI.DrawFolderProperty(serializedObject.FindProperty("_outputCodeDir"), "逻辑目录");
            UICodeGenEditorGUI.DrawFolderProperty(serializedObject.FindProperty("_generatedCodeDir"), "生成目录");
            UICodeGenEditorGUI.DrawProperty(serializedObject.FindProperty("_fileNameTemplate"), "文件名 / 类名");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("默认组件优先级", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_builtinComponentPriority"),
                new GUIContent("组件顺序"), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("字段命名", EditorStyles.boldLabel);
            UICodeGenEditorGUI.DrawProperty(serializedObject.FindProperty("_fieldNameTemplate"), "字段名模板");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_omitComponentTokenWhenContained"),
                new GUIContent("名称已包含组件时省略"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_componentAliases"),
                new GUIContent("组件别名"), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("脚本自动挂载", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_autoAssignWindowScript"),
                new GUIContent("自动挂窗口脚本"));

            serializedObject.ApplyModifiedProperties();
        }
    }

    internal static class UICodeGenEditorGUI
    {
        internal static void DrawScript(SerializedObject serializedObject)
        {
            var script = serializedObject.FindProperty("m_Script");
            if (script == null) return;
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(script);
        }

        internal static void DrawProperty(SerializedProperty property, string label)
        {
            if (property == null) return;
            if (EditorGUIUtility.currentViewWidth < 300f)
            {
                EditorGUILayout.LabelField(new GUIContent(label, property.tooltip), EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(property, GUIContent.none, true);
                return;
            }
            EditorGUILayout.PropertyField(property, new GUIContent(label, property.tooltip), true);
        }

        internal static void DrawFolderProperty(SerializedProperty property, string label)
        {
            if (property == null) return;
            bool narrow = EditorGUIUtility.currentViewWidth < 300f;
            if (narrow)
                EditorGUILayout.LabelField(new GUIContent(label, property.tooltip), EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(property,
                    narrow ? GUIContent.none : new GUIContent(label, property.tooltip));
                if (GUILayout.Button(new GUIContent("…", "选择 Assets 下的目录"), GUILayout.Width(30f)))
                {
                    string selected = EditorUtility.OpenFolderPanel(
                        $"选择{label}", ResolveStartFolder(property.stringValue), string.Empty);
                    if (!string.IsNullOrEmpty(selected) && TryToAssetPath(selected, out string assetPath))
                    {
                        property.stringValue = assetPath;
                        property.serializedObject.ApplyModifiedProperties();
                    }
                }
            }
        }

        private static string ResolveStartFolder(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && configured.StartsWith("Assets", StringComparison.Ordinal))
            {
                try
                {
                    string full = Path.GetFullPath(Path.Combine(ProjectRoot, configured));
                    if (Directory.Exists(full) && IsInsideAssets(full)) return full;
                }
                catch (Exception)
                {
                    // 手填路径可能包含当前平台不接受的字符；目录选择器稳定回退到 Assets 根。
                }
            }
            return Application.dataPath;
        }

        internal static bool TryToAssetPath(string selectedFolder, out string assetPath)
        {
            assetPath = string.Empty;
            string selected;
            try
            {
                selected = Path.GetFullPath(selectedFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                Game.Framework.Editor.FrameworkEditorFeedback.Warn(
                    "UI 代码生成目录未修改",
                    "所选目录不是当前平台可识别的有效路径。代码生成配置保持原值。");
                return false;
            }
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(selected, assets, PathComparison))
            {
                assetPath = "Assets";
                return true;
            }

            string prefix = assets + Path.DirectorySeparatorChar;
            if (!selected.StartsWith(prefix, PathComparison))
            {
                Game.Framework.Editor.FrameworkEditorFeedback.Warn(
                    "UI 代码生成目录未修改",
                    "所选目录不在当前项目的 Assets 下。代码生成配置保持原值，请重新选择项目内目录。");
                return false;
            }

            assetPath = "Assets/" + selected.Substring(prefix.Length).Replace('\\', '/');
            return true;
        }

        private static bool IsInsideAssets(string path)
        {
            string selected = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string assets = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(selected, assets, PathComparison) ||
                   selected.StartsWith(assets + Path.DirectorySeparatorChar, PathComparison);
        }

        private static StringComparison PathComparison => Application.platform == RuntimePlatform.WindowsEditor
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
    }
}
