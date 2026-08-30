using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// Framework Editor 模块自动创建项目级配置时使用的中性目录。已有配置始终按类型发现，不要求迁移到这里；
    /// 该目录避免可复用工具猜测项目是否采用 <c>Assets/Game</c>、<c>Assets/Scripts</c> 等业务布局，
    /// 并在自动创建前收口默认目录的物理边界与固定目标占用检查。
    /// </summary>
    public static class FrameworkProjectSettingsLocation
    {
        /// <summary>新建 Framework 项目配置的默认目录；不位于可抽包的 Framework 源码目录内。</summary>
        public const string Directory = "Assets/Settings/SSFramework";

        /// <summary>确保默认目录逐级存在并返回其 Assets 相对路径。</summary>
        public static string EnsureDirectory()
        {
            ValidateDirectoryPath();
            const string root = "Assets/Settings";
            EnsureChildFolder("Assets", "Settings", root);
            EnsureChildFolder(root, "SSFramework", Directory);
            ValidateDirectoryPath();
            return Directory;
        }

        /// <summary>
        /// 检查自动创建 Profile 的固定目标：路径空闲时返回 <c>null</c>，已有同类型资产时返回并复用；
        /// 被其它类型、目录或无法加载的文件占用时抛出异常，确保 <see cref="AssetDatabase.CreateAsset(UnityEngine.Object,string)"/>
        /// 不会覆盖现有项目资产。本方法不负责全工程发现、默认初始化或实际创建。
        /// </summary>
        public static T GetExistingProfileOrThrow<T>(string assetPath) where T : ScriptableObject
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                throw new ArgumentException("Profile 创建路径不能为空。", nameof(assetPath));

            if (!FrameworkProjectPath.TryResolveAssetsFile(
                    assetPath, ".asset", out string normalizedPath, out string absolutePath, out string pathError))
                throw new InvalidOperationException("Profile 创建路径无效：" + pathError);

            UnityEngine.Object occupant = AssetDatabase.LoadMainAssetAtPath(normalizedPath);
            if (occupant is T existing) return existing;

            bool pathExists = AssetDatabase.AssetPathExists(normalizedPath) ||
                              File.Exists(absolutePath) ||
                              System.IO.Directory.Exists(absolutePath);
            if (!pathExists) return null;

            throw new InvalidOperationException(
                $"无法创建 {typeof(T).Name}：目标路径已被" +
                (occupant != null ? $" {occupant.GetType().Name} " : "无法加载的文件或资产") +
                $"占用：{normalizedPath}。" +
                "为保护现有资产，本次不会覆盖；请移动或重命名占用项后重试。");
        }

        private static void EnsureChildFolder(string parent, string name, string expectedPath)
        {
            if (AssetDatabase.IsValidFolder(expectedPath)) return;
            if (AssetDatabase.LoadMainAssetAtPath(expectedPath) != null)
                throw new InvalidOperationException($"无法创建 Framework 项目配置目录：{expectedPath} 已被同名文件占用。");

            string guid = AssetDatabase.CreateFolder(parent, name);
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(expectedPath))
                throw new InvalidOperationException($"无法创建 Framework 项目配置目录：{expectedPath}。请检查 Assets 写权限与同名资产。");
        }

        private static void ValidateDirectoryPath()
        {
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    Directory, out _, out _, out string error))
                throw new InvalidOperationException("Framework 项目配置目录不安全：" + error);
        }
    }
}
