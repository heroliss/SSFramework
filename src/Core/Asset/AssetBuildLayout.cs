#if UNITY_EDITOR
using System.IO;
using UnityEngine;
#endif

namespace Game.Framework
{
    /// <summary>
    /// 构建相关目录的**单一真源**：把分散的构建目录统一收在 <c>项目根/AssetBuild/</c> 下，名字只在这里定义一处。
    /// 编辑器构建工具（<c>Game.Framework.Build.Editor</c>）和运行时 provider 都从这里取名，避免各处写死、漂移。
    ///
    /// <para><b>这些目录都是编辑器 / 构建期概念</b>（全 gitignore），真机上不存在；故路径辅助方法用 <c>#if UNITY_EDITOR</c> 包裹。</para>
    ///
    /// <para><b>后端无关 vs 后端能力：</b><see cref="BundlesDir"/>（原始产物）/ <see cref="DeployDir"/>（平铺发布）是
    /// 后端无关的「构建流」目录——任何资源库都有「产物」和「待发布」两个阶段。而 <see cref="DownloadedDir"/> 是
    /// <b>后端能力</b>：把运行时下载缓存重定向到工程目录，只有支持该能力的后端能用（当前 YooAsset 经 SandboxFileSystem 的
    /// packageRoot 重载支持；Unity Addressables 等用 Unity 缓存 / persistentDataPath，不重定向）。真机上各后端一律用
    /// persistentDataPath，所以这个重定向纯属编辑器开发便利。换后端时由各 provider 自行决定缓存落点（见 IAssetProvider 实现）。</para>
    /// </summary>
    public static class AssetBuildLayout
    {
        /// <summary>项目根下唯一的构建父目录名。</summary>
        public const string RootDirName = "AssetBuild";

        /// <summary>原始构建产物（平台/包/版本层级，保留最近 N 个版本）。后端无关概念。</summary>
        public const string BundlesDir = "Bundles";

        /// <summary>平铺成 <c>{包}/{文件}</c> 的当前发布产物：本地 python 伺服 + CI 上传都用它。后端无关概念。</summary>
        public const string DeployDir = "Deploy";

        /// <summary>
        /// 运行时下载缓存（编辑器期重定向到工程目录，便于查看）。**后端能力，非通用**：仅支持「缓存重定向」的后端用
        /// （当前 YooAsset）；其余后端 / 真机用 persistentDataPath。
        /// </summary>
        public const string DownloadedDir = "Downloaded";

#if UNITY_EDITOR
        /// <summary>项目根（<c>&lt;项目根&gt;/Assets</c> 的父目录）。仅编辑器 / 构建期有意义。</summary>
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        /// <summary><c>项目根/AssetBuild</c>。</summary>
        public static string Root => Path.Combine(ProjectRoot, RootDirName);

        /// <summary><c>项目根/AssetBuild/Bundles</c>。</summary>
        public static string BundlesRoot => Path.Combine(Root, BundlesDir);

        /// <summary><c>项目根/AssetBuild/Deploy</c>。</summary>
        public static string DeployRoot => Path.Combine(Root, DeployDir);

        /// <summary><c>项目根/AssetBuild/Downloaded</c>（仅 YooAsset 这类支持缓存重定向的后端在编辑器用）。</summary>
        public static string DownloadedRoot => Path.Combine(Root, DownloadedDir);
#endif
    }
}
