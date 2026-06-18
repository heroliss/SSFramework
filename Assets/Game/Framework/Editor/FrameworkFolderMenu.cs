#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// <c>SSFramework/打开目录/*</c>——在资源管理器打开各运行时 / 工程路径，方便排查缓存、首包、日志等。
    /// 纯开发期便利菜单（与 <c>资源构建/打开目录</c> 那种"构建产物目录"无关，这里是 Application.* 系统路径）。
    /// </summary>
    internal static class FrameworkFolderMenu
    {
        private const string Root = "SSFramework/打开目录/";

        [MenuItem(Root + "持久化数据 (PersistentData)", priority = 1)]
        private static void OpenPersistentDataPath() => OpenFolder(Application.persistentDataPath);

        [MenuItem(Root + "流式资源 (StreamingAssets)", priority = 2)]
        private static void OpenStreamingAssetsPath() => OpenFolder(Application.streamingAssetsPath);

        [MenuItem(Root + "临时缓存 (TemporaryCache)", priority = 3)]
        private static void OpenTemporaryCachePath() => OpenFolder(Application.temporaryCachePath);

        [MenuItem(Root + "工程 Assets 目录 (DataPath)", priority = 4)]
        private static void OpenDataPath() => OpenFolder(Application.dataPath);

        [MenuItem(Root + "工程根目录", priority = 5)]
        private static void OpenProjectRoot() => OpenFolder(Directory.GetParent(Application.dataPath)?.FullName);

        [MenuItem(Root + "控制台日志目录", priority = 6)]
        private static void OpenConsoleLogPath() => OpenFolder(Directory.GetParent(Application.consoleLogPath)?.FullName);

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[SSFramework] 目录路径为空。");
                return;
            }

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            EditorUtility.RevealInFinder(path);
        }
    }
}
#endif
