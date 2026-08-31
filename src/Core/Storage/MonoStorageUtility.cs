using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Storage
{
    /// <summary>
    /// 本地存储工具的 Mono 版：挂在 Context 子节点上，可在 Inspector 配存储根目录名，
    /// 底层复用纯 C# 的 <see cref="StorageUtility"/>（同一套逻辑）。随宿主 GameObject / 场景销毁自动释放。
    /// </summary>
    /// <remarks>
    /// <b>何时用：</b>想在 Inspector 看到 / 配置存储目录、或希望存储随某个 Context 节点生命周期释放时用本类；
    /// 全局共享、纯代码配置用 <c>builder.RegisterOwnedUtility(new StorageUtility())</c>（纯 C# 路径）。<br/>
    /// <b>生命周期：</b>继承 <see cref="MonoUtilityBase"/>——Awake 注册为 <see cref="IStorageUtility"/>（+ <c>IUtility</c>），
    /// OnDestroy 反注册并 Dispose 底层实现；已释放内核会被保留为终态守卫，使销毁前借出的
    /// <see cref="IStorageUtility"/> 旧引用仍抛 <see cref="ObjectDisposedException"/>，而不是退化成空引用异常。<br/>
    /// <b>实现：</b>组合而非继承底层（同 <c>MonoPoolUtility</c> 模式），全部成员转发给内部 <see cref="StorageUtility"/>。
    /// </remarks>
    public sealed class MonoStorageUtility : MonoUtilityBase, IStorageUtility
    {
        [SerializeField, Tooltip("persistentDataPath 下的单个可移植目录名（1-255 个英文字母、数字、-、_）。多个实例用不同名称隔离；改名等同切换数据集。")]
        private string _rootFolder = "storage";

        private StorageUtility _impl;

#if UNITY_EDITOR
        /// <summary>原生 Inspector 展示的实际存储根路径。</summary>
        internal string EditorStorageRoot
        {
            get
            {
                try
                {
                    return StorageRootPath.Resolve(Application.persistentDataPath, _rootFolder);
                }
                catch (ArgumentException e)
                {
                    // Inspector 每次重绘都会读取诊断值，配置错误应显示在原处，不能让异常持续打断 Inspector。
                    return $"（配置无效：{e.Message}）";
                }
            }
        }
#endif

        protected override void Awake()
        {
            string storageRoot;
            try
            {
                storageRoot = StorageRootPath.Resolve(Application.persistentDataPath, _rootFolder);
            }
            catch (ArgumentException e)
            {
                // 根目录名是持久契约，不能 Trim、兜底或自动修正，否则会把数据悄悄切到另一套目录。
                throw new InvalidOperationException($"[MonoStorageUtility] '{name}' 的存储根目录配置无效：{e.Message}", e);
            }

            // 先建实现再注册（base.Awake 会把本组件登记进容器，登记后即可能被解析调用）。
            _impl = new StorageUtility(new FileStorageProvider(storageRoot));
            base.Awake();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // 释放 Bag + 从容器反注册
            _impl?.Dispose();
            // 不置 null：销毁前借出的 IStorageUtility 引用仍可能短暂存活，必须由已 Dispose 的
            // 内核保留 fail-fast 终态，不能把明确的 ObjectDisposedException 降级成 NRE。
        }

        // ── IStorageUtility 转发到底层 StorageUtility ──────────────────────────
        public UniTask Save<T>(string key, T data, CancellationToken ct = default) where T : class => _impl.Save(key, data, ct);

        public UniTask<T> Load<T>(string key, CancellationToken ct = default) where T : class => _impl.Load<T>(key, ct);

        public bool Exists(string key) => _impl.Exists(key);

        public UniTask Delete(string key, CancellationToken ct = default) => _impl.Delete(key, ct);

        public UniTask<IReadOnlyList<string>> ListKeys(string prefix = null, CancellationToken ct = default) => _impl.ListKeys(prefix, ct);
    }

    /// <summary>
    /// 将 Inspector 中的根目录名解析成 persistentDataPath 的直接子目录。
    /// 严格单段 + ASCII 白名单保证同一序列化配置在 Windows、macOS、Linux 与移动平台上含义一致。
    /// </summary>
    internal static class StorageRootPath
    {
        private const int MaxFolderNameLength = 255;

        internal static string Resolve(string persistentDataPath, string rootFolderName)
        {
            if (string.IsNullOrWhiteSpace(persistentDataPath) || !Path.IsPathFullyQualified(persistentDataPath))
                throw new ArgumentException("persistentDataPath 必须是非空、完全限定的绝对路径。", nameof(persistentDataPath));
            if (string.IsNullOrWhiteSpace(rootFolderName))
                throw new ArgumentException("存储根目录名不能为空。", nameof(rootFolderName));
            if (rootFolderName.Length > MaxFolderNameLength)
                throw new ArgumentException(
                    $"存储根目录名不能超过 {MaxFolderNameLength} 个 ASCII 字符。",
                    nameof(rootFolderName));

            for (int i = 0; i < rootFolderName.Length; i++)
            {
                char c = rootFolderName[i];
                bool legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!legal)
                    throw new ArgumentException(
                        $"存储根目录名 '{rootFolderName}' 含非法字符 '{c}'——仅允许英文字母、数字、-、_，不能传路径。",
                        nameof(rootFolderName));
            }

            if (IsReservedWindowsDeviceName(rootFolderName))
                throw new ArgumentException(
                    $"存储根目录名 '{rootFolderName}' 是 Windows 保留设备名，请更换名称。",
                    nameof(rootFolderName));

            DirectoryInfo baseDirectory;
            try
            {
                baseDirectory = new DirectoryInfo(Path.GetFullPath(persistentDataPath));
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                throw new ArgumentException("persistentDataPath 无法规范化为可用的绝对路径。", nameof(persistentDataPath), e);
            }

            DirectoryInfo candidate;
            try
            {
                candidate = new DirectoryInfo(Path.GetFullPath(Path.Combine(baseDirectory.FullName, rootFolderName)));
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                throw new ArgumentException("存储根目录名无法解析为可移植的直接子目录。", nameof(rootFolderName), e);
            }
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (candidate.Parent == null ||
                !string.Equals(candidate.Parent.FullName, baseDirectory.FullName, comparison))
            {
                throw new ArgumentException("存储根目录必须是 persistentDataPath 的直接子目录。", nameof(rootFolderName));
            }

            return candidate.FullName;
        }

        private static bool IsReservedWindowsDeviceName(string value)
        {
            if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                return true;

            if (value.Length != 4) return false;
            bool numberedDevice = value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                  value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase);
            return numberedDevice && value[3] >= '1' && value[3] <= '9';
        }
    }
}
