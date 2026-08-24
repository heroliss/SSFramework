using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 「UI 生成配置总览」窗口：把散落在工程各处的生成配置集中显示——全工程根 <see cref="UICodeGenProfile"/> + 所有目录级
    /// <see cref="UICodeGenDirConfig"/>。每条列出它管哪个目录、覆盖了哪几项（及模板值，留空=继承），点击资产名定位选中。
    /// 多模块项目里一眼看清「谁在哪覆盖了什么」，省得到处翻文件夹找配置。
    /// </summary>
    public sealed class UICodeGenConfigOverviewWindow : EditorWindow
    {
        [MenuItem("SSFramework/UI 绑定/配置总览", priority = 21)]
        public static void Open() => GetWindow<UICodeGenConfigOverviewWindow>("UI 生成配置总览").Show();

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(280, 320);

        private void OnGUI()
        {
            bool compact = position.width < 520f;
            EditorGUILayout.Space(4);
            if (position.width < 380f)
            {
                EditorGUILayout.LabelField("UI 节点绑定 · 生成配置总览", EditorStyles.boldLabel);
                if (GUILayout.Button("刷新")) Repaint();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("UI 节点绑定 · 生成配置总览", EditorStyles.boldLabel);
                    if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
                }
            }
            EditorGUILayout.HelpBox(
                "覆盖链（就近优先）：prefab 覆盖 → 目录配置（由近到远）→ 全工程 Profile。每项独立覆盖，留空即继承上层。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            var profile = UICodeGenProfile.Resolve();
            EditorGUILayout.LabelField("全工程默认 (UICodeGenProfile)", EditorStyles.boldLabel);
            DrawRow(profile, AssetDatabase.GetAssetPath(profile),
                profile.NamespaceRoot, profile.OutputCodeDir, profile.GeneratedCodeDir, profile.FileNameTemplate,
                isProfile: true, compact: compact);

            EditorGUILayout.Space(8);
            var dirConfigs = AssetDatabase.FindAssets("t:" + nameof(UICodeGenDirConfig))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p)
                .Select(p => (path: p, cfg: AssetDatabase.LoadAssetAtPath<UICodeGenDirConfig>(p)))
                .Where(t => t.cfg != null)
                .ToList();

            EditorGUILayout.LabelField($"目录级配置（{dirConfigs.Count}）", EditorStyles.boldLabel);
            if (dirConfigs.Count == 0)
                EditorGUILayout.LabelField("（无——所有 prefab 都走全工程默认）", EditorStyles.miniLabel);
            foreach (var (path, cfg) in dirConfigs)
                DrawRow(cfg, path, cfg.NamespaceOrNull, cfg.OutputDirOrNull, cfg.GeneratedDirOrNull, cfg.FileNameOrNull,
                    isProfile: false, compact: compact);

            EditorGUILayout.EndScrollView();
        }

        // 一条配置：资产名（点击定位选中）+ 管辖目录（仅目录配置）+ 四项值（目录配置空项显示「继承」，Profile 全显示）。
        private static void DrawRow(
            Object asset,
            string path,
            string ns,
            string outDir,
            string genDir,
            string fileName,
            bool isProfile,
            bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var assetRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                if (GUI.Button(assetRect, new GUIContent(System.IO.Path.GetFileName(path), path + "\n点击定位并选中"), EditorStyles.objectField))
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                }
                if (!isProfile)
                {
                    string dir = (System.IO.Path.GetDirectoryName(path) ?? string.Empty).Replace('\\', '/');
                    Field("管辖目录", dir + "/**", isProfile: true, compact: compact);
                }
                Field("命名空间", ns, isProfile, compact);
                Field("逻辑目录", outDir, isProfile, compact);
                Field("生成目录", genDir, isProfile, compact);
                Field("文件名/类名", fileName, isProfile, compact);
            }
        }

        private static void Field(string label, string value, bool isProfile, bool compact)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (isProfile) return; // Profile 这四项不该为空
                value = "（继承）";
            }

            if (!compact)
            {
                EditorGUILayout.LabelField(label, value);
                return;
            }

            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            GUILayout.Label(value, EditorStyles.wordWrappedMiniLabel);
        }
    }
}
