using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>目录级代码生成配置的 Unity 原生 Inspector，明确展示继承来源与逐字段生效结果。</summary>
    [CustomEditor(typeof(UICodeGenDirConfig))]
    public sealed class UICodeGenDirConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            UICodeGenEditorGUI.DrawScript(serializedObject);

            var config = (UICodeGenDirConfig)target;
            bool hasProfile = UICodeGenProfile.TryResolve(out UICodeGenProfile profile);
            UnityEngine.Object parent = UIBindingUtil.DirConfigParent(config, profile);

            EditorGUILayout.HelpBox(
                "配置按 prefab 所在目录向上继承；每个字段可独立覆盖。留空表示继续使用最近的父配置或全工程默认值。",
                MessageType.Info);
            DrawParent(parent);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("覆盖项（留空 = 继承）", EditorStyles.boldLabel);
            UICodeGenEditorGUI.DrawProperty(serializedObject.FindProperty("_namespaceOverride"), "命名空间");
            UICodeGenEditorGUI.DrawFolderProperty(serializedObject.FindProperty("_outputDirOverride"), "逻辑目录");
            UICodeGenEditorGUI.DrawFolderProperty(serializedObject.FindProperty("_generatedDirOverride"), "生成目录");
            UICodeGenEditorGUI.DrawProperty(serializedObject.FindProperty("_fileNameOverride"), "文件名 / 类名");
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("生效预览", EditorStyles.boldLabel);
            if (!hasProfile)
            {
                EditorGUILayout.HelpBox(
                    "尚未创建全工程 UI CodeGen Profile。仅查看目录配置不会写项目；创建后才能补齐未覆盖字段的继承预览。",
                    MessageType.Warning);
                if (GUILayout.Button("创建全工程配置"))
                {
                    UICodeGenProfile created = UICodeGenProfile.Resolve();
                    Selection.activeObject = created;
                    EditorGUIUtility.PingObject(created);
                    GUIUtility.ExitGUI();
                }
                return;
            }
            EditorGUILayout.HelpBox("占位符会在处理具体 prefab 时展开；这里显示模板及其来源。", MessageType.None);
            DrawEffective("命名空间", config.EffectiveLine(UIBindingUtil.GenTargetField.Namespace, profile));
            DrawEffective("逻辑目录", config.EffectiveLine(UIBindingUtil.GenTargetField.OutputDir, profile));
            DrawEffective("生成目录", config.EffectiveLine(UIBindingUtil.GenTargetField.GeneratedDir, profile));
            DrawEffective("文件名 / 类名", config.EffectiveLine(UIBindingUtil.GenTargetField.FileName, profile));
        }

        private static void DrawParent(UnityEngine.Object parent)
        {
            bool narrow = EditorGUIUtility.currentViewWidth < 300f;
            if (narrow)
                EditorGUILayout.LabelField(new GUIContent("父配置", "由目录层级推导，只读。"), EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(narrow ? GUIContent.none : new GUIContent("父配置", "由目录层级推导，只读。"), parent,
                        typeof(UnityEngine.Object), false);
                using (new EditorGUI.DisabledScope(parent == null))
                {
                    if (GUILayout.Button("定位", GUILayout.Width(48f)))
                    {
                        Selection.activeObject = parent;
                        EditorGUIUtility.PingObject(parent);
                    }
                }
            }
        }

        private static void DrawEffective(string label, string value)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(value) ? "（空）" : value, MessageType.None);
        }
    }
}
