using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Framework.Pool
{
    /// <summary>
    /// 单个 prefab 的 GameObject 池：复用实例、避免频繁 Instantiate/Destroy 的 GC 与卡顿。
    /// </summary>
    /// <remarks>
    /// <b>键控：</b>每个池对应一个 prefab，经 <c>IPoolUtility.GetGameObjectPool(prefab)</c> 获取（同一 prefab 返回同一池）。<br/>
    /// <b>线程契约：</b>主线程独占（与框架 Container / <see cref="IObjectPool{T}"/> 一致），不加锁。<br/>
    /// <b>复用语义：</b><see cref="Spawn(Transform,bool)"/> 出来的实例会被 <see cref="GameObject.SetActive(bool)"/> 激活、
    /// local transform 重置为 prefab 默认值（位置/旋转/缩放），实例上实现了 <see cref="IPoolable"/> 的组件收到 <see cref="IPoolable.OnRent"/>；
    /// <see cref="Despawn"/> 时收到 <see cref="IPoolable.OnReturn"/>（<b>在此清理状态</b>），随后停用并挂回池的 parking 节点。<br/>
    /// <b>所有权：</b>Spawn 出去的实例用完必须 <see cref="Despawn"/>（或交给 <c>Bag.Spawn</c> 自动归还）；已 Despawn 的实例不要再用——它可能被下一次 Spawn 取走。
    /// </remarks>
    public interface IGameObjectPool
    {
        /// <summary>此池对应的源 prefab。</summary>
        GameObject Prefab { get; }

        /// <summary>当前池中空闲（已停用、可立即复用）的实例数。</summary>
        int CountInactive { get; }

        /// <summary>
        /// 当前 Spawn 出去未 Despawn 的实例数。实例被外部 Destroy（如随场景卸载）不会自动减——
        /// 计数停在借出侧本身就是线索（该实例再也不会归还池了）。持续增长 = 漏归还嫌疑。
        /// </summary>
        int CountActive { get; }

        /// <summary>
        /// 取一个实例并挂到 <paramref name="parent"/> 下（null = 挂到场景根）。会触发 <see cref="IPoolable.OnRent"/>。
        /// </summary>
        /// <param name="parent">父节点，null 表示挂到当前激活场景根。</param>
        /// <param name="worldPositionStays">
        /// SetParent 时是否保持世界位姿。<b>false</b>（默认）：local 位置/旋转/<b>缩放</b>整体重置为 prefab 默认值，
        /// 按 prefab 本地坐标摆放。<b>true</b>：保留实例当前世界位姿（含缩放），三项都不重置——复用实例时会沿用上次的 transform。
        /// </param>
        GameObject Spawn(Transform parent = null, bool worldPositionStays = false);

        /// <summary>取一个实例，置于指定世界 <paramref name="position"/> / <paramref name="rotation"/> 并挂到 <paramref name="parent"/>。会触发 <see cref="IPoolable.OnRent"/>。</summary>
        GameObject Spawn(Vector3 position, Quaternion rotation, Transform parent = null);

        /// <summary>
        /// 归还实例：触发 <see cref="IPoolable.OnReturn"/>、停用、挂回 parking 节点入池（超过容量上限则 Destroy）。
        /// null 安全；归还非本池实例在 Editor/Development Build 下 LogError。
        /// </summary>
        void Despawn(GameObject instance);

        /// <summary>
        /// 预创建 <paramref name="count"/> 个实例放入池中（受容量上限约束），避免运行期首次 Spawn 的实例化尖峰。
        /// <b>异步分帧：</b>每帧实例化 <paramref name="perFrame"/> 个，把开销摊到多帧（通常在加载界面期间调用），可被 <paramref name="ct"/> 取消。
        /// </summary>
        UniTask Prewarm(int count, int perFrame = 1, CancellationToken ct = default);

        /// <summary>Destroy 池中所有空闲实例（已 Spawn 出去的不受影响）。瞬时全销；要分帧用 <see cref="ClearAsync"/>。</summary>
        void Clear();

        /// <summary>
        /// 分帧把空闲实例收缩到至多 <paramref name="targetCount"/> 个：每帧 Destroy <paramref name="perFrame"/> 个，
        /// 把销毁开销摊到多帧（避免一次性 Destroy 大量实例造成卡顿）。已 Spawn 出去的不受影响；可被 <paramref name="ct"/> 取消。
        /// </summary>
        UniTask TrimAsync(int targetCount, int perFrame = 1, CancellationToken ct = default);

        /// <summary>分帧 Destroy 全部空闲实例（等价 <c>TrimAsync(0, …)</c>）。</summary>
        UniTask ClearAsync(int perFrame = 1, CancellationToken ct = default);
    }
}
