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
        /// 行内订阅挂它，该行离开列表时自动退订。<b>不必</b>在工厂里设父级 / 兄弟位，交给绑定统一摆放。
        /// </param>
        /// <remarks>移除时子物体走 <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/>；要池化复用（弹幕级高频）请用领域 List + 手动池，不走本方法。</remarks>
        public static IDisposable BindList<T>(
            this DisposableBag bag, Transform container,
            IReadOnlyObservableList<T> source,
            Func<T, DisposableBag, GameObject> itemFactory)
            => Game.Framework.UI.ReactiveListBinding.Bind(bag, source, itemFactory,
                attach: (index, go) =>
                {
                    // worldPositionStays:false 保留 UI 的局部布局；随后放到目标兄弟位。
                    go.transform.SetParent(container, false);
                    go.transform.SetSiblingIndex(index);
                },
                detach: go => { if (go != null) UnityEngine.Object.Destroy(go); }, // fake null：已被外部销毁则跳过
                reorder: (index, go) => { if (go != null) go.transform.SetSiblingIndex(index); });
    }
}
