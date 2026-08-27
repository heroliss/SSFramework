using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>配置驱动场景导航与 Boot 启动策略的人工工作台。</summary>
    public sealed class SceneShortcutWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.SceneShortcuts, priority = 61)]
        public static void Open() => GetWindow<SceneShortcutWindow>("SSFramework 场景快捷入口").Show();

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(320, 380);

        private void OnGUI()
        {
            bool compact = position.width < 460f;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("场景快捷入口", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "这里维护工具设置；真正的场景项仍直接出现在 SSFramework/场景，因为它们只是导航。替换打开会走 Unity 原生保存确认，Play 中切换会先明确询问是否退出。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (SceneShortcutProfile.Find() is not { } profile)
            {
                EditorGUILayout.HelpBox(
                    "尚无场景快捷入口配置。创建时会导入 Build Settings 中已启用的场景作为初始候选，但不会自动开启 Boot 启动。",
                    MessageType.Warning);
                if (GUILayout.Button("创建默认场景配置")) SceneShortcutMenu.SelectProfile();
                EditorGUILayout.EndScrollView();
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("配置与菜单", EditorStyles.boldLabel);
                GUILayout.Label($"{profile.Entries.Count} 条配置 · {AssetDatabase.GetAssetPath(profile)}",
                    EditorStyles.wordWrappedMiniLabel);
                if (compact)
                {
                    if (GUILayout.Button("定位并编辑配置")) SceneShortcutMenu.SelectProfile();
                    if (GUILayout.Button("刷新动态场景菜单")) SceneShortcutMenu.Refresh();
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("定位并编辑配置")) SceneShortcutMenu.SelectProfile();
                        if (GUILayout.Button("刷新动态场景菜单")) SceneShortcutMenu.Refresh();
                    }
                }
                GUILayout.Label("修改 Entries 后需刷新；脚本域重载也会自动重建。空场景槽位会被忽略，重名会按父目录稳定消歧。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Play 起始场景", EditorStyles.boldLabel);
                GUILayout.Label(
                    profile.BootScene == null
                        ? "尚未配置 Boot Scene；开启后也不会生效。"
                        : $"Boot Scene：{AssetDatabase.GetAssetPath(profile.BootScene)}",
                    EditorStyles.wordWrappedMiniLabel);
                bool enabled = EditorGUILayout.ToggleLeft(
                    new GUIContent("从 Boot 场景启动 Play", "设置 EditorSceneManager.playModeStartScene；关闭后恢复 Unity 默认从当前场景启动。"),
                    profile.PlayFromBootScene);
                if (enabled != profile.PlayFromBootScene) SceneShortcutMenu.SetPlayFromBoot(enabled);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
