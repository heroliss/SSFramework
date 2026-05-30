#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    internal static class FrameworkFolderMenu
    {
        [MenuItem("SSFramework/Open Folder/Persistent Data Path")]
        private static void OpenPersistentDataPath() => OpenFolder(Application.persistentDataPath);

        [MenuItem("SSFramework/Open Folder/Streaming Assets Path")]
        private static void OpenStreamingAssetsPath() => OpenFolder(Application.streamingAssetsPath);

        [MenuItem("SSFramework/Open Folder/Temporary Cache Path")]
        private static void OpenTemporaryCachePath() => OpenFolder(Application.temporaryCachePath);

        [MenuItem("SSFramework/Open Folder/Data Path")]
        private static void OpenDataPath() => OpenFolder(Application.dataPath);

        [MenuItem("SSFramework/Open Folder/Project Root")]
        private static void OpenProjectRoot() => OpenFolder(Directory.GetParent(Application.dataPath)?.FullName);

        [MenuItem("SSFramework/Open Folder/Console Log Path")]
        private static void OpenConsoleLogPath() => OpenFolder(Directory.GetParent(Application.consoleLogPath)?.FullName);

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[SSFramework] Folder path is empty.");
                return;
            }

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            EditorUtility.RevealInFinder(path);
        }
    }
}
#endif
