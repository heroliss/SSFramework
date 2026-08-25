using System;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// Framework Editor 模块自动创建项目级配置时使用的中性目录。已有配置始终按类型发现，不要求迁移到这里；
    /// 该目录只负责避免可复用工具猜测项目是否采用 <c>Assets/Game</c>、<c>Assets/Scripts</c> 等业务布局。
    /// </summary>
    public static class FrameworkProjectSettingsLocation
    {
        /// <summary>新建 Framework 项目配置的默认目录；不位于可抽包的 Framework 源码目录内。</summary>
        public const string Directory = "Assets/Settings/SSFramework";

        /// <summary>确保默认目录逐级存在并返回其 Assets 相对路径。</summary>
        public static string EnsureDirectory()
        {
            const string root = "Assets/Settings";
            EnsureChildFolder("Assets", "Settings", root);
            EnsureChildFolder(root, "SSFramework", Directory);
            return Directory;
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
    }
}
