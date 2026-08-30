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
    /// OnDestroy 反注册并 Dispose 底层实现。<br/>
    /// <b>实现：</b>组合而非继承底层（同 <c>MonoPoolUtility</c> 模式），全部成员转发给内部 <see cref="StorageUtility"/>。
    /// </remarks>
    public sealed class MonoStorageUtility : MonoUtilityBase, IStorageUtility
    {
        [SerializeField, Tooltip("存储根目录名（persistentDataPath 下的子目录）。多个存储实例用不同目录名隔离；改名等同丢弃旧数据。")]
        private string _rootFolder = "storage";

        private StorageUtility _impl;

#if UNITY_EDITOR
        /// <summary>原生 Inspector 展示的实际存储根路径。</summary>
        internal string EditorStorageRoot => Path.Combine(Application.persistentDataPath, _rootFolder ?? string.Empty);
#endif

        protected override void Awake()
        {
            // Inspector 漏配 fail-fast（规则 #22）：根目录名是持久契约，静默兜底成默认值会让数据悄悄写去别处。
            if (string.IsNullOrWhiteSpace(_rootFolder))
                throw new InvalidOperationException($"[MonoStorageUtility] '{name}' 的存储根目录名未配置。");

            // 先建实现再注册（base.Awake 会把本组件登记进容器，登记后即可能被解析调用）。
            _impl = new StorageUtility(new FileStorageProvider(Path.Combine(Application.persistentDataPath, _rootFolder)));
            base.Awake();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // 释放 Bag + 从容器反注册
            _impl?.Dispose();
            _impl = null;
        }

        // ── IStorageUtility 转发到底层 StorageUtility ──────────────────────────
        public UniTask Save<T>(string key, T data, CancellationToken ct = default) where T : class => _impl.Save(key, data, ct);

        public UniTask<T> Load<T>(string key, CancellationToken ct = default) where T : class => _impl.Load<T>(key, ct);

        public bool Exists(string key) => _impl.Exists(key);

        public UniTask Delete(string key, CancellationToken ct = default) => _impl.Delete(key, ct);

        public UniTask<IReadOnlyList<string>> ListKeys(string prefix = null, CancellationToken ct = default) => _impl.ListKeys(prefix, ct);
    }
}
