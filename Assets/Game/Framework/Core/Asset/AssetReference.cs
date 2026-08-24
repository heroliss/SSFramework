using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// Inspector 可拖拽的资源引用基类。
    /// GUID 只作为序列化定位信息保存，实际加载委托给 <see cref="IAssetUtility"/>。
    /// 自己持有一份 <see cref="IAssetHandle{T}"/>，跟随宿主生命周期由 bag 统一 Dispose。
    /// </summary>
    [Serializable]
    public abstract class AssetReferenceBase : IAssetReferenceBindable
    {
        [SerializeField, HideInInspector] private string _assetGUID = "";
        [SerializeField, HideInInspector] private string _packageName = "";

        [NonSerialized] internal IAssetUtility BoundUtility;
        [NonSerialized] internal CancellationToken HostToken;

        /// <summary>
        /// Inspector 保存的资源 GUID。
        /// 业务通常不直接使用；ScriptableObject 把引用当"资源地址载体"传递时才会读取。
        /// </summary>
        public string AssetGUID => _assetGUID;

        /// <summary>显式指定的资源包名；为空表示使用默认包。</summary>
        public string PackageName => string.IsNullOrEmpty(_packageName) ? null : _packageName;

        /// <summary>GUID 是否已配置（非空字符串）。注意此属性不验证 GUID 对应的资源是否真实存在。</summary>
        public bool HasGuid => !string.IsNullOrEmpty(_assetGUID);

        /// <summary>是否已经绑定资源加载器。MonoXxxBase 字段会在 Awake 时自动绑定。</summary>
        public bool IsBound => BoundUtility != null;

        /// <summary>
        /// 显式绑定资源加载器和宿主销毁信号。
        /// ScriptableObject 或纯 C# 对象没有 Mono 生命周期自动绑定时，需要由调用方指定资源归属。
        /// hostToken 触发时引用侧的加载等待会取消并拒绝晚到结果；provider 已经启动的共享物理操作可能继续到终态，
        /// 其无人接收的 handle 由 provider 释放。
        /// </summary>
        public void Bind(IAssetUtility utility, CancellationToken hostToken)
        {
            BoundUtility = utility;
            HostToken = hostToken;
        }

        internal IAssetUtility ResolveUtility()
        {
            if (BoundUtility != null) return BoundUtility;
            return ResolveFallbackUtility();
        }

        private IAssetUtility ResolveFallbackUtility()
        {
            // 兜底只服务于脱离 MonoXxxBase 的旧用法；正常路径应在 Awake 自动绑定加载器。
            if (GameContext.Main != null &&
                GameContext.Main.TryResolve(typeof(IAssetUtility), out var utility) &&
                utility is IAssetUtility assetUtility)
            {
                BoundUtility = assetUtility;
                return assetUtility;
            }

            Debug.LogError(
                "[AssetReference] No IAssetUtility bound. Use a MonoXxxBase field, call Bind(), or register IAssetUtility in GameContext.Main.");
            return null;
        }

        public abstract void Dispose();
    }

    /// <summary>
    /// 泛型资源引用。
    /// 提供 Inspector 拖拽体验，并自行持有 <see cref="IAssetHandle{T}"/>；Dispose 时释放底层 handle。
    /// 同一 ref 多次并发 Get 共享同一加载任务（避免一帧内重复创建 handle）。
    /// </summary>
    [Serializable]
    public class AssetReference<T> : AssetReferenceBase where T : UnityEngine.Object
    {
        [NonSerialized] private IAssetHandle<T> _handle;
        [NonSerialized] private UniTaskCompletionSource<IAssetHandle<T>> _loadTcs;
        [NonSerialized] private bool _releaseWhenLoaded;

        /// <summary>已加载的资源对象；未加载、加载失败或已 Dispose 时为 null。</summary>
        public T Asset => _handle != null && _handle.IsValid ? _handle.Asset : null;

        /// <summary>资源是否已就绪。</summary>
        public bool IsLoaded => Asset != null;

        /// <summary>是否正在加载中。</summary>
        public bool IsLoading { get; private set; }

        /// <summary>
        /// 非阻塞检查资源是否已就绪。
        /// 返回 true 表示已加载且可直接使用；false 表示尚未加载或加载失败。
        /// </summary>
        public bool TryGetAsset(out T asset)
        {
            asset = Asset;
            return asset != null;
        }

        /// <summary>获取资源对象，已加载时直接返回缓存，否则异步加载。</summary>
        public UniTask<T> Get() => Get(default);

        /// <summary>
        /// 获取资源对象，支持取消当前等待。
        /// 取消 token 只影响本次 await，不会中断同一引用的共享加载；hostToken 则结束这次引用级共享等待，
        /// 但同样不承诺强停 provider 已经启动的物理操作。
        /// </summary>
        public async UniTask<T> Get(CancellationToken ct)
        {
            if (IsLoaded) return Asset;

            if (!HasGuid)
            {
                Debug.LogWarning("[AssetReference] GUID is empty, assign asset in Inspector.");
                return null;
            }

            var utility = ResolveUtility();
            if (utility == null) return null;

            UniTaskCompletionSource<IAssetHandle<T>> tcs;
            if (!IsLoading || _loadTcs == null)
            {
                IsLoading = true;
                _releaseWhenLoaded = false;
                _loadTcs = new UniTaskCompletionSource<IAssetHandle<T>>();
                tcs = _loadTcs;
                RunLoad(utility, tcs).Forget();
            }
            else
            {
                // 同一个 AssetReference 的并发 Get 共享一次底层加载，避免一帧内重复创建 handle。
                tcs = _loadTcs;
            }

            var handle = await tcs.Task.AttachExternalCancellation(ct);
            return handle != null && handle.IsValid ? handle.Asset : null;
        }

        /// <summary>
        /// 释放当前持有的 handle。
        /// 加载中调用会标记"完成后立即释放"，避免缓存一个宿主已经不要的 handle。
        /// </summary>
        public void Unload()
        {
            if (IsLoading)
                _releaseWhenLoaded = true;

            _handle?.Dispose();
            _handle = null;
        }

        /// <summary>等价于 <see cref="Unload"/>，便于走 IDisposable 统一释放路径。</summary>
        public override void Dispose() => Unload();

        private async UniTask RunLoad(IAssetUtility utility, UniTaskCompletionSource<IAssetHandle<T>> tcs)
        {
            try
            {
                // HostToken 决定这个引用级 owner 何时停止等待并拒绝结果；provider 的物理 owner 若已启动仍会安全收尾。
                // 外部 await 用各自的 ct 影响单个等待者，不中断同一 AssetReference 的共享加载。
                var handle = await utility.LoadByGuid<T>(PackageName, AssetGUID, HostToken);
                if (_releaseWhenLoaded)
                {
                    // Unload 可能发生在加载完成前；完成后立刻释放，避免缓存一个宿主已经不要的 handle。
                    handle?.Dispose();
                    handle = null;
                    _releaseWhenLoaded = false;
                }

                _handle = handle;
                tcs.TrySetResult(handle);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                IsLoading = false;
                _loadTcs = null;
            }
        }
    }

    /// <summary>
    /// 资源引用列表，支持批量加载和统一释放。
    /// 列表本身也参与自动绑定，因此 View 中的 AssetReferenceList 字段会在 View 销毁时由 bag 逐项 Dispose。
    /// </summary>
    [Serializable]
    public class AssetReferenceList<T> : IAssetReferenceBindable where T : UnityEngine.Object
    {
        [SerializeField] private List<AssetReference<T>> _items = new();

        /// <summary>列表中配置的资源引用数量。</summary>
        public int Count => _items.Count;

        /// <summary>按序号访问单个资源引用，便于业务按配置顺序逐个加载。</summary>
        public AssetReference<T> this[int index] => _items[index];

        /// <summary>枚举底层引用列表；枚举得到的是 AssetReference，而不是已经加载出的资源。</summary>
        public List<AssetReference<T>>.Enumerator GetEnumerator() => _items.GetEnumerator();

        /// <summary>把列表内所有引用绑定到同一个资源加载器和宿主销毁信号。</summary>
        /// <remarks>列表项不做空守卫：Unity 序列化对 [Serializable] 类的列表项总是产出实例（不会留 null），且列表无公开可变入口——与 GetAll / UnloadAll / Dispose 一致。</remarks>
        public void Bind(IAssetUtility utility, CancellationToken hostToken)
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].Bind(utility, hostToken);
        }

        /// <summary>
        /// 并行加载所有资源并返回结果列表。
        /// 多个 AssetReference 会并发触发底层加载，受资源 provider 的下载/加载并发上限约束。
        /// 若某个资源加载失败，对应位置为 null（不影响其他项）。
        /// </summary>
        public UniTask<T[]> GetAll() => GetAll(default);

        /// <summary>支持取消的批量加载。</summary>
        public async UniTask<T[]> GetAll(CancellationToken ct)
        {
            if (_items.Count == 0) return Array.Empty<T>();

            var tasks = new UniTask<T>[_items.Count];
            for (int i = 0; i < _items.Count; i++)
                tasks[i] = _items[i].Get(ct);
            return await UniTask.WhenAll(tasks);
        }

        /// <summary>释放所有 handle。</summary>
        public void UnloadAll()
        {
            foreach (var item in _items) item.Unload();
        }

        public void Dispose()
        {
            foreach (var item in _items) item.Dispose();
        }
    }
}
