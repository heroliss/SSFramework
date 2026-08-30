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

        /// <summary>
        /// 当前借出未归还的实例数。池在所有构建中按引用身份跟踪 lease，重复 / 外来 Return 会被拒绝，因此计数精确；
        /// 持续增长 = 漏归还嫌疑。
        /// </summary>
        int CountActive { get; }

        /// <summary>
        /// 取一个实例：池中有则复用，否则用工厂新建。会触发 onRent / <see cref="IPoolable.OnRent"/>。
        /// 工厂返回 null / 重复引用或租借钩子失败时不发布 lease；已触发钩子的实例会先 best-effort 执行归还补偿，再丢弃并重抛首异常。
        /// </summary>
        T Rent();

        /// <summary>
        /// 归还实例：触发 onReturn / <see cref="IPoolable.OnReturn"/> 后入池（超过容量上限则丢弃交 GC）。null 安全；
        /// 重复归还、归还外来引用或钩子重入会被拒绝（Editor/Development Build 额外记录错误）。归还钩子失败时仍完成其余清理、关闭 lease 并丢弃脏实例，然后重抛首异常。
        /// 所属 Utility 已释放时，既有 lease 仍可做最后一次 Return，但只清理、不再入池。
        /// </summary>
        void Return(T instance);

        /// <summary>预创建 <paramref name="count"/> 个实例放入池中（受容量上限约束），避免运行期首次租借的分配尖峰。</summary>
        void Prewarm(int count);

        /// <summary>清空池中所有空闲实例（已租出的不受影响）。只解除引用交 GC，<b>不</b>调用实例的 Dispose——见 <see cref="ObjectPool{T}"/> 的所有权说明。</summary>
        void Clear();

        /// <summary>把空闲实例收缩到至多 <paramref name="targetCount"/> 个，多余的丢弃交 GC（已租出的不受影响，不调用 Dispose）。</summary>
        void Trim(int targetCount);
    }

    /// <summary>
    /// 池计数的非泛型诊断视图：<see cref="PoolUtility"/> 按类型擦除（object）存储 <see cref="IObjectPool{T}"/>，
    /// 诊断枚举无法闭合泛型，经此接口读计数。仅诊断展示用，不进公共 API。
    /// </summary>
    internal interface IPoolCounters
    {
        int CountInactive { get; }
        int CountActive { get; }
    }

    /// <summary>
    /// PoolUtility 的内部生命周期接缝：封闭新租借并清空 idle，但允许已发布 lease 做最后一次 Return/Despawn。
    /// 不进入公共接口，避免业务手动终止由 Utility 管理的单池。
    /// </summary>
    internal interface IPoolLifetime
    {
        void Terminate();
    }

    /// <summary>把类型擦除后的实例归还给真实来源池；仅供 PoolUtility 的引用身份路由使用。</summary>
    internal interface IPoolReturnRoute
    {
        void ReturnObject(object instance);
    }

    /// <summary>PoolUtility 管理的 C# lease 来源表；直接构造的 ObjectPool 不需要此接缝。</summary>
    internal interface IPoolLeaseRegistry
    {
        void RegisterLease(object instance, IPoolReturnRoute route);
        void UnregisterLease(object instance, IPoolReturnRoute route);
    }

    /// <summary>
    /// 对象池工具：按类型管理一组 <see cref="IObjectPool{T}"/>，框架统一的池入口。
    /// 经 <c>this.GetUtility&lt;IPoolUtility&gt;()</c> 访问；或在 <c>MonoXxxBase</c> 子类里用 <c>Bag.Rent&lt;T&gt;()</c> 租借并随宿主自动归还。
    /// </summary>
    /// <remarks>
    /// <b>注册（按池生命周期选）：</b>纯 C# 跟随 Context 用 <c>builder.RegisterOwnedUtility(new PoolUtility())</c>（随 <c>GameContext.Dispose</c> 清池）；
    /// 已有外部 owner 时用 <c>RegisterUtility</c>；需 Inspector 配参数 / 跟随 GameObject 生命周期用 <see cref="MonoPoolUtility"/>。三者复用同一套池逻辑。<br/>
    /// <b>首次配置生效：</b>同一类型的池在首次配置（带工厂/钩子的 <c>GetPool</c>）时按参数创建；之后再取返回同一池，忽略后续参数。需要自定义工厂或钩子时，在首次使用前显式配置一次。<br/>
    /// <b>关闭：</b>Utility 释放后立即拒绝新建池与新租借；此前已借出的实例仍按引用身份路由回真实来源池，完成一次清理后丢弃。<br/>
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

        /// <summary>
        /// 按实例的引用身份把 lease 归还到真实来源池，不依赖调用点的静态 <typeparamref name="T"/>；
        /// 因此实例上转型后仍能正确归还。外来 / 重复实例会被忽略（Editor/Development Build 额外记录错误），绝不为 Return 新建池。
        /// </summary>
        void Return<T>(T instance) where T : class;

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
