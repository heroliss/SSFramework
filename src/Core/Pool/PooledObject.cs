using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>单个池化实例的内部租借事务状态；不向业务暴露。</summary>
    internal enum PooledInstanceState
    {
        Inactive,
        Renting,
        Active,
        Returning,
    }

    /// <summary>
    /// 池化 GameObject 的运行时标记组件，由 <see cref="GameObjectPool"/> 在实例化时自动挂上，业务不应手动添加。
    /// </summary>
    /// <remarks>
    /// 作用有二：<br/>
    /// 1. <b>归还路由：</b>记录实例来自哪个池，让 <c>IPoolUtility.Despawn(go)</c> 无需调用方再传 prefab 即可找回源池归还。<br/>
    /// 2. <b>性能：</b>实例化时一次性缓存自身及子节点上的 <see cref="IPoolable"/> 组件，避免每次 Spawn/Despawn 都 <c>GetComponentsInChildren</c> 分配。<br/>
    /// 字段为 internal：仅框架 Pool 层读写，对业务隐藏。
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PooledObject : MonoBehaviour
    {
        /// <summary>实例所属的池，用于 <c>Despawn</c> 路由。</summary>
        internal IGameObjectPool OwningPool;

        /// <summary>实例化时缓存的 <see cref="IPoolable"/> 组件（含未激活子节点），可能为空数组。</summary>
        internal IPoolable[] Poolables;

        /// <summary>
        /// 当前租借事务状态。Renting / Returning 会阻断钩子里的同步重入，避免同一实例被提前入池或重复发布。
        /// </summary>
        internal PooledInstanceState State;
    }
}
