using System;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using ObservableCollections;
using UnityEngine.UIElements;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI Toolkit 列表 Adapter 的边界测试。共享 diff 与逐行生命周期由 <see cref="ReactiveListBindingTests"/> 覆盖；
    /// 此处只守最懂后端的前置条件，避免空集合让无效容器 / factory 延迟到未来 Add 才报错。
    /// </summary>
    public sealed class UIToolkitListBindingTests
    {
        private DisposableBag _bag;

        [SetUp]
        public void SetUp() => _bag = new DisposableBag();

        [TearDown]
        public void TearDown() => _bag.Dispose();

        [Test]
        public void Bind_NullContainer_FailsImmediatelyEvenWhenSourceIsEmpty()
        {
            var source = new ObservableList<int>();

            var error = Assert.Throws<ArgumentNullException>(() =>
                _bag.BindList((VisualElement)null, source, (_, __) => new Label()));

            Assert.AreEqual("container", error.ParamName);
        }

        [Test]
        public void Bind_FactoryReturnsNull_FailsClearlyAndDisposesRowBag()
        {
            var source = new ObservableList<int> { 1 };
            DisposableBag rowBag = null;

            var error = Assert.Throws<InvalidOperationException>(() =>
                _bag.BindList(source: source, container: new VisualElement(), itemFactory: (_, bag) =>
                {
                    rowBag = bag;
                    return null;
                }));

            StringAssert.Contains("itemFactory 返回了 null", error.Message);
            Assert.IsTrue(rowBag.IsDisposed, "无效 factory 结果对应的行作用域仍归绑定负责释放");
        }
    }
}
