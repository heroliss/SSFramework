using System.Collections.Generic;
using System.Linq;
using Game.Framework.UI;
using NUnit.Framework;
using ObservableCollections;

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

        [SetUp]
        public void SetUp()
        {
            _bag = new DisposableBag();
            _source = new ObservableList<int>();
        }

        [TearDown]
        public void TearDown() => _bag.Dispose();

        private void Bind() => _h = new Harness(_bag, _source);

        private void AssertMirrorsSource()
            => CollectionAssert.AreEqual(_source, _h.Order, "子视图顺序应始终与源集合逐项对应");

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
    }
}
