using System;
using ObservableCollections;
using UnityEngine;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// UGUI 侧的响应式集合绑定 helper：把 <see cref="IReadOnlyObservableList{T}"/> <b>增量</b>绑到一个
    /// <see cref="Transform"/> 容器下（每个元素一个子 <see cref="GameObject"/>），与 UI Toolkit 的
    /// <c>Bag.BindList</c> 一套心智——集合增删移换只动对应子物体，不整表 <c>Destroy</c> 重建。
    /// </summary>
    /// <remarks>
    /// 都是 <see cref="DisposableBag"/> 的扩展：在 <c>MonoViewBase</c> 子类里 <c>Bag.BindList(content, source, factory)</c> 即可，
    /// 绑定登记进 Bag、随视图销毁统一解绑并销毁全部子物体。增量维护逻辑复用后端中立的
    /// <see cref="Game.Framework.UI.ReactiveListBinding"/>，此处只提供 UGUI 的「挂 / 摘 / 移」三个动作。
    /// </remarks>
    public static class UGuiListBindingExtensions
    {
        /// <summary>
        /// 把 <paramref name="source"/> 增量绑定到 <paramref name="container"/>：为每个元素造一个子 <see cref="GameObject"/>，
        /// 集合变化时增量增删移换对应子物体。子物体按源集合顺序排列（<see cref="Transform.SetSiblingIndex"/>）。
        /// </summary>
        /// <param name="container">承载子物体的父 <see cref="Transform"/>（一般挂了 <c>LayoutGroup</c> 自动排布）。</param>
        /// <param name="source">响应式源集合（一般是 Model 持有、经只读查询 Command 暴露）。</param>
        /// <param name="itemFactory">
        /// 为一个元素造子物体（如 <c>Instantiate(prefab)</c> 后填内容）；第二参是<b>该行专属</b>的子 <see cref="DisposableBag"/>——
        /// 行内订阅挂它，该行离开列表时自动退订。<b>不必</b>在工厂里设父级 / 兄弟位，交给绑定统一摆放；不得返回
        /// <c>null</c> / 已销毁对象，也不要在工厂内同步修改同一个 <paramref name="source"/>。
        /// </param>
        /// <remarks>
        /// 移除时子物体走 <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/>；要池化复用（弹幕级高频）请用领域 List + 手动池，不走本方法。
        /// 初始化失败会回滚已建行；增量回调失败会终止本次绑定并清理所有行，避免后续索引在半损坏容器上继续漂移。
        /// </remarks>
        public static IDisposable BindList<T>(
            this DisposableBag bag, Transform container,
            IReadOnlyObservableList<T> source,
            Func<T, DisposableBag, GameObject> itemFactory)
        {
            if (container == null)
                throw new ArgumentNullException(nameof(container), "UGUI BindList 需要一个仍然存活的 Transform 容器。");
            if (itemFactory == null) throw new ArgumentNullException(nameof(itemFactory));

            return Game.Framework.UI.ReactiveListBinding.Bind(
                bag,
                source,
                createItem: (value, rowBag) =>
                {
                    var go = itemFactory(value, rowBag);
                    if (go == null)
                    {
                        throw new InvalidOperationException(
                            "UGUI BindList 的 itemFactory 返回了 null 或已销毁的 GameObject；" +
                            "请返回一个有效实例，父级与兄弟位交给绑定设置。");
                    }

                    return go;
                },
                attach: (index, go) =>
                {
                    // worldPositionStays:false 保留 UI 的局部布局；随后放到目标兄弟位。
                    go.transform.SetParent(container, false);
                    go.transform.SetSiblingIndex(index);
                },
                detach: go =>
                {
                    if (go == null) return; // fake null：已被外部销毁则跳过
                    // Destroy 延迟到帧末执行，将死子物体这一帧仍占父级 childCount / sibling 索引；同帧若还有
                    // 插入或移动，会按被污染的层级算错兄弟位。先同步 SetParent(null) 摘出容器（对齐 Toolkit 的
                    // 同步 RemoveFromHierarchy），再交给帧末延迟销毁。
                    go.transform.SetParent(null, false);
                    UnityEngine.Object.Destroy(go);
                },
                reorder: (index, go) => { if (go != null) go.transform.SetSiblingIndex(index); });
        }
    }
}
