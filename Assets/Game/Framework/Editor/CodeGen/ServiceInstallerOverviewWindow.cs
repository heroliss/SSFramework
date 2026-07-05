using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「服务安装器配置总览」窗口：把工程内所有 <see cref="ServiceInstallerProfile"/> 集中成卡片——每份列出
    /// 各条目的「扫描目录 → 输出安装器 / 命名空间」，并提供「生成这份 / 点名定位资产 / 定位生成的 .g.cs」。
    /// 多份并存（demo + 正式项目）时一眼看清各份落点、按份操作；顶部「生成全部」等同菜单「生成服务安装器代码」。
    /// </summary>
    public sealed class ServiceInstallerOverviewWindow : EditorWindow
    {
        public static void Open() => GetWindow<ServiceInstallerOverviewWindow>("服务安装器总览").Show();

        private Vector2 _scroll;

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("服务安装器 · 配置总览", EditorStyles.boldLabel);
                if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
            }
            EditorGUILayout.HelpBox(
                "每份 profile 若干条目：扫描目录下的纯 C# 服务类 → 生成一个静态安装器 (.g.cs)；" +
                "装进哪个 Context 由业务在该 Context 的 InstallBindings 里手写一行调用决定。\n" +
                "demo 与正式项目可各一份并存、生成互不干扰；demo 那份的输出落在 Demo/ 隔离岛，正式打包时随 demo 一并排除。",
                MessageType.Info);

            var profiles = ServiceInstallerProfile.ResolveAll();
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"共 {profiles.Count} 份", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(playing || profiles.Count == 0))
                    if (GUILayout.Button("生成全部", GUILayout.Width(90)))
                        ServiceInstallerMenu.GenerateProfiles(profiles);
            }
            if (playing)
                EditorGUILayout.LabelField("（运行中——停止后可生成）", EditorStyles.miniLabel);
            if (profiles.Count == 0)
                EditorGUILayout.LabelField("（无——经 Assets/Create/SSFramework/服务安装器配置 创建）", EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var profile in profiles)
                DrawCard(profile, playing);
            EditorGUILayout.EndScrollView();
        }

        // 一份 profile 一张卡片：资产名（点击定位选中）+ 逐条目「扫描目录 → 输出 / 命名空间」+ 生成按钮。
        private static void DrawCard(ServiceInstallerProfile profile, bool playing)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string path = AssetDatabase.GetAssetPath(profile);
                if (GUILayout.Button(new GUIContent(Path.GetFileName(path), path + "\n点击定位并选中"), EditorStyles.objectField))
                {
                    EditorGUIUtility.PingObject(profile);
                    Selection.activeObject = profile;
                }

                if (profile.Installers == null || profile.Installers.Count == 0)
                    EditorGUILayout.LabelField("（没有任何安装器条目）", EditorStyles.miniLabel);
                else
                    foreach (var entry in profile.Installers)
                        DrawEntry(entry);

                using (new EditorGUI.DisabledScope(playing))
                    if (GUILayout.Button("生成这份"))
                        ServiceInstallerMenu.GenerateProfiles(new[] { profile });
            }
        }

        private static void DrawEntry(ServiceInstallerProfile.InstallerEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var folders = entry.ScanFolders == null
                    ? new List<string>()
                    : entry.ScanFolders.Where(f => f != null).Select(AssetDatabase.GetAssetPath).ToList();
                if (folders.Count == 0)
                    EditorGUILayout.LabelField("扫描目录", "（未配置）", EditorStyles.miniLabel);
                else
                    for (int i = 0; i < folders.Count; i++)
                        EditorGUILayout.LabelField(i == 0 ? "扫描目录" : " ", folders[i]);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("输出", entry.OutputPath);
                    // 生成过才有资产可定位；没生成过按钮不出现（而非置灰——避免误解为「点了会生成」）。
                    var generated = AssetDatabase.LoadAssetAtPath<MonoScript>(entry.OutputPath);
                    if (generated != null && GUILayout.Button("定位 .g.cs", GUILayout.Width(80)))
                        EditorGUIUtility.PingObject(generated);
                }
                EditorGUILayout.LabelField("命名空间", entry.Namespace);
            }
        }
    }
}
