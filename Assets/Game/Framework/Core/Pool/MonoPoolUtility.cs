using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// 对象池工具的 Mono 版：挂在 Context 子节点上，可在 Inspector 针对不同 prefab 配置容量与预热数，
    /// 底层复用纯 C# 的 <see cref="PoolUtility"/>（同一套逻辑）。随宿主 GameObject / 场景销毁自动清池。
    /// </summary>
    /// <remarks>
    /// <b>何时用：</b>想在 Inspector 配各 prefab 池参数（容量 / 预热）、或希望池随某个 Context 节点 / 场景生命周期销毁时用本类；
    /// 全局共享、纯代码配置的场景用 <c>builder.RegisterOwned(new PoolUtility(), …)</c>（纯 C# 路径，随 <c>Context.Dispose</c> 清理）。<br/>
    /// <b>生命周期：</b>继承 <see cref="MonoUtilityBase"/>——Awake 注册为 <see cref="IPoolUtility"/>（+ <c>IUtility</c>），
    /// OnDestroy 释放 Bag 并反注册，并 Dispose 底层 <see cref="PoolUtility"/>（销毁停放节点 + 空闲实例），天然跟随宿主 GameObject / 场景。<br/>
    /// <b>实现：</b>组合而非继承底层（<see cref="PoolUtility"/> 为 sealed、本类基类已是 <see cref="MonoUtilityBase"/>），
    /// <see cref="IPoolUtility"/> 全部成员转发给内部实例——两条路径共用同一套池逻辑。
    /// </remarks>
    public sealed class MonoPoolUtility : MonoUtilityBase, IPoolUtility
    {
        /// <summary>单个 prefab 池的 Inspector 配置。</summary>
        [Serializable]
        public struct GameObjectPoolConfig
        {
            [Tooltip("源 prefab")] public GameObject Prefab;
            [Tooltip("池容量上限；0 = 不限。超限的归还实例被 Destroy")] public int MaxSize;
            [Tooltip("启动时预热（分帧实例化）的数量；0 = 不预热")] public int PrewarmCount;
        }

        [SerializeField, Tooltip("各 prefab 池的容量 / 预热配置：启动时按此建池并分帧预热")]
        private List<GameObjectPoolConfig> _prefabPools = new();

        [SerializeField, Min(1), Tooltip("预热分帧节流：每帧实例化多少个")]
        private int _prewarmPerFrame = 1;

        // 底层共用同一套逻辑：组合而非继承（PoolUtility 为 sealed，本类已继承 MonoUtilityBase）。
        private readonly PoolUtility _impl = new();

        /// <summary>底层池实现。供框架诊断面板经 InternalsVisibleTo 读取池概要（<see cref="PoolUtility.GetPoolDiagnostics"/>）。</summary>
        internal PoolUtility Impl => _impl;

        protected override void Awake()
        {
            base.Awake(); // 注册为 IPoolUtility / IUtility + 注入 [Inject] + 绑 Bag
            ApplyInspectorConfig(this.GetCancellationTokenOnDestroy());
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // 释放 Bag + 从容器反注册
            _impl.Dispose();  // 销毁停放节点 + 空闲实例，池随本 GameObject 一起回收
        }

        // 按 Inspector 配置建池（定容量）并启动分帧预热。
        private void ApplyInspectorConfig(CancellationToken ct)
        {
            if (_prefabPools == null) return;
            foreach (var cfg in _prefabPools)
            {
                if (cfg.Prefab == null) continue;
                var pool = _impl.GetGameObjectPool(cfg.Prefab, cfg.MaxSize); // 首次配置定容量
                if (cfg.PrewarmCount > 0)
                    PrewarmAsync(pool, cfg.PrewarmCount, _prewarmPerFrame, ct).Forget();
            }
        }

        // 启动期分帧预热：fire-and-forget，但按 Assets/Game/AGENTS.md「状态、Command 与异步」
        // 用 try-catch 兜异常 + 随 OnDestroy 取消，不裸丢错误。
        private static async UniTaskVoid PrewarmAsync(IGameObjectPool pool, int count, int perFrame, CancellationToken ct)
        {
            try { await pool.Prewarm(count, perFrame, ct); }
            catch (OperationCanceledException) { /* 宿主销毁，正常取消 */ }
            catch (Exception e) { Log.Error("GameObject 池预热失败。", e, "MonoPoolUtility"); }
        }

        // ── IPoolUtility 转发到底层 PoolUtility ──────────────────────────────
        public IObjectPool<T> GetPool<T>() where T : class, new() => _impl.GetPool<T>();

        public IObjectPool<T> GetPool<T>(Func<T> factory, Action<T> onRent = null, Action<T> onReturn = null, int maxSize = 0)
            where T : class => _impl.GetPool(factory, onRent, onReturn, maxSize);

        public T Rent<T>() where T : class, new() => _impl.Rent<T>();

        public void Return<T>(T instance) where T : class, new() => _impl.Return(instance);

        public IGameObjectPool GetGameObjectPool(GameObject prefab, int maxSize = 0) => _impl.GetGameObjectPool(prefab, maxSize);

        public GameObject Spawn(GameObject prefab, Transform parent = null) => _impl.Spawn(prefab, parent);

        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
            => _impl.Spawn(prefab, position, rotation, parent);

        public void Despawn(GameObject instance) => _impl.Despawn(instance);
    }
}
