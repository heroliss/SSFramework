using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 「配置表生成总览」窗口：把工程内所有 <see cref="LubanConfigProfile"/> 集中成卡片——每套列出 luban.conf 源、目标、
    /// 代码 / 数据输出目录、命名空间，并提供「生成这套 / 打开各目录 / 点名定位资产」。多套并存（demo + 正式游戏）时
    /// 一眼看清各套落点、按套操作，省得到处翻文件夹；顶部「生成全部」等同菜单「1. 生成」。
    /// </summary>
    public sealed class LubanConfigOverviewWindow : EditorWindow
    {
        public static void Open() => GetWindow<LubanConfigOverviewWindow>("配置表生成总览").Show();

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(280, 320);

        private void OnGUI()
        {
            bool compact = position.width < 520f;
            EditorGUILayout.Space(4);
            if (position.width < 380f)
            {
                EditorGUILayout.LabelField("配置表生成 · 总览", EditorStyles.boldLabel);
                if (GUILayout.Button("刷新")) Repaint();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("配置表生成 · 总览", EditorStyles.boldLabel);
                    if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
                }
            }
            EditorGUILayout.HelpBox(
                "每套配置 = 一个 Luban Profile（各自的 luban.conf 源 + 输出目录 + 命名空间），互不干扰。\n" +
                "demo 与正式游戏可各一套并存；demo 那套的代码 / 数据落在 Demo/ 隔离岛（程序集带 UNITY_EDITOR 约束、数据在样例资源包），" +
                "正式打包时随 demo 一并排除。",
                MessageType.Info);

            var profiles = LubanConfigProfile.ResolveAll();
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (position.width < 380f)
            {
                EditorGUILayout.LabelField($"共 {profiles.Count} 套", EditorStyles.miniBoldLabel);
                using (new EditorGUI.DisabledScope(playing))
                    if (GUILayout.Button("生成全部"))
                        LubanBuildMenu.GenerateProfiles(profiles);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"共 {profiles.Count} 套", EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(playing))
                        if (GUILayout.Button("生成全部", GUILayout.Width(90)))
                            LubanBuildMenu.GenerateProfiles(profiles);
                }
            }
            if (playing)
                EditorGUILayout.LabelField("（运行中——停止后可生成）", EditorStyles.miniLabel);

            foreach (var profile in profiles)
                DrawCard(profile, playing, compact);
            EditorGUILayout.EndScrollView();
        }

        // 一套配置一张卡片：资产名（点击定位选中）+ 源 / 目标 / 输出 / 命名空间 + 响应式操作区。
        private static void DrawCard(LubanConfigProfile profile, bool playing, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string path = AssetDatabase.GetAssetPath(profile);
                var assetRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                if (GUI.Button(assetRect, new GUIContent(Path.GetFileName(path), path + "\n点击定位并选中"), EditorStyles.objectField))
                {
                    EditorGUIUtility.PingObject(profile);
                    Selection.activeObject = profile;
                }
                DrawValue("源 (luban.conf)", profile.ConfPath, compact);
                DrawValue("目标", $"{profile.Target} · {profile.CodeTarget} / {profile.DataTarget}", compact);
                DrawValue("代码输出", profile.OutputCodeDir, compact);
                DrawValue("数据输出", profile.OutputDataDir, compact);
                DrawValue("命名空间", profile.ManifestNamespace, compact);

                if (compact)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(playing))
                            if (GUILayout.Button("生成这套"))
                                LubanBuildMenu.GenerateProfiles(new[] { profile });
                        if (GUILayout.Button("源目录")) Reveal(Path.GetDirectoryName(profile.ConfPath));
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("代码目录")) Reveal(profile.OutputCodeDir);
                        if (GUILayout.Button("数据目录")) Reveal(profile.OutputDataDir);
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(playing))
                            if (GUILayout.Button("生成这套"))
                                LubanBuildMenu.GenerateProfiles(new[] { profile });
                        if (GUILayout.Button("源目录")) Reveal(Path.GetDirectoryName(profile.ConfPath));
                        if (GUILayout.Button("代码目录")) Reveal(profile.OutputCodeDir);
                        if (GUILayout.Button("数据目录")) Reveal(profile.OutputDataDir);
                    }
                }
            }
        }

        private static void DrawValue(string label, string value, bool compact)
        {
            value = string.IsNullOrEmpty(value) ? "（未配置）" : value;
            if (!compact)
            {
                EditorGUILayout.LabelField(label, value);
                return;
            }

            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            GUILayout.Label(value, EditorStyles.wordWrappedMiniLabel);
        }

        // 工程相对路径 → 绝对路径后在资源管理器定位；尚未生成（目录不存在）时只提示、不报错。
        private static void Reveal(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return;
            string full = Path.GetFullPath(projectRelative);
            if (Directory.Exists(full) || File.Exists(full)) EditorUtility.RevealInFinder(full);
            else Debug.LogWarning($"[配置表构建] 目录不存在（可能尚未生成）：{full}");
        }
    }
}
