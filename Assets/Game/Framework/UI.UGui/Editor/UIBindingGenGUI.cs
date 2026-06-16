using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UI 绑定的「生成」面板块：本 prefab 生成目标覆盖（命名空间 / 逻辑目录 / 生成目录，留空 = 用全工程 Profile 默认）、
    /// 解析出的脚本引用（可点定位）、以及生成按钮。Root 弹窗与 <see cref="UIBindingData"/> Inspector 共用，口径一致。
    /// </summary>
    internal static class UIBindingGenGUI
    {
        public static void Draw(UIBindingData data, string assetPath, bool editable)
        {
            var profile = UICodeGenProfile.Resolve();
            string className = UIBindingUtil.SanitizeIdentifier(Path.GetFileNameWithoutExtension(assetPath));
            string outDir = UIBindingUtil.ResolveOutputDir(data, profile);
            string genDir = UIBindingUtil.ResolveGeneratedDir(data, profile);
            string logicPath = outDir + "/" + className + ".cs";
            string nodesPath = genDir + "/" + className + ".nodes.g.cs";

            EditorGUILayout.LabelField("生成目标（留空 = 用全工程 Profile 默认）", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!editable))
            {
                OverrideField(data, "命名空间", profile.NamespaceRoot, () => data.NamespaceOverride, v => data.NamespaceOverride = v);
                OverrideField(data, "逻辑目录", profile.OutputCodeDir, () => data.OutputDirOverride, v => data.OutputDirOverride = v);
                OverrideField(data, "生成目录", profile.GeneratedCodeDir, () => data.GeneratedDirOverride, v => data.GeneratedDirOverride = v);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("脚本（点击定位；尚未生成则为 None）", EditorStyles.boldLabel);
            ScriptField("逻辑", logicPath);
            ScriptField("绑定", nodesPath);

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(data.Entries.Count == 0))
                if (GUILayout.Button("生成 / 重新生成代码"))
                {
                    if (EditorApplication.isPlayingOrWillChangePlaymode)
                        EditorUtility.DisplayDialog("UI 绑定", "Play 模式下不能生成代码（会触发重编译），先停止运行。", "好");
                    else
                        UIBindingCodeGenerator.GenerateAndLog(assetPath, data, profile);
                }
        }

        private static void OverrideField(UIBindingData data, string label, string fallback, Func<string> get, Action<string> set)
        {
            EditorGUI.BeginChangeCheck();
            string value = EditorGUILayout.TextField(new GUIContent(label, $"默认：{fallback}"), get());
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(data, "改 UI 生成目标");
                set(value);
                EditorUtility.SetDirty(data);
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
            }
        }

        // 解析出的脚本引用：可点定位 / 双击打开。编辑被忽略（每帧按路径重解析），相当于只读引用。
        private static void ScriptField(string label, string path)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            EditorGUILayout.ObjectField(label, script, typeof(MonoScript), false);
        }
    }
}
