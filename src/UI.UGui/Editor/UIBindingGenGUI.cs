using System;
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
        private const float CompactBreakpoint = 320f;

        public static void Draw(UIBindingData data, string assetPath, bool editable)
        {
            var profile = UICodeGenProfile.Resolve();
            string ns = UIBindingUtil.ResolveNamespace(assetPath, data, profile);
            string className = UIBindingUtil.ResolveClassName(assetPath, data, profile);
            string outDir = UIBindingUtil.ResolveOutputDir(assetPath, data, profile);
            string genDir = UIBindingUtil.ResolveGeneratedDir(assetPath, data, profile);
            string logicPath = outDir + "/" + className + ".cs";
            string nodesPath = genDir + "/" + className + ".nodes.g.cs";

            GUILayout.Label("生成目标（留空 = 继承目录配置 / 全工程默认）", SectionHeading);
            EditorGUILayout.HelpBox("覆盖项留空即继承；占位符：{PrefabName} / {DirectoryName} / {ParentDirectoryName}", MessageType.None);
            using (new EditorGUI.DisabledScope(!editable))
            {
                OverrideField(data, "命名空间", false, data.NamespaceOverride, x => data.NamespaceOverride = x);
                OverrideField(data, "逻辑目录", true, data.OutputDirOverride, x => data.OutputDirOverride = x);
                OverrideField(data, "生成目录", true, data.GeneratedDirOverride, x => data.GeneratedDirOverride = x);
                OverrideField(data, "文件名/类名", false, data.FileNameOverride, x => data.FileNameOverride = x);
            }

            // 生效：按本 prefab 路径展开占位符后的最终结果（只读），看清覆盖 / 继承落地成什么。
            EditorGUILayout.Space(2);
            GUILayout.Label("生效（解析后）", SectionHeading);
            using (new EditorGUI.DisabledScope(true))
            {
                ReadOnlyField("命名空间", ns);
                ReadOnlyField("文件名/类名", className);
                ReadOnlyField("逻辑目录", outDir);
                ReadOnlyField("生成目录", genDir);
            }

            EditorGUILayout.Space(2);
            GUILayout.Label("脚本（点击定位；尚未生成则为 None）", SectionHeading);
            ScriptField("逻辑", logicPath);
            ScriptField("绑定", nodesPath);

            EditorGUILayout.Space(4);
            // 运行时不显示生成按钮——Play 下本就不能生成（会触发重编译），藏掉避免误点。
            if (EditorApplication.isPlaying)
                GUILayout.Label("（运行中——停止后可生成代码）", EditorStyles.wordWrappedMiniLabel);
            else
                using (new EditorGUI.DisabledScope(data.Entries.Count == 0))
                    if (GUILayout.Button("生成 / 重新生成代码"))
                        UIBindingCodeGenerator.GenerateAndLog(assetPath, data, profile);
        }

        // 覆盖行：普通文本框（目录字段带「…」选择器），留空 = 继承（实际生效值在下方「生效（解析后）」只读区看）。
        // 改动经 Commit（Undo + 标脏 + prefab 编辑模式同步场景脏标记）。
        private static void OverrideField(UIBindingData data, string label, bool isDir, string current, Action<string> set)
        {
            string cur = current ?? string.Empty;
            string v = DrawLabeledField(label, isDir, cur);
            if (v != cur) Commit(data, set, v);
        }

        /// <summary>画一行「标签 + 文本框（目录字段附 … 选择器）」，返回（可能变更后的）值。Profile 根字段直接用，与勾选式覆盖的标签列宽一致，两套 Inspector 视觉统一。</summary>
        internal static string DrawLabeledField(string label, bool isDir, string value)
        {
            bool compact = UseCompactLayout(EditorGUIUtility.currentViewWidth);
            if (compact)
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            if (!compact)
                EditorGUILayout.LabelField(label, GUILayout.Width(96f));
            string v = EditorGUILayout.TextField(value);
            if (isDir && GUILayout.Button(new GUIContent("…", "选择目录（自动转工程相对路径）"), GUILayout.Width(24)))
            {
                string picked = PickProjectDir(string.IsNullOrEmpty(v) ? value : v);
                if (picked != null) { v = picked; GUI.FocusControl(null); }
            }
            EditorGUILayout.EndHorizontal();
            return v;
        }

        private static void ReadOnlyField(string label, string value)
        {
            if (UseCompactLayout(EditorGUIUtility.currentViewWidth))
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
                EditorGUILayout.TextField(value);
                return;
            }

            EditorGUILayout.TextField(label, value);
        }

        internal static bool UseCompactLayout(float viewWidth) => viewWidth < CompactBreakpoint;

        // 选目录对话框：选完把绝对路径转工程相对（Assets/…）返回；取消 / 选到工程外 → 返回 null（外部不改值）。
        internal static string PickProjectDir(string current)
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
            if (UseCompactLayout(EditorGUIUtility.currentViewWidth))
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
                EditorGUILayout.ObjectField(script, typeof(MonoScript), false);
            }
            else
            {
                EditorGUILayout.ObjectField(label, script, typeof(MonoScript), false);
            }
        }

        private static GUIStyle _sectionHeading;
        private static GUIStyle SectionHeading => _sectionHeading ??= new GUIStyle(EditorStyles.boldLabel)
        {
            wordWrap = true,
        };
    }
}
