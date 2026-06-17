using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UI 绑定的「生成」面板块：本 prefab 生成目标的勾选式覆盖（命名空间 / 逻辑目录 / 生成目录，不勾 = 继承目录配置 / 全工程默认）、
    /// 解析出的脚本引用（可点定位）、以及生成按钮。Root 弹窗与 <see cref="UIBindingData"/> Inspector 共用，口径一致。
    /// </summary>
    internal static class UIBindingGenGUI
    {
        // 勾选式覆盖的「展开」瞬态状态（按 资产路径|字段 记）。覆盖值本身存在 prefab 上，这里只持 UI 折叠态，
        // 让「勾上后清空文本」不会立刻塌缩输入框（仿 UIBindingDataEditor._expandedFieldName）。
        private static readonly HashSet<string> _expanded = new();

        public static void Draw(UIBindingData data, string assetPath, bool editable)
        {
            var profile = UICodeGenProfile.Resolve();
            string className = UIBindingUtil.ResolveClassName(assetPath, data, profile);
            string outDir = UIBindingUtil.ResolveOutputDir(assetPath, data, profile);
            string genDir = UIBindingUtil.ResolveGeneratedDir(assetPath, data, profile);
            string logicPath = outDir + "/" + className + ".cs";
            string nodesPath = genDir + "/" + className + ".nodes.g.cs";

            EditorGUILayout.LabelField("生成目标", EditorStyles.boldLabel);
            // 说明走 HelpBox（自动换行），避免长文本在窄 Inspector / 弹窗里溢出。
            EditorGUILayout.HelpBox("勾选 = 本 prefab 覆盖；不勾 = 继承目录配置 / 全工程默认。\n占位符：{PrefabName} / {DirectoryName} / {ParentDirectoryName}", MessageType.None);
            using (new EditorGUI.DisabledScope(!editable))
            {
                DrawCheckedOverride("命名空间", false, data.NamespaceOverride,
                    v => Commit(data, x => data.NamespaceOverride = x, v),
                    _expanded, assetPath + "|ns",
                    () => UIBindingUtil.ResolveInherited(UIBindingUtil.GenTargetField.Namespace, assetPath, profile));
                DrawCheckedOverride("逻辑目录", true, data.OutputDirOverride,
                    v => Commit(data, x => data.OutputDirOverride = x, v),
                    _expanded, assetPath + "|out",
                    () => UIBindingUtil.ResolveInherited(UIBindingUtil.GenTargetField.OutputDir, assetPath, profile));
                DrawCheckedOverride("生成目录", true, data.GeneratedDirOverride,
                    v => Commit(data, x => data.GeneratedDirOverride = x, v),
                    _expanded, assetPath + "|gen",
                    () => UIBindingUtil.ResolveInherited(UIBindingUtil.GenTargetField.GeneratedDir, assetPath, profile));
                DrawCheckedOverride("文件名/类名", false, data.FileNameOverride,
                    v => Commit(data, x => data.FileNameOverride = x, v),
                    _expanded, assetPath + "|file",
                    () => UIBindingUtil.ResolveInherited(UIBindingUtil.GenTargetField.FileName, assetPath, profile));
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("脚本（点击定位；尚未生成则为 None）", EditorStyles.boldLabel);
            ScriptField("逻辑", logicPath);
            ScriptField("绑定", nodesPath);

            EditorGUILayout.Space(4);
            // 运行时不显示生成按钮——Play 下本就不能生成（会触发重编译），藏掉避免误点。
            if (EditorApplication.isPlaying)
                EditorGUILayout.LabelField("（运行中——停止后可生成代码）", EditorStyles.miniLabel);
            else
                using (new EditorGUI.DisabledScope(data.Entries.Count == 0))
                    if (GUILayout.Button("生成 / 重新生成代码"))
                        UIBindingCodeGenerator.GenerateAndLog(assetPath, data, profile);
        }

        /// <summary>
        /// 勾选式覆盖行：勾选框 + 字段。勾上显示可编辑输入框（目录字段带「…」选目录），不勾显示置灰的继承值（tooltip 标来源）。
        /// 覆盖值的读写由 <paramref name="current"/> / <paramref name="commit"/> 注入（prefab 字段或 SerializedProperty 皆可），
        /// 继承值由 <paramref name="inherited"/> 提供。两处覆盖 UI（prefab 级 / 目录配置级）共用此绘制，口径一致。
        /// </summary>
        internal static void DrawCheckedOverride(string label, bool isDir, string current, Action<string> commit,
            HashSet<string> expandedKeys, string key, Func<(string value, string source)> inherited)
        {
            bool expanded = !string.IsNullOrEmpty(current) || expandedKeys.Contains(key);

            EditorGUILayout.BeginHorizontal();

            bool now = EditorGUILayout.ToggleLeft(label, expanded, GUILayout.Width(96f));
            if (now != expanded)
            {
                if (now)
                {
                    expandedKeys.Add(key);
                    // 勾上瞬间原值空 → 用当前继承值预填作编辑起点（也让本行保持「已展开」）。
                    if (string.IsNullOrEmpty(current)) { current = inherited().value; commit(current); }
                }
                else
                {
                    expandedKeys.Remove(key);
                    commit(string.Empty); // 取消勾选 = 清空 → 回到继承
                    current = string.Empty;
                }
                expanded = now;
                GUI.FocusControl(null);
            }

            if (expanded)
            {
                EditorGUI.BeginChangeCheck();
                string v = EditorGUILayout.TextField(current);
                bool changed = EditorGUI.EndChangeCheck();
                if (isDir && GUILayout.Button(new GUIContent("…", "选择目录（自动转工程相对路径）"), GUILayout.Width(24)))
                {
                    string picked = PickProjectDir(string.IsNullOrEmpty(current) ? inherited().value : current);
                    if (picked != null) { v = picked; changed = true; GUI.FocusControl(null); }
                }
                if (changed) commit(v);
            }
            else
            {
                var (value, source) = inherited();
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField(new GUIContent(string.Empty, $"{source}：{value}"), value);
            }

            EditorGUILayout.EndHorizontal();
        }

        // 选目录对话框：选完把绝对路径转工程相对（Assets/…）返回；取消 / 选到工程外 → 返回 null（外部不改值）。
        private static string PickProjectDir(string current)
        {
            string picked = EditorUtility.OpenFolderPanel("选择目录（须在工程 Assets 内）", ToAbsolute(current), string.Empty);
            if (string.IsNullOrEmpty(picked)) return null;

            string rel = FileUtil.GetProjectRelativePath(picked.Replace('\\', '/'));
            if (string.IsNullOrEmpty(rel) || !(rel == "Assets" || rel.StartsWith("Assets/")))
            {
                EditorUtility.DisplayDialog("UI 绑定", "请选择本工程 Assets 目录下的文件夹。", "好");
                return null;
            }
            return rel;
        }

        // 写回覆盖值：记撤销、标脏、若在 prefab 编辑模式同步标脏场景（随 Ctrl+S 落盘）。prefab 级覆盖用此提交。
        private static void Commit(UIBindingData data, Action<string> set, string value)
        {
            Undo.RecordObject(data, "改 UI 生成目标");
            set(value);
            EditorUtility.SetDirty(data);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
        }

        // 工程相对路径（Assets/…）→ 绝对路径，作为选目录对话框的起始位置；Editor 工作目录即工程根，GetFullPath 直接成立。
        private static string ToAbsolute(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return Application.dataPath;
            try { return Path.GetFullPath(projectRelative); }
            catch { return Application.dataPath; }
        }

        // 解析出的脚本引用：可点定位 / 双击打开。编辑被忽略（每帧按路径重解析），相当于只读引用。
        private static void ScriptField(string label, string path)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            EditorGUILayout.ObjectField(label, script, typeof(MonoScript), false);
        }
    }
}
