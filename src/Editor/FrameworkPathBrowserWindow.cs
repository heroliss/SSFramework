using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>集中解释并打开 Unity 与 SSFramework 常用目录；打开动作不会为了“方便”暗中创建不存在的目录。</summary>
    public sealed class FrameworkPathBrowserWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.ProjectFolders, priority = 63)]
        public static void Open() => GetWindow<FrameworkPathBrowserWindow>("SSFramework 常用目录").Show();

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(300, 340);

        private void OnGUI()
        {
            bool compact = position.width < 460f;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("常用目录", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "先确认用途与实际路径再打开。不存在的运行时目录不会被工具自动创建，避免一次查看给项目制造空目录或 diff。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPath("工程根目录", Directory.GetParent(Application.dataPath)?.FullName,
                "Git、Packages、ProjectSettings 与 AssetBuild 等项目级内容所在位置。", compact);
            DrawPath("Assets", Application.dataPath,
                "Unity 导入的项目资产根目录。", compact);
            DrawPath("StreamingAssets", Application.streamingAssetsPath,
                "随 Player 原样发布的文件；目录可能尚未创建。", compact);
            DrawPath("持久化数据", Application.persistentDataPath,
                "当前平台与项目的运行时持久化数据；清理前应先确认内容。", compact);
            DrawPath("临时缓存", Application.temporaryCachePath,
                "操作系统可能回收的运行时缓存，不应存必须保留的数据。", compact);
            DrawPath("Editor 日志", Directory.GetParent(Application.consoleLogPath)?.FullName,
                "Unity Editor 日志目录；用于诊断编译、导入和崩溃。", compact);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawPath(string title, string path, string explanation, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                GUILayout.Label(explanation, EditorStyles.wordWrappedMiniLabel);
                GUILayout.Label(string.IsNullOrWhiteSpace(path) ? "（路径不可用）" : path,
                    EditorStyles.wordWrappedMiniLabel);
                bool exists = !string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path));
                using (new EditorGUI.DisabledScope(!exists))
                {
                    if (compact)
                    {
                        if (GUILayout.Button(exists ? "在资源管理器中打开" : "目录尚不存在")) Reveal(path);
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(exists ? "在资源管理器中打开" : "目录尚不存在", GUILayout.Width(150)))
                                Reveal(path);
                        }
                    }
                }
            }
        }

        private static void Reveal(string path)
        {
            try
            {
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception exception)
            {
                FrameworkEditorFeedback.Warn(
                    "打开目录失败",
                    $"影响：没有打开资源管理器。\n路径：{path}\n原因：{exception.Message}");
            }
        }
    }
}
