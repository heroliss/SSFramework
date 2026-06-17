using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// <see cref="UICodeGenDirConfig"/> 的 Inspector：三值走勾选式覆盖——勾上覆盖、不勾显示置灰的继承值（来源 tooltip）。
    /// 与 <see cref="UIBindingGenGUI"/> 的 prefab 级覆盖共用同一绘制，口径一致。
    /// </summary>
    [CustomEditor(typeof(UICodeGenDirConfig))]
    public sealed class UICodeGenDirConfigEditor : UnityEditor.Editor
    {
        private readonly HashSet<string> _expanded = new();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var profile = UICodeGenProfile.Resolve();
            string selfPath = AssetDatabase.GetAssetPath(target);

            EditorGUILayout.HelpBox(
                "目录级生成配置：勾选项覆盖本目录（及子目录）下 prefab 的对应生成目标；不勾 = 继承父目录配置 / 全工程默认。\n" +
                "占位符：{PrefabName} / {DirectoryName} / {ParentDirectoryName}，按各 prefab 自己的路径解析。",
                MessageType.Info);

            DrawProp("命名空间", false, "_namespaceOverride", "ns", selfPath, profile, UIBindingUtil.GenTargetField.Namespace);
            DrawProp("逻辑目录", true, "_outputDirOverride", "out", selfPath, profile, UIBindingUtil.GenTargetField.OutputDir);
            DrawProp("生成目录", true, "_generatedDirOverride", "gen", selfPath, profile, UIBindingUtil.GenTargetField.GeneratedDir);
            DrawProp("文件名/类名", false, "_fileNameOverride", "file", selfPath, profile, UIBindingUtil.GenTargetField.FileName);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawProp(string label, bool isDir, string propName, string key, string selfPath,
            UICodeGenProfile profile, UIBindingUtil.GenTargetField field)
        {
            var prop = serializedObject.FindProperty(propName);
            UIBindingGenGUI.DrawCheckedOverride(label, isDir, prop.stringValue,
                v => { prop.stringValue = v; serializedObject.ApplyModifiedProperties(); }, // ApplyModifiedProperties 自带 Undo + 标脏
                _expanded, key,
                () => UIBindingUtil.ResolveInherited(field, selfPath, profile));
        }
    }
}
