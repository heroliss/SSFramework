using System;
using System.Collections.Generic;
using Game.Framework.UI.UGui;
using NUnit.Framework;
using ObservableCollections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Framework.Test
{
    /// <summary>
    /// UGUI 后端列表绑定（ADR-0027 <see cref="UGuiListBindingExtensions"/>）的真实 <see cref="Transform"/> 回归测试。
    /// 引擎的增量逻辑在 <c>Game.Framework.UI.Tests/ReactiveListBindingTests</c> 已用假容器覆盖；这里专守 UGUI 后端<b>特有</b>的坑：
    /// <see cref="Object.Destroy(Object)"/> 延迟到帧末，若移除子物体后同帧还有插入 / 移动，将死子物体这一帧仍占
    /// sibling 索引会把兄弟位算错——detach 必须<b>同步</b>把子物体摘出容器。假容器无法复现帧末延迟销毁语义，故用真物体。
    /// </summary>
    public class UGuiListBindingTests
    {
        private DisposableBag _bag;
        private GameObject _root;
        private readonly List<GameObject> _spawned = new(); // 造过的行，TearDown 兜底清理

        [SetUp]
        public void SetUp()
        {
            _bag = new DisposableBag();
            _root = new GameObject("list-container");
        }

        [TearDown]
        public void TearDown()
        {
            _bag.Dispose(); // 解绑：子物体走延迟 Destroy
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go); // 帧不推进，同步销毁残留避免测试间泄漏
            _spawned.Clear();
            if (_root != null) Object.DestroyImmediate(_root);
        }

        // 行工厂：一个以元素命名的空 GameObject（父级/兄弟位交给绑定摆放）。
        private GameObject Row(string s, DisposableBag _)
        {
            var go = new GameObject(s);
            _spawned.Add(go);
            return go;
        }

        // 容器当前子物体名序（跳过 fake-null）。
        private List<string> ChildNames()
        {
            var names = new List<string>();
            for (var i = 0; i < _root.transform.childCount; i++)
            {
                var c = _root.transform.GetChild(i);
                if (c != null) names.Add(c.name);
            }
            return names;
        }

        [Test]
        public void Bind_SeedsChildrenInOrder()
        {
            var source = new ObservableList<string>();
            source.AddRange(new[] { "A", "B", "C" });
            _bag.BindList(_root.transform, source, Row);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, ChildNames());
        }

        [Test]
        public void RemoveThenInsert_SameFrame_KeepsSiblingOrder()
        {
            var source = new ObservableList<string>();
            source.AddRange(new[] { "A", "B", "C" });
            _bag.BindList(_root.transform, source, Row);

            // 同一「帧」（同步块，延迟 Destroy 尚未在帧末执行）内先移除再追加：
            source.RemoveAt(1); // 移除 B
            source.Add("D");    // 追加 D → 逻辑序 [A, C, D]

            // detach 同步把 B 摘出容器，childCount / 兄弟位不再被将死的 B 污染。
            Assert.AreEqual(3, _root.transform.childCount, "将死子物体应已同步摘出、不再占 childCount");
            CollectionAssert.AreEqual(new[] { "A", "C", "D" }, ChildNames(), "同帧移除+追加后兄弟序应等于源逻辑序");
        }

        [Test]
        public void Move_ReordersSiblings()
        {
            var source = new ObservableList<string>();
            source.AddRange(new[] { "A", "B", "C" });
            _bag.BindList(_root.transform, source, Row);

            source.Move(0, 2); // [A,B,C] → [B,C,A]
            CollectionAssert.AreEqual(new[] { "B", "C", "A" }, ChildNames());
        }

        [Test]
        public void DisposeBag_DetachesAllChildren()
        {
            var source = new ObservableList<string>();
            source.AddRange(new[] { "A", "B" });
            _bag.BindList(_root.transform, source, Row);
            Assert.AreEqual(2, _root.transform.childCount);

            _bag.Dispose(); // 解绑：全部子物体同步摘出容器（随后帧末销毁）
            Assert.AreEqual(0, _root.transform.childCount, "解绑后容器应无子物体");
        }

        [Test]
        public void Bind_DestroyedContainer_FailsBeforeDeferredCollectionChange()
        {
            var source = new ObservableList<string>();
            var destroyedContainer = _root.transform;
            Object.DestroyImmediate(_root);
            _root = null;

            var error = Assert.Throws<ArgumentNullException>(() =>
                _bag.BindList(destroyedContainer, source, Row));

            Assert.AreEqual("container", error.ParamName);
        }

        [Test]
        public void Bind_FactoryReturnsDestroyedObject_FailsClearlyAndDisposesRowBag()
        {
            var source = new ObservableList<string> { "A" };
            DisposableBag rowBag = null;

            var error = Assert.Throws<InvalidOperationException>(() =>
                _bag.BindList(_root.transform, source, (_, bag) =>
                {
                    rowBag = bag;
                    var go = new GameObject("already-destroyed");
                    Object.DestroyImmediate(go);
                    return go;
                }));

            StringAssert.Contains("null 或已销毁", error.Message);
            Assert.IsTrue(rowBag.IsDisposed, "无效 factory 结果对应的行作用域仍归绑定负责释放");
        }
    }
}
