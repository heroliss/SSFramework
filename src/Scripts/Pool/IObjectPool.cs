using System;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// 单一类型的对象池。复用实例、减少 GC。
    /// </summary>
    /// <remarks>
    /// <b>线程契约：</b>主线程独占（与框架 Container 一致），方法不加锁。<br/>
    /// <b>所有权：</b><see cref="Rent"/> 出去的实例归调用方所有，用完必须 <see cref="Return"/>（或交给 <c>DisposableBag.Rent</c> 自动归还）；
    /// 已 <see cref="Return"/> 的实例不要再使用——它可能被下一个租借者取走。<br/>
    /// <b>清理：</b>状态清理发生在归还时（<see cref="IPoolable.OnReturn"/> 或构造池时传入的 onReturn 委托）。
    /// </remarks>
    /// <typeparam name="T">池化对象类型（引用类型）。</typeparam>
    public interface IObjectPool<T> where T : class
    {
        /// <summary>当前池中空闲（可立即复用）的实例数。</summary>
        int CountInactive { get; }

        /// <summary>取一个实例：池中有则复用，否则用工厂新建。会触发 onRent / <see cref="IPoolable.OnRent"/>。</summary>
        T Rent();

        /// <summary>归还实例：触发 onReturn / <see cref="IPoolable.OnReturn"/> 后入池（超过容量上限则丢弃交 GC）。null 安全。</summary>
        void Return(T instance);

        /// <summary>预创建 <paramref name="count"/> 个实例放入池中（受容量上限约束），避免运行期首次租借的分配尖峰。</summary>
        void Prewarm(int count);

        /// <summary>清空池中所有空闲实例（已租出的不受影响）。</summary>
        void Clear();
    }

    /// <summary>
    /// 对象池工具：按类型管理一组 <see cref="IObjectPool{T}"/>，框架统一的池入口。
    /// 经 <c>this.GetUtility&lt;IPoolUtility&gt;()</c> 访问；或在 <c>MonoXxxBase</c> 子类里用 <c>Bag.Rent&lt;T&gt;()</c> 租借并随宿主自动归还。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b>在 Context 的 <c>InstallBindings</c> 中 <c>builder.RegisterValue(new PoolUtility(), typeof(IPoolUtility))</c>。<br/>
    /// <b>首次配置生效：</b>同一类型的池在首次配置（带工厂/钩子的 <c>GetPool</c>）时按参数创建；之后再取返回同一池，忽略后续参数。需要自定义工厂或钩子时，在首次使用前显式配置一次。<br/>
    /// <b>线程：</b>主线程独占。
    /// </remarks>
    public interface IPoolUtility : IUtility
    {
        /// <summary>获取（或用默认 <c>new T()</c> 工厂创建）<typeparamref name="T"/> 的池。</summary>
        IObjectPool<T> GetPool<T>() where T : class, new();

        /// <summary>
        /// 获取（或用自定义工厂/钩子创建）<typeparamref name="T"/> 的池。
        /// 若该类型的池已存在，返回既有池并忽略本次参数（首次配置生效）。
        /// </summary>
        IObjectPool<T> GetPool<T>(Func<T> factory, Action<T> onRent = null, Action<T> onReturn = null, int maxSize = 0) where T : class;

        /// <summary>从 <typeparamref name="T"/> 的默认池租借一个实例（等价 <c>GetPool&lt;T&gt;().Rent()</c>）。</summary>
        T Rent<T>() where T : class, new();

        /// <summary>把实例归还到 <typeparamref name="T"/> 的默认池（等价 <c>GetPool&lt;T&gt;().Return(instance)</c>）。</summary>
        void Return<T>(T instance) where T : class, new();

        // ── GameObject / Prefab 池 ──────────────────────────────────────────
        // 按 prefab 键控，与上面的纯 C# 对象池共用同一个工具入口。
        // 位置加载（按 location 异步加载 prefab）不内建于此——先 Bag.Load<GameObject>(location) 取到 prefab 再建池，
        // 以保持 PoolUtility 不依赖 Context/IAssetUtility（见 PoolUtility 注释）。

        /// <summary>获取（或创建）<paramref name="prefab"/> 的 GameObject 池；同一 prefab 返回同一池（首次的 maxSize 生效）。</summary>
        IGameObjectPool GetGameObjectPool(GameObject prefab, int maxSize = 0);

        /// <summary>从 <paramref name="prefab"/> 的池取一个实例并挂到 <paramref name="parent"/>（等价 <c>GetGameObjectPool(prefab).Spawn(parent)</c>）。</summary>
        GameObject Spawn(GameObject prefab, Transform parent = null);

        /// <summary>从 <paramref name="prefab"/> 的池取一个实例，置于指定世界位置/旋转并挂到 <paramref name="parent"/>。</summary>
        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null);

        /// <summary>归还一个 Spawn 出来的实例（经其 <see cref="PooledObject"/> 标记自动路由回源池）。null / 非池化对象安全忽略。</summary>
        void Despawn(GameObject instance);
    }
}
