using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Localization;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using R3;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI Toolkit 本地化绑定 Adapter 的真实控件契约：数据源从不可用变为可用时，
    /// Label 应随 TextRevision 自动刷新，不要求伪造语言切换。
    /// </summary>
    public sealed class ToolkitLocalizationBindingTests
    {
        [Test]
        public void BindLocalizedText_RefreshesActualToolkitLabelWhenSourceBecomesReady()
        {
            var source = new DelayedTextSource();
            using var builder = new ContainerBuilder();
            builder.RegisterOwned(new LocalizationUtility(source, "zh-CN"), typeof(ILocalizationUtility));
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
            using var bag = new DisposableBag(context);
            var label = new Label();

            bag.BindLocalizedText(label, "demo/delayed");
            Assert.AreEqual("demo/delayed", label.text);

            source.SetReady("Toolkit 已自动刷新");
            Assert.AreEqual("Toolkit 已自动刷新", label.text);
            LogAssert.NoUnexpectedReceived();
        }

        private sealed class DelayedTextSource : ILocalizedTextSource
        {
            private readonly Subject<Unit> _invalidated = new();
            private bool _available;
            private string _text;

            public Observable<Unit> Invalidated => _invalidated;

            public LocalizedTextLookupStatus Lookup(string locale, string key, out string text)
            {
                text = null;
                if (!_available) return LocalizedTextLookupStatus.Unavailable;
                if (locale != "zh-CN" || key != "demo/delayed" || _text == null)
                    return LocalizedTextLookupStatus.Missing;
                text = _text;
                return LocalizedTextLookupStatus.Found;
            }

            public void SetReady(string text)
            {
                _available = true;
                _text = text;
                _invalidated.OnNext(Unit.Default);
            }
        }
    }
}
