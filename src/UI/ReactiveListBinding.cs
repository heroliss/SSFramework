using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Game.Framework.Internal;
using ObservableCollections;
using R3;

namespace Game.Framework.UI
{
    /// <summary>
    /// 后端中立的「响应式列表 → UI 容器」增量绑定引擎。补 <c>RP&lt;T&gt;</c> 单值订阅覆盖不到的空缺：
    /// 集合状态（背包 / 聊天 / 排行榜 / 队伍）用 <see cref="ObservableList{T}"/> 持有，界面只跟着<b>增量</b>
    /// 增删移换，而不是每次变化整表重建（销毁全部子视图再重造，丢滚动 / 选中 / 焦点、且抖动 GC）。
    /// </summary>
    /// <remarks>
    /// 这里只做「保持一份 UI 子视图列表与源集合逐项对应」的<b>脏活</b>——索引管理、每项子作用域、
    /// 首次种入快照、销毁时序——刻意与具体 UI 技术无关（不认识 <c>VisualElement</c> / <c>Transform</c>）。
    /// 两个后端各写一个 ~15 行的 <c>Bag.BindList</c> 适配（UI Toolkit / UGUI），把「怎么挂 / 摘 / 移」三个动作
    /// 委托进来即可，绑定的正确性逻辑只此一份、只测一次。<br/>
    /// <b>心智与 <c>Bag.BindText</c> 一致</b>：绑定登记进宿主 <see cref="DisposableBag"/>，视图 Dispose 时统一解绑、
    /// 销毁全部子视图。每个列表项独享一个子 <see cref="DisposableBag"/>（随该项进出列表创建 / 销毁），
    /// 项内的响应式订阅（如「这一行的血条随 RP 刷新」）挂它，项被移除时自动退订。<br/>
    /// <b>不做</b>：虚拟化 / 滚动复用（那是 Toolkit <c>ListView</c> 的活，见 <c>Bag.BindListView</c>）、
    /// 过滤 / 排序视图（用 <see cref="ObservableList{T}"/> 之上的 <c>CreateView</c> 或业务侧组织数据）——
    /// 目标是「项数适中的 UI 列表」（背包 / 聊天 / 设置项），弹幕级高频用领域 List + 手动池。
    /// </remarks>
    public static class ReactiveListBinding
    {
        /// <summary>
        /// 把 <paramref name="source"/> 的每个元素映射成一个 UI 子视图，登记进 <paramref name="bag"/>，
        /// 并订阅集合变化做增量维护。绑定时立即为现有元素种入子视图（<see cref="IObservableCollection{T}"/> 订阅
        /// <b>不</b>回放已有项，故种入必须显式做）。
        /// </summary>
        /// <typeparam name="TSource">源集合元素类型（通常是不可变快照或自带 RP 的行数据）。</typeparam>
        /// <typeparam name="TItem">UI 子视图类型（<c>VisualElement</c> / <c>GameObject</c> 等，由后端决定）。</typeparam>
        /// <param name="bag">宿主生命周期容器：绑定随它 Dispose 时整体解绑、销毁全部子视图。</param>
        /// <param name="source">响应式源集合（一般是 Model 持有、经查询 Command 以 <see cref="IReadOnlyObservableList{T}"/> 暴露）。</param>
        /// <param name="createItem">
        /// 为一个元素造子视图。第二参是<b>该项专属</b>的子 bag——项内的订阅 / 资源挂它，项被移除时随之释放；
        /// 无需项内订阅就忽略它。子 bag 与宿主 bag 共享 Context（可 <c>Load</c> / 订阅 Framework Event）。
        /// </param>
        /// <param name="attach">把子视图挂到容器的第 index 个兄弟位。</param>
        /// <param name="detach">把子视图从容器摘除并销毁。</param>
        /// <param name="reorder">把子视图移动到第 index 个兄弟位（元素在源集合内 Move 时）。</param>
        /// <returns>可提前解绑的句柄（一般无需手动调用——已登记进 <paramref name="bag"/> 自动释放）。</returns>
        public static IDisposable Bind<TSource, TItem>(
            DisposableBag bag,
            IReadOnlyObservableList<TSource> source,
            Func<TSource, DisposableBag, TItem> createItem,
            Action<int, TItem> attach,
            Action<TItem> detach,
            Action<int, TItem> reorder)
        {
            if (bag == null) throw new ArgumentNullException(nameof(bag));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (createItem == null) throw new ArgumentNullException(nameof(createItem));
            if (attach == null) throw new ArgumentNullException(nameof(attach));
            if (detach == null) throw new ArgumentNullException(nameof(detach));
            if (reorder == null) throw new ArgumentNullException(nameof(reorder));

            var binding = new Binding<TSource, TItem>(bag, source, createItem, attach, detach, reorder);
            bag.Add(binding); // 随宿主 bag 级联释放
            return binding;
        }

        // 一次绑定的全部状态：一份与源逐项对应的子视图 + 子 bag 表，加一条集合变化订阅。
        private sealed class Binding<TSource, TItem> : IDisposable
        {
            private readonly IGameContext _ctx;
            private readonly IReadOnlyObservableList<TSource> _source;
            private readonly Func<TSource, DisposableBag, TItem> _createItem;
            private readonly Action<int, TItem> _attach;
            private readonly Action<TItem> _detach;
            private readonly Action<int, TItem> _reorder;
            private readonly List<Entry> _entries = new();
            private readonly IDisposable _sub;
            private bool _disposed;

            private struct Entry
            {
                public TItem View;
                public DisposableBag ItemBag;
            }

            public Binding(
                DisposableBag bag,
                IReadOnlyObservableList<TSource> source,
                Func<TSource, DisposableBag, TItem> createItem,
                Action<int, TItem> attach,
                Action<TItem> detach,
                Action<int, TItem> reorder)
            {
                _ctx = bag.Context;
                _source = source;
                _createItem = createItem;
                _attach = attach;
                _detach = detach;
                _reorder = reorder;

                // 种入当前快照（订阅不回放已有项）。
                for (var i = 0; i < source.Count; i++)
                    InsertAt(i, source[i]);

                // 订阅增量：ObserveChanged 把每次结构变化摊成逐项 Add/Remove/Move（含 AddRange/RemoveRange）
                // 加 Replace（索引器赋值）与 Reset（Clear）。ct 传 bag 的 dispose token，随 bag 一并完成退订。
                _sub = source.ObserveChanged(bag.DisposeToken).Subscribe(OnChanged);
            }

            private void OnChanged(CollectionChangedEvent<TSource> e)
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        InsertAt(e.NewStartingIndex, e.NewItem);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        RemoveAt(e.OldStartingIndex);
                        break;
                    case NotifyCollectionChangedAction.Replace:
                        // 子视图 = 元素值的纯函数：换值即重造该槽（最稳妥；就地更新是业务在项内订阅里的事）。
                        RemoveAt(e.NewStartingIndex);
                        InsertAt(e.NewStartingIndex, e.NewItem);
                        break;
                    case NotifyCollectionChangedAction.Move:
                        MoveEntry(e.OldStartingIndex, e.NewStartingIndex);
                        break;
                    case NotifyCollectionChangedAction.Reset:
                        Reset();
                        break;
                }
            }

            private void InsertAt(int index, TSource value)
            {
                var itemBag = new DisposableBag(_ctx);
                var view = _createItem(value, itemBag);
                _entries.Insert(index, new Entry { View = view, ItemBag = itemBag });
                _attach(index, view);
            }

            private void RemoveAt(int index)
            {
                var entry = _entries[index];
                _entries.RemoveAt(index);
                _detach(entry.View);
                entry.ItemBag.Dispose();
            }

            private void MoveEntry(int oldIndex, int newIndex)
            {
                var entry = _entries[oldIndex];
                _entries.RemoveAt(oldIndex);
                _entries.Insert(newIndex, entry);
                _reorder(newIndex, entry.View);
            }

            private void Reset()
            {
                DisposeAllEntries();
                for (var i = 0; i < _source.Count; i++)
                    InsertAt(i, _source[i]);
            }

            private void DisposeAllEntries()
            {
                // 逆序摘除：子视图后进先出，容器索引不因中途移除而漂移。
                for (var i = _entries.Count - 1; i >= 0; i--)
                {
                    _detach(_entries[i].View);
                    _entries[i].ItemBag.Dispose();
                }
                _entries.Clear();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _sub?.Dispose();
                DisposeAllEntries();
            }
        }
    }
}
