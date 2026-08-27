using System;
using System.Collections.Generic;
using System.Linq;
using Game.Framework.Logging;
using Game.Framework.UI;
using NUnit.Framework;
using ObservableCollections;
using R3;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证后端中立的列表绑定引擎（ADR-0027 <see cref="ReactiveListBinding"/>）：种入快照、
    /// 增量 Add / Remove / Replace / Move / Reset 后子视图列表始终与源逐项对应，
    /// 每项子 bag 随该项进出创建 / 释放，解绑时全部子视图摘除、子 bag 释放、后续源变动不再触达。
    /// 纯 C# 逻辑用例——用一个引用式假容器代替真实 UI，无场景无帧推进。
    /// </summary>
    public class ReactiveListBindingTests
    {
        private sealed class CapturingSink : ILogSink
        {
            public readonly List<LogEntry> Entries = new();
            public LogLevel MinLevel => LogLevel.Trace;
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        // 假子视图：记录自己的值、专属子 bag，以及是否已被摘除——替代真实 VisualElement / GameObject。
        private sealed class FakeView
        {
            public readonly int Value;
            public readonly DisposableBag ItemBag;
            public bool Detached;
            public FakeView(int value, DisposableBag itemBag) { Value = value; ItemBag = itemBag; }
        }

        // 假容器 + 绑定：container 的顺序应始终等于源集合顺序。
        private sealed class Harness
        {
            public readonly List<FakeView> Container = new();
            public readonly List<FakeView> Created = new(); // 累计造过的所有行（查子 bag 释放）

            public Harness(DisposableBag bag, IReadOnlyObservableList<int> source)
            {
                ReactiveListBinding.Bind<int, FakeView>(
                    bag, source,
                    createItem: (v, itemBag) => { var view = new FakeView(v, itemBag); Created.Add(view); return view; },
                    attach: (index, view) => Container.Insert(index, view),
                    detach: view => { Container.Remove(view); view.Detached = true; },
                    reorder: (index, view) => { Container.Remove(view); Container.Insert(index, view); });
            }

            public IEnumerable<int> Order => Container.Select(v => v.Value);
        }

        private DisposableBag _bag;
        private ObservableList<int> _source;
        private Harness _h;
        private List<ILogSink> _previousSinks;
        private LogLevel _previousMinLevel;
        private CapturingSink _logSink;

        [SetUp]
        public void SetUp()
        {
            _previousSinks = new List<ILogSink>(Log.Sinks);
            _previousMinLevel = Log.MinLevel;
            Log.ClearSinks();
            Log.MinLevel = LogLevel.Trace;
            _logSink = new CapturingSink();
            Log.AddSink(_logSink);

            _bag = new DisposableBag();
            _source = new ObservableList<int>();
        }

        [TearDown]
        public void TearDown()
        {
            try { _bag.Dispose(); }
            finally
            {
                Log.ClearSinks();
                foreach (var sink in _previousSinks) Log.AddSink(sink);
                Log.MinLevel = _previousMinLevel;
            }
        }

        private void Bind() => _h = new Harness(_bag, _source);

        private void AssertMirrorsSource()
            => CollectionAssert.AreEqual(_source, _h.Order, "子视图顺序应始终与源集合逐项对应");

        private LogEntry AssertTerminalFailureLogged(string rootCause)
        {
            var entries = _logSink.Entries
                .Where(entry => entry.Level == LogLevel.Error &&
                                entry.Category == nameof(ReactiveListBinding) &&
                                entry.Message.Contains("已停止订阅并释放全部行"))
                .ToList();
            Assert.AreEqual(1, entries.Count, "每次增量失败应产生且只产生一条终止根因日志");
            StringAssert.Contains(rootCause, entries[0].Exception?.ToString());
            return entries[0];
        }

        // ── 种入快照 ─────────────────────────────────────────────────────────

        [Test]
        public void Bind_SeedsExistingItems_NoReplayNeededByCaller()
        {
            _source.Add(10); _source.Add(20); _source.Add(30);
            Bind(); // 订阅不回放已有项——引擎种入快照兜住
            AssertMirrorsSource();
            Assert.AreEqual(3, _h.Container.Count);
        }

        // ── 增量增删 ─────────────────────────────────────────────────────────

        [Test]
        public void Add_Insert_AddRange_MirrorInOrder()
        {
            Bind();
            _source.Add(1);                       // 尾加
            _source.Insert(0, 0);                 // 头插
            _source.AddRange(new[] { 2, 3, 4 });  // 批量（摊成逐项 Add）
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, _h.Order);
        }

        [Test]
        public void Insert_IntoMiddle_ShiftsTail()
        {
            _source.AddRange(new[] { 0, 1, 3 });
            Bind();
            _source.Insert(2, 2); // 插到中间（非头非尾）→ [0,1,2,3]
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, _h.Order);
        }

        [Test]
        public void Remove_DisposesThatRowBag_MirrorInOrder()
        {
            _source.AddRange(new[] { 1, 2, 3, 4 });
            Bind();
            var rowFor2 = _h.Container.First(v => v.Value == 2);

            _source.RemoveAt(1); // 移除值 2
            Assert.IsTrue(rowFor2.Detached, "被移除行应从容器摘除");
            Assert.IsTrue(rowFor2.ItemBag.IsDisposed, "被移除行的子 bag 应释放");
            AssertMirrorsSource();

            _source.RemoveRange(0, 2); // 再批量移除头两项（1、3）
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 4 }, _h.Order);
        }

        // ── 换值 ─────────────────────────────────────────────────────────────

        [Test]
        public void Replace_RecreatesSlot_DisposesOldRowBag()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            Bind();
            var oldRow = _h.Container.First(v => v.Value == 2);

            _source[1] = 99; // 索引器赋值 → Replace
            Assert.IsTrue(oldRow.Detached, "换值应摘除旧行");
            Assert.IsTrue(oldRow.ItemBag.IsDisposed, "旧行子 bag 应释放");
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 1, 99, 3 }, _h.Order);
        }

        // ── 移动 ─────────────────────────────────────────────────────────────

        [Test]
        public void Move_ReordersView_SameInstanceKept()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            Bind();
            var movedRow = _h.Container.First(v => v.Value == 1);

            _source.Move(0, 2); // [1,2,3] → [2,3,1]
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 2, 3, 1 }, _h.Order);
            Assert.AreSame(movedRow, _h.Container[2], "移动应复用同一行实例、不重造");
            Assert.IsFalse(movedRow.ItemBag.IsDisposed, "移动不释放该行子 bag");
        }

        [Test]
        public void Move_Backward_ReordersView_SameInstanceKept()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            Bind();
            var movedRow = _h.Container.First(v => v.Value == 3);

            _source.Move(2, 0); // oldIndex>newIndex 的后向移动 [1,2,3] → [3,1,2]（后向最易在“移除后索引”上算错）
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 3, 1, 2 }, _h.Order);
            Assert.AreSame(movedRow, _h.Container[0], "后向移动同样复用同一行实例");
            Assert.IsFalse(movedRow.ItemBag.IsDisposed, "移动不释放该行子 bag");
        }

        // ── 清空 ─────────────────────────────────────────────────────────────

        [Test]
        public void Clear_ResetsAll_DisposesEveryRowBag()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            Bind();
            var rows = _h.Container.ToList();

            _source.Clear(); // → Reset
            Assert.AreEqual(0, _h.Container.Count, "Reset 后容器应为空");
            Assert.IsTrue(rows.All(r => r.Detached && r.ItemBag.IsDisposed), "每行应摘除并释放子 bag");

            _source.Add(7); // Reset 后仍能继续增量
            AssertMirrorsSource();
            CollectionAssert.AreEqual(new[] { 7 }, _h.Order);
        }

        // ── 解绑 ─────────────────────────────────────────────────────────────

        [Test]
        public void DisposeBag_Unbinds_ClearsViews_StopsTracking()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            Bind();
            var rows = _h.Container.ToList();

            _bag.Dispose(); // 宿主释放 → 绑定级联解绑
            Assert.AreEqual(0, _h.Container.Count, "解绑应摘除全部子视图");
            Assert.IsTrue(rows.All(r => r.ItemBag.IsDisposed), "解绑应释放全部子 bag");

            _source.Add(42); // 解绑后源再变动不应触达容器
            Assert.AreEqual(0, _h.Container.Count, "解绑后不再跟踪源变化");
        }

        // ── 失败事务与终止态 ───────────────────────────────────────────────

        [Test]
        public void Bind_SeedFactoryThrows_RollsBackCommittedRowsAndEveryRowBag()
        {
            _source.AddRange(new[] { 1, 2 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var rowBags = new List<DisposableBag>();
            var factoryCalls = 0;

            var error = Assert.Throws<InvalidOperationException>(() =>
                ReactiveListBinding.Bind<int, FakeView>(
                    _bag,
                    _source,
                    createItem: (value, rowBag) =>
                    {
                        factoryCalls++;
                        rowBags.Add(rowBag);
                        if (value == 2) throw new InvalidOperationException("seed-factory-failed");
                        var view = new FakeView(value, rowBag);
                        rows.Add(view);
                        return view;
                    },
                    attach: (index, view) => container.Insert(index, view),
                    detach: view => { container.Remove(view); view.Detached = true; },
                    reorder: (index, view) => { container.Remove(view); container.Insert(index, view); }));

            StringAssert.Contains("seed-factory-failed", error.Message);
            Assert.AreEqual(0, container.Count, "构造失败时已提交的前序行也必须回滚");
            Assert.IsTrue(rows.All(row => row.Detached), "已返回给引擎的视图必须全部摘除");
            Assert.IsTrue(rowBags.All(rowBag => rowBag.IsDisposed), "含抛异常那一行在内，所有已创建子 bag 都必须释放");

            _source.Add(3);
            Assert.AreEqual(2, factoryCalls, "构造失败后订阅必须撤销，后续源变化不得再触达 factory");
        }

        [Test]
        public void Bind_AttachPartiallySucceedsThenThrows_RollsBackCandidateAndEarlierRows()
        {
            _source.AddRange(new[] { 1, 2 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            var error = Assert.Throws<InvalidOperationException>(() =>
                ReactiveListBinding.Bind<int, FakeView>(
                    _bag,
                    _source,
                    createItem: (value, rowBag) =>
                    {
                        var view = new FakeView(value, rowBag);
                        rows.Add(view);
                        return view;
                    },
                    attach: (index, view) =>
                    {
                        container.Insert(index, view); // 模拟 SetParent 已成功、SetSiblingIndex 才失败
                        if (view.Value == 2) throw new InvalidOperationException("seed-attach-failed");
                    },
                    detach: view => { container.Remove(view); view.Detached = true; },
                    reorder: (index, view) => { container.Remove(view); container.Insert(index, view); }));

            StringAssert.Contains("seed-attach-failed", error.Message);
            Assert.AreEqual(0, container.Count, "半挂上的候选行与已提交行都必须回滚");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
        }

        [Test]
        public void RuntimeAdd_FactoryThrows_TerminatesAndStopsFutureTracking()
        {
            _source.Add(1);
            var container = new List<FakeView>();
            var rowBags = new List<DisposableBag>();
            var factoryCalls = 0;

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    factoryCalls++;
                    rowBags.Add(rowBag);
                    if (value == 2) throw new InvalidOperationException("runtime-factory-failed");
                    return new FakeView(value, rowBag);
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.Add(2), "R3 observer 错误不从集合写入调用点反抛");
            AssertTerminalFailureLogged("runtime-factory-failed");
            Assert.AreEqual(0, container.Count, "增量失败后不能留下一个仍声称与 source 同步的旧容器");
            Assert.IsTrue(rowBags.All(rowBag => rowBag.IsDisposed));

            _source.Add(3);
            Assert.AreEqual(2, factoryCalls, "终止后的绑定不得消费后续源变化");
        }

        [Test]
        public void RuntimeReplace_NewFactoryThrows_DoesNotReviveDisposedOldSlot()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var rowBags = new List<DisposableBag>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    rowBags.Add(rowBag);
                    if (value == 99) throw new InvalidOperationException("replace-factory-failed");
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            var oldSlot = container[1];
            Assert.DoesNotThrow(() => _source[1] = 99);
            AssertTerminalFailureLogged("replace-factory-failed");
            Assert.IsTrue(oldSlot.Detached && oldSlot.ItemBag.IsDisposed, "Replace 失败不能复活已释放的旧槽");
            Assert.AreEqual(0, container.Count, "其余行也应随终止态释放，避免留下与新 source 不一致的旧镜像");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
            Assert.IsTrue(rowBags.All(rowBag => rowBag.IsDisposed), "失败的新槽 rowBag 同样必须释放");
        }

        [Test]
        public void RuntimeRemove_DetachThrows_RetriesDuringTerminalCleanupAndReleasesEveryBag()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var failDetachOnce = true;

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view =>
                {
                    if (view.Value == 2 && failDetachOnce)
                    {
                        failDetachOnce = false;
                        throw new InvalidOperationException("runtime-detach-failed");
                    }
                    container.Remove(view);
                    view.Detached = true;
                },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.RemoveAt(1));
            AssertTerminalFailureLogged("runtime-detach-failed");
            Assert.AreEqual(0, container.Count, "首轮摘除失败的行应在终止清理中按幂等契约重试");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed), "一个坏 detach 不能截断其余行清理");

            _source.Add(4);
            Assert.AreEqual(0, container.Count, "失败终止后不再追踪集合");
        }

        [Test]
        public void RuntimeMove_ReorderThrows_TerminatesAndCleansEveryCommittedRow()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) =>
                {
                    container.Remove(view);
                    container.Insert(index, view); // 模拟物理移动完成后 Adapter 才抛
                    throw new InvalidOperationException("runtime-reorder-failed");
                });

            Assert.DoesNotThrow(() => _source.Move(0, 2));
            AssertTerminalFailureLogged("runtime-reorder-failed");
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
        }

        [Test]
        public void Reset_DetachThrows_StillAttemptsEveryRowAndRetriesFailedEntry()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var failDetachOnce = true;

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view =>
                {
                    if (view.Value == 2 && failDetachOnce)
                    {
                        failDetachOnce = false;
                        throw new InvalidOperationException("reset-detach-failed");
                    }
                    container.Remove(view);
                    view.Detached = true;
                },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.Clear());
            AssertTerminalFailureLogged("reset-detach-failed");
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed), "Reset 不能被第一条坏行截断");
        }

        [Test]
        public void DirectDispose_DetachThrows_CleansAndRetriesBeforeReportingPrimaryFailure()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var failDetachOnce = true;

            var handle = ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view =>
                {
                    if (view.Value == 2 && failDetachOnce)
                    {
                        failDetachOnce = false;
                        throw new InvalidOperationException("dispose-detach-failed");
                    }
                    container.Remove(view);
                    view.Detached = true;
                },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            var error = Assert.Throws<InvalidOperationException>(() => handle.Dispose());
            StringAssert.Contains("dispose-detach-failed", error.Message);
            Assert.AreEqual(0, container.Count, "直接 Dispose 也要在报告失败前重试并穷尽清理");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
            Assert.DoesNotThrow(() => handle.Dispose(), "第二次 Dispose 必须幂等");

            _source.Add(4);
            Assert.AreEqual(0, container.Count);
        }

        [Test]
        public void SeedFactoryMutatesSameSource_FailsFastAndRemovesSubscription()
        {
            _source.Add(1);
            var rowBags = new List<DisposableBag>();
            var factoryCalls = 0;

            var error = Assert.Throws<InvalidOperationException>(() =>
                ReactiveListBinding.Bind<int, FakeView>(
                    _bag,
                    _source,
                    createItem: (value, rowBag) =>
                    {
                        factoryCalls++;
                        rowBags.Add(rowBag);
                        _source.Add(2); // 明确违反 Adapter 契约：初始化期间同步改同一 source
                        return new FakeView(value, rowBag);
                    },
                    attach: (_, __) => { },
                    detach: view => view.Detached = true,
                    reorder: (_, __) => { }));

            StringAssert.Contains("不得在 Bind 初始化期间同步修改", error.ToString());
            Assert.IsTrue(rowBags.All(rowBag => rowBag.IsDisposed));

            _source.Add(3);
            Assert.AreEqual(1, factoryCalls, "重入失败后必须撤销已经先行建立的订阅");
        }

        [Test]
        public void RuntimeReorderMutatesSameSource_DefersCleanupUntilCallbackReturns()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) =>
                {
                    container.Remove(view);
                    container.Insert(index, view);
                    _source.Add(4); // 明确违反契约：reorder 尚未返回就同步进入下一条集合事件
                });

            Assert.DoesNotThrow(() => _source.Move(0, 2));
            AssertTerminalFailureLogged("不得同步修改同一个源集合");
            Assert.AreEqual(0, container.Count, "终止清理应等 reorder 返回后执行，避免递归进入同一个 Adapter 回调");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
            Assert.AreEqual(3, rows.Count, "重入产生的新集合项不得再进入已经终止的 factory");
        }

        [Test]
        public void RuntimeRemove_RowBagDisposeMutatesSameSource_CompletesDeferredTerminalCleanup()
        {
            _source.AddRange(new[] { 1, 2 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    if (value == 1)
                    {
                        rowBag.Add(Disposable.Create(() =>
                            _source.Add(3))); // Remove 已摘行、正释放 rowBag 时同步重入
                    }
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.RemoveAt(0));
            AssertTerminalFailureLogged("不得同步修改同一个源集合");
            Assert.AreEqual(0, container.Count, "最外层 Remove 结束后必须收口延迟的终止清理");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
            Assert.AreEqual(2, rows.Count, "rowBag 回调写入的新项不得进入已终止的 factory");

            _source.Add(4);
            Assert.AreEqual(2, rows.Count, "终止后未来事件也必须忽略");
        }

        [Test]
        public void RuntimeRemove_RowBagDisposeDisposesHost_CleansRemainingRowsWithoutFailureLog()
        {
            _source.AddRange(new[] { 1, 2 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    if (value == 1)
                        rowBag.Add(Disposable.Create(_bag.Dispose));
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.RemoveAt(0));
            Assert.IsTrue(_bag.IsDisposed);
            Assert.AreEqual(0, container.Count, "宿主在 rowBag 回调中结束，也要在外层事件退出时释放剩余行");
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
            Assert.IsFalse(_logSink.Entries.Any(entry =>
                    entry.Level == LogLevel.Error &&
                    entry.Message.Contains("已停止订阅并释放全部行")),
                "主动释放宿主是正常生命周期结束，不应伪装成增量失败");

            _source.Add(3);
            Assert.AreEqual(2, rows.Count);
        }

        [Test]
        public void RuntimeReplace_OldRowBagDisposesHost_DoesNotInvokeReplacementFactory()
        {
            _source.Add(1);
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var factoryCalls = 0;

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    factoryCalls++;
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    if (value == 1)
                        rowBag.Add(Disposable.Create(_bag.Dispose));
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source[0] = 2);
            Assert.AreEqual(1, factoryCalls, "旧 rowBag 结束宿主后，不得为 Replace 新值启动 factory");
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.Single().Detached && rows.Single().ItemBag.IsDisposed);
            Assert.IsFalse(_logSink.Entries.Any(entry =>
                    entry.Level == LogLevel.Error &&
                    entry.Message.Contains("已停止订阅并释放全部行")),
                "rowBag 清理阶段结束宿主属于正常生命周期结束");
        }

        [Test]
        public void RuntimeReset_RowBagDisposeMutatesSameSource_DoesNotInvokeReseedFactory()
        {
            _source.Add(1);
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var factoryCalls = 0;

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    factoryCalls++;
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    if (value == 1)
                        rowBag.Add(Disposable.Create(() => _source.Add(3)));
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.Clear());
            AssertTerminalFailureLogged("不得同步修改同一个源集合");
            Assert.AreEqual(1, factoryCalls, "Reset 清理期间终止后，不得为重入写入的新项重启 factory");
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.Single().Detached && rows.Single().ItemBag.IsDisposed);
        }

        [Test]
        public void RuntimeReset_FirstDisposedRowBagDisposesHost_CleansLaterRowsWithoutFailureLog()
        {
            _source.AddRange(new[] { 1, 2, 3 });
            var container = new List<FakeView>();
            var rows = new List<FakeView>();
            var factoryCalls = 0;

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    factoryCalls++;
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    // Reset 逆序释放；值 3 的 rowBag 最先结束宿主，值 2/1 仍须无误报地继续清理。
                    if (value == 3)
                        rowBag.Add(Disposable.Create(_bag.Dispose));
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.Clear());
            Assert.AreEqual(3, factoryCalls, "宿主结束后 Reset 不得启动任何新 factory");
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed),
                "较早 rowBag 结束宿主不能截断后续行的 best-effort 清理");
            Assert.IsFalse(_logSink.Entries.Any(entry =>
                    entry.Level == LogLevel.Error &&
                    entry.Message.Contains("已停止订阅并释放全部行")),
                "进入后续 detach 前宿主已经正常结束，不应升级成 Reset 失败");
        }

        [Test]
        public void RuntimeFactoryDisposesHost_RollsBackCandidateAndCommittedRows()
        {
            _source.Add(1);
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    if (value == 2) _bag.Dispose();
                    return view;
                },
                attach: (index, view) => container.Insert(index, view),
                detach: view => { container.Remove(view); view.Detached = true; },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.Add(2));
            AssertTerminalFailureLogged("宿主 DisposableBag 已释放");
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed),
                "运行期 factory 返回的未挂载候选与此前已提交行都归引擎收口");
        }

        [Test]
        public void RuntimeAttachFailure_CleanupFailure_LogsPrimaryAndSupplementalErrors()
        {
            _source.Add(1);
            var container = new List<FakeView>();
            var rows = new List<FakeView>();

            ReactiveListBinding.Bind<int, FakeView>(
                _bag,
                _source,
                createItem: (value, rowBag) =>
                {
                    var view = new FakeView(value, rowBag);
                    rows.Add(view);
                    return view;
                },
                attach: (index, view) =>
                {
                    container.Insert(index, view);
                    if (view.Value == 2)
                        throw new InvalidOperationException("primary-attach-failed");
                },
                detach: view =>
                {
                    container.Remove(view);
                    view.Detached = true;
                    if (view.Value == 1)
                        throw new InvalidOperationException("secondary-cleanup-failed");
                },
                reorder: (index, view) => { container.Remove(view); container.Insert(index, view); });

            Assert.DoesNotThrow(() => _source.Add(2));
            var root = AssertTerminalFailureLogged("primary-attach-failed");
            Assert.AreEqual("primary-attach-failed", root.Exception?.Message,
                "清理失败不得覆盖触发终止的主异常");

            var supplemental = _logSink.Entries.Single(entry =>
                entry.Level == LogLevel.Error &&
                entry.Category == nameof(ReactiveListBinding) &&
                entry.Exception?.Message == "secondary-cleanup-failed");
            StringAssert.Contains("清理第", supplemental.Message);
            Assert.AreEqual(0, container.Count);
            Assert.IsTrue(rows.All(row => row.Detached && row.ItemBag.IsDisposed));
        }

        [Test]
        public void Bind_DisposedHost_FailsBeforeFactoryRuns()
        {
            _source.Add(1);
            var factoryCalls = 0;
            _bag.Dispose();

            var error = Assert.Throws<ObjectDisposedException>(() =>
                ReactiveListBinding.Bind<int, FakeView>(
                    _bag,
                    _source,
                    createItem: (value, rowBag) =>
                    {
                        factoryCalls++;
                        return new FakeView(value, rowBag);
                    },
                    attach: (_, __) => { },
                    detach: _ => { },
                    reorder: (_, __) => { }));

            Assert.AreEqual("bag", error.ObjectName);
            Assert.AreEqual(0, factoryCalls, "无效宿主应在产生任何视图副作用前被拒绝");
        }

        [Test]
        public void SeedFactoryDisposesHost_RollsBackReturnedCandidateAndStopsSeeding()
        {
            _source.AddRange(new[] { 1, 2 });
            var rows = new List<FakeView>();
            var factoryCalls = 0;

            var error = Assert.Throws<ObjectDisposedException>(() =>
                ReactiveListBinding.Bind<int, FakeView>(
                    _bag,
                    _source,
                    createItem: (value, rowBag) =>
                    {
                        factoryCalls++;
                        var view = new FakeView(value, rowBag);
                        rows.Add(view);
                        _bag.Dispose();
                        return view;
                    },
                    attach: (_, __) => { },
                    detach: view => view.Detached = true,
                    reorder: (_, __) => { }));

            StringAssert.Contains("宿主 DisposableBag 已释放", error.Message);
            Assert.AreEqual(1, factoryCalls, "宿主结束后不应继续创建后续行");
            Assert.IsTrue(rows.Single().Detached, "factory 已返回的候选视图已转移给引擎，初始化回滚应摘除它");
            Assert.IsTrue(rows.Single().ItemBag.IsDisposed);
        }
    }
}
