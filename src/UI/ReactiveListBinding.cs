using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using Game.Framework.Internal;
using Game.Framework.Logging;
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
    /// <b>失败语义</b>：首次种入或订阅失败会回滚已创建的行；增量处理中任一 factory / 挂摘 / 移动回调失败，
    /// 说明源集合与外部 UI 容器已无法可靠回滚到同一状态，绑定会停止订阅并尽力释放全部行，再通过框架 Error 日志保留根因。
    /// 修复 Adapter / factory 后重新绑定，不会让半损坏绑定继续接收后续变化。所有行回调及 rowBag 的 Dispose 回调都不得
    /// 同步修改正在绑定的同一个源集合；create / attach / detach / reorder 内释放宿主会中断尚未提交的操作并记为失败，
    /// rowBag 清理期间释放宿主则是正常生命周期结束，引擎会等当前事件退栈后收口余行。<br/>
    /// <b>不做</b>：虚拟化 / 滚动复用（那是 Toolkit 原生 <c>ListView</c> 的活，见 guide §24；框架刻意不包装 <c>BindListView</c>）、
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
        /// <param name="bag">仍然存活的宿主生命周期容器：绑定随它 Dispose 时整体解绑、销毁全部子视图。</param>
        /// <param name="source">响应式源集合（一般是 Model 持有、经查询 Command 以 <see cref="IReadOnlyObservableList{T}"/> 暴露）。</param>
        /// <param name="createItem">
        /// 为一个元素造子视图。第二参是<b>该项专属</b>的子 bag——项内的订阅 / 资源挂它，项被移除时随之释放；
        /// 无需项内订阅就忽略它。子 bag 与宿主 bag 共享 Context（可 <c>Load</c> / 订阅 Framework Event）。
        /// 若工厂在返回视图前抛出，传入的子 bag 仍由引擎释放；引擎看不到的其它半成品须由工厂自己回收。
        /// 工厂及登记进子 bag 的 Dispose 回调都不得同步修改 <paramref name="source"/>；工厂也不得释放宿主
        /// <paramref name="bag"/>，因为当前行尚未完成所有权移交。
        /// </param>
        /// <param name="attach">
        /// 把子视图挂到容器的第 index 个兄弟位。若回调中途抛异常，引擎仍会把该视图交给
        /// <paramref name="detach"/> 做回滚，因此不要把 attach 当通知钩子去修改 <paramref name="source"/> 或释放宿主 bag。
        /// </param>
        /// <param name="detach">
        /// 把子视图从容器摘除并销毁。必须允许同一视图被重复调用，也要能处理“尚未挂上 / 只挂到一半”的视图，
        /// 以便初始化回滚、失败终止和正常 Dispose 共用同一条清理路径；不得同步修改 <paramref name="source"/> 或释放宿主 bag。
        /// </param>
        /// <param name="reorder">把子视图移动到第 index 个兄弟位（元素在源集合内 Move 时）；不得同步修改 <paramref name="source"/> 或释放宿主 bag。</param>
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
            if (bag.IsDisposed)
                throw new ObjectDisposedException(nameof(bag), "不能把响应式列表绑定到已经释放的 DisposableBag。");

            var binding = new Binding<TSource, TItem>(bag, source, createItem, attach, detach, reorder);
            bag.Add(binding); // 随宿主 bag 级联释放
            return binding;
        }

        // 一次绑定的全部状态：一份与源逐项对应的子视图 + 子 bag 表，加一条集合变化订阅。
        private sealed class Binding<TSource, TItem> : IDisposable
        {
            private enum BindingState
            {
                Initializing,
                Active,
                Terminated
            }

            private readonly DisposableBag _hostBag;
            private readonly IGameContext _ctx;
            private readonly IReadOnlyObservableList<TSource> _source;
            private readonly Func<TSource, DisposableBag, TItem> _createItem;
            private readonly Action<int, TItem> _attach;
            private readonly Action<TItem> _detach;
            private readonly Action<int, TItem> _reorder;
            private readonly List<Entry> _entries = new();
            private IDisposable _sub;
            private BindingState _state = BindingState.Initializing;
            private bool _handlingChange;
            private Exception _terminalCause;
            private NotifyCollectionChangedAction _terminalAction;
            private bool _hasTerminalAction;
            private bool _terminalFailureLogged;
            private bool _terminalCleanupStarted;

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
                _hostBag = bag;
                _ctx = bag.Context;
                _source = source;
                _createItem = createItem;
                _attach = attach;
                _detach = detach;
                _reorder = reorder;

                try
                {
                    // 先订阅再种入：factory / attach 若同步改回同一 source，OnChanged 能明确拒绝重入，
                    // 不会在“尚未订阅”的窗口静默漏掉变化。ObserveChanged 本身不回放已有项。
                    _sub = source.ObserveChanged(bag.DisposeToken).Subscribe(OnChanged);

                    // 构造只有全部种入且订阅成功后才把所有权交给宿主 bag。
                    for (var i = 0; i < source.Count; i++)
                    {
                        if (!InsertAt(i, source[i])) break;
                    }

                    // 初始化期间的非法重入会先把 binding 标成 Terminated；除了宿主存活，还要拒绝提交该状态。
                    EnsureCallbackCanCommit("初始化");
                    _state = BindingState.Active;
                }
                catch
                {
                    // Bind 尚未返回，宿主 bag 还持有不到 binding；这里必须自己回滚已经创建的所有行。
                    _state = BindingState.Terminated;
                    DisposeSubscription("初始化回滚", logFailure: true);
                    DisposeAllEntries(
                        "初始化回滚",
                        logFirstFailure: true,
                        retainFailedEntries: false,
                        validateCallbackState: false);
                    throw;
                }
            }

            private void OnChanged(CollectionChangedEvent<TSource> change)
            {
                if (_state == BindingState.Terminated) return;

                if (_state == BindingState.Initializing)
                {
                    var error = new InvalidOperationException(
                        "itemFactory / attach 不得在 Bind 初始化期间同步修改正在绑定的源集合；" +
                        "请把集合写入移到 Command 或绑定完成后的独立操作。");
                    TerminateAfterChangeFailure(change.Action, error, deferCleanup: true);
                    throw error;
                }

                if (_handlingChange)
                {
                    var error = new InvalidOperationException(
                        "响应式列表的 itemFactory / attach / detach / reorder 不得同步修改同一个源集合；" +
                        "该重入会破坏逐项索引契约，绑定已终止。");
                    TerminateAfterChangeFailure(change.Action, error, deferCleanup: true);
                    throw error;
                }

                _handlingChange = true;
                try
                {
                    switch (change.Action)
                    {
                        case NotifyCollectionChangedAction.Add:
                            InsertAt(change.NewStartingIndex, change.NewItem);
                            break;
                        case NotifyCollectionChangedAction.Remove:
                            RemoveAt(change.OldStartingIndex);
                            break;
                        case NotifyCollectionChangedAction.Replace:
                            // 子视图 = 元素值的纯函数：换值即重造该槽（最稳妥；就地更新是业务在项内订阅里的事）。
                            RemoveAt(change.NewStartingIndex);
                            InsertAt(change.NewStartingIndex, change.NewItem);
                            break;
                        case NotifyCollectionChangedAction.Move:
                            MoveEntry(change.OldStartingIndex, change.NewStartingIndex);
                            break;
                        case NotifyCollectionChangedAction.Reset:
                            Reset();
                            break;
                    }
                }
                catch (Exception error)
                {
                    // 源集合的变化已经提交，而 Adapter 可能只做了一半；外部副作用不可逆，不能假装还能继续同步。
                    // 外层 operation error 是主失败；若回滚/Dispose 又触发了嵌套错误，不能反过来覆盖它。
                    TerminateAfterChangeFailure(change.Action, error, preferCause: true);
                    LogTerminalFailure();
                }
                finally
                {
                    _handlingChange = false;
                    // Dispose 或 rowBag 回调可能只标记终止、等待当前 Adapter 回调退栈。
                    // 统一在最外层事件结束时收口，避免 Remove 的 rowBag.Dispose 之后没有下一次 Ensure 可触发清理。
                    CompleteDeferredTermination();
                }
            }

            private bool InsertAt(int index, TSource value)
            {
                // Replace / Reset 的前半段会先释放旧 rowBag；其 Dispose 回调可能在这里之前结束宿主。
                // 正常宿主结束不再启动任何新 factory，避免 Instantiate / 订阅等“终止后副作用”。
                if (_state == BindingState.Terminated && _terminalCause == null)
                    return false;
                EnsureCallbackCanCommit("itemFactory 调用前");

                var itemBag = new DisposableBag(_ctx);
                TItem view;

                try
                {
                    view = _createItem(value, itemBag);
                }
                catch
                {
                    // factory 可能已经把订阅挂进 itemBag；即使没有返回视图，子作用域也归引擎负责。
                    itemBag.Dispose();
                    throw;
                }

                try
                {
                    EnsureCallbackCanCommit("itemFactory");
                    _attach(index, view);
                    EnsureCallbackCanCommit("attach");
                    _entries.Insert(index, new Entry { View = view, ItemBag = itemBag });
                    return true;
                }
                catch
                {
                    // attach 可能先改了父子层级再抛；detach 契约允许清理未挂上或半挂上的视图。
                    DetachSafely(view, "新增行回滚");
                    itemBag.Dispose();
                    throw;
                }
            }

            private void RemoveAt(int index)
            {
                var entry = _entries[index];
                Exception detachError = null;

                try
                {
                    _detach(entry.View);
                    EnsureCallbackCanCommit("detach");
                }
                catch (Exception error) { detachError = error; }
                finally { entry.ItemBag.Dispose(); }

                // 失败时先保留 Entry，让终止清理有机会再次 detach；正常路径才提交内部表的删除。
                if (detachError != null)
                    ExceptionDispatchInfo.Capture(detachError).Throw();

                _entries.RemoveAt(index);
            }

            private void MoveEntry(int oldIndex, int newIndex)
            {
                var entry = _entries[oldIndex];
                _entries.RemoveAt(oldIndex);
                _entries.Insert(newIndex, entry);
                _reorder(newIndex, entry.View);
                EnsureCallbackCanCommit("reorder");
            }

            private void Reset()
            {
                // Reset 也必须尽力释放每一行；任一 detach 失败都会在全部清理完成后触发终止，而不是卡在第一行。
                var cleanupError = DisposeAllEntries(
                    "Reset",
                    logFirstFailure: false,
                    retainFailedEntries: true,
                    validateCallbackState: true);
                if (cleanupError != null)
                    ExceptionDispatchInfo.Capture(cleanupError).Throw();

                for (var i = 0; i < _source.Count; i++)
                {
                    if (!InsertAt(i, _source[i])) break;
                }
            }

            /// <summary>
            /// 逆序、尽力释放当前拥有的每一行。返回首个失败供 Reset 维持 fail-fast 语义；终止 / Dispose 路径则只记录。
            /// </summary>
            private Exception DisposeAllEntries(
                string phase,
                bool logFirstFailure,
                bool retainFailedEntries,
                bool validateCallbackState)
            {
                Exception firstFailure = null;

                // 子视图后进先出；即使某一行摘除失败，也不能阻断其余 itemBag 的释放。
                for (var i = _entries.Count - 1; i >= 0; i--)
                {
                    var entry = _entries[i];
                    var entryFailed = false;
                    // 前一行 rowBag 的清理可能已正常结束宿主；后续行仍要 best-effort 摘除，
                    // 但不能把“进入 detach 前就已终止”误判成当前 detach 导致的失败。
                    // 若本次 detach 自己终止或重入，进入时仍是 Active，下面的校验会照常报告。
                    bool validateThisDetach = validateCallbackState && _state != BindingState.Terminated;
                    try
                    {
                        _detach(entry.View);
                        if (validateThisDetach)
                            EnsureCallbackCanCommit("detach");
                    }
                    catch (Exception error)
                    {
                        entryFailed = true;
                        firstFailure = RecordCleanupFailure(
                            firstFailure, error, phase, i, logFirstFailure);
                    }

                    try { entry.ItemBag.Dispose(); }
                    catch (Exception error)
                    {
                        entryFailed = true;
                        firstFailure = RecordCleanupFailure(
                            firstFailure, error, phase, i, logFirstFailure);
                    }

                    // Reset / 直接 Dispose 的首轮保留失败项，外层终止清理可再尝试一次幂等 detach；
                    // 成功项立即退表，避免重试时重复触达已经完整清理的 View。
                    if (retainFailedEntries && !entryFailed)
                        _entries.RemoveAt(i);
                }

                if (!retainFailedEntries)
                    _entries.Clear();
                return firstFailure;
            }

            private static Exception RecordCleanupFailure(
                Exception firstFailure,
                Exception error,
                string phase,
                int index,
                bool logFirstFailure)
            {
                // Reset 的首个失败由调用者作为根因抛出；其余失败、以及不会再抛出的 Dispose/回滚失败必须逐条可见。
                if (firstFailure != null || logFirstFailure)
                {
                    Log.Error(
                        $"响应式列表绑定在{phase}时清理第 {index} 行失败；其余行仍会继续释放。",
                        error,
                        nameof(ReactiveListBinding));
                }

                return firstFailure ?? error;
            }

            private void DetachSafely(TItem view, string phase)
            {
                try { _detach(view); }
                catch (Exception error)
                {
                    Log.Error(
                        $"响应式列表绑定在{phase}时无法摘除一行；该行的子作用域仍会释放。",
                        error,
                        nameof(ReactiveListBinding));
                }
            }

            private void EnsureCallbackCanCommit(string callback)
            {
                EnsureHostAlive(callback);

                if (_state == BindingState.Terminated)
                {
                    throw new InvalidOperationException(
                        $"{callback} 返回前绑定已终止（常见原因是同步重入或宿主释放），当前行不会提交到容器镜像。",
                        _terminalCause);
                }
            }

            private void EnsureHostAlive(string phase)
            {
                if (_hostBag.IsDisposed)
                {
                    throw new ObjectDisposedException(
                        nameof(_hostBag),
                        $"响应式列表绑定在{phase}期间宿主 DisposableBag 已释放，当前初始化将回滚。");
                }
            }

            private Exception DisposeSubscription(string phase, bool logFailure)
            {
                var sub = _sub;
                _sub = null;
                if (sub == null) return null;

                try { sub.Dispose(); }
                catch (Exception error)
                {
                    if (logFailure)
                    {
                        Log.Error(
                            $"响应式列表绑定在{phase}时退订失败；行资源仍会继续释放。",
                            error,
                            nameof(ReactiveListBinding));
                    }

                    return error;
                }

                return null;
            }

            private void TerminateAfterChangeFailure(
                NotifyCollectionChangedAction action,
                Exception cause,
                bool deferCleanup = false,
                bool preferCause = false)
            {
                if (preferCause || _terminalCause == null)
                {
                    _terminalCause = cause;
                    _terminalAction = action;
                    _hasTerminalAction = true;
                }

                _state = BindingState.Terminated;
                DisposeSubscription($"{action} 失败终止", logFailure: true);
                // 同步重入发生在用户 callback 仍在栈上时；此刻递归调用 detach 会再次进入同一个坏 callback。
                // 先标终止并退订，等外层 OnChanged 捕获 EnsureCallbackCanCommit 的失败后再做一次完整清理。
                if (deferCleanup || _terminalCleanupStarted) return;
                _terminalCleanupStarted = true;
                DisposeAllEntries(
                    $"{action} 失败终止",
                    logFirstFailure: true,
                    retainFailedEntries: false,
                    validateCallbackState: false);
            }

            private void CompleteDeferredTermination()
            {
                if (_state != BindingState.Terminated) return;

                if (!_terminalCleanupStarted)
                {
                    _terminalCleanupStarted = true;
                    string phase = _hasTerminalAction
                        ? $"{_terminalAction} 失败终止"
                        : "回调内宿主释放";
                    DisposeAllEntries(
                        phase,
                        logFirstFailure: true,
                        retainFailedEntries: false,
                        validateCallbackState: false);
                }

                // rowBag 清理阶段主动 Dispose 宿主是正常生命周期结束，没有 terminal cause，也不制造误导性的 Error。
                LogTerminalFailure();
            }

            private void LogTerminalFailure()
            {
                if (_terminalCause == null || _terminalFailureLogged) return;
                _terminalFailureLogged = true;

                string action = _hasTerminalAction ? _terminalAction.ToString() : "集合变化";
                Log.Error(
                    $"响应式列表绑定处理 {action} 失败，已停止订阅并释放全部行；" +
                    "请修复 itemFactory 或 UI 容器 Adapter 后重新绑定。",
                    _terminalCause,
                    nameof(ReactiveListBinding));
            }

            public void Dispose()
            {
                if (_state == BindingState.Terminated && _terminalCleanupStarted) return;
                _state = BindingState.Terminated;
                var subscriptionFailure = DisposeSubscription("Dispose", logFailure: _handlingChange);

                // host bag / handle 可能在 create/attach/detach/reorder 回调里同步 Dispose。
                // 只先阻断订阅；外层 OnChanged 会观察终止态并在 callback 返回后完成清理，避免递归 detach。
                if (_handlingChange) return;
                _terminalCleanupStarted = true;

                // 直接 Dispose 维持“清理失败可观察”的既有契约，但先穷尽清理；挂在宿主 Bag 时由 Bag 统一隔离并记录。
                var firstFailure = subscriptionFailure;
                var entryFailure = DisposeAllEntries(
                    "Dispose",
                    logFirstFailure: firstFailure != null,
                    retainFailedEntries: true,
                    validateCallbackState: false);
                firstFailure ??= entryFailure;

                // detach 可能“先改层级再抛”或只是瞬时失败；契约要求幂等，首轮失败项在报告前再尽力收口一次。
                if (_entries.Count > 0)
                {
                    DisposeAllEntries(
                        "Dispose 重试",
                        logFirstFailure: true,
                        retainFailedEntries: false,
                        validateCallbackState: false);
                }

                if (firstFailure != null)
                    ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
        }
    }
}
