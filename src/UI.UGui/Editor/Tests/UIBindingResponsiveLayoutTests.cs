using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace Game.Framework.UI.UGui.Editor.Tests
{
    /// <summary>锁定 UI Binding 窄 Inspector、低工作区 Popup 与 Overlay 的响应式契约。</summary>
    public sealed class UIBindingResponsiveLayoutTests
    {
        [TestCase(0f, true)]
        [TestCase(359.99f, true)]
        [TestCase(360f, false)]
        [TestCase(800f, false)]
        public void InspectorCompactMode_UsesStableBreakpoint(float width, bool expected)
        {
            Assert.That(UIBindingDataEditor.UseCompactLayout(width), Is.EqualTo(expected));
        }

        [TestCase(319.99f, true)]
        [TestCase(320f, false)]
        public void GenerationFields_StackOnlyAtVeryNarrowWidths(float width, bool expected)
        {
            Assert.That(UIBindingGenGUI.UseCompactLayout(width), Is.EqualTo(expected));
        }

        [TestCase(200f, 120f)]
        [TestCase(720f, 640f)]
        [TestCase(1080f, 760f)]
        [TestCase(2160f, 760f)]
        public void PopupHeightBudget_LeavesDesktopSafetyMargin(float desktopHeight, float expected)
        {
            Assert.That(UIBindingPopupLayout.CalculateMaxWindowHeight(desktopHeight), Is.EqualTo(expected));
        }

        [TestCase(0f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void PopupHeightBudget_InvalidDesktopHeightUsesSafeFallback(float desktopHeight)
        {
            Assert.That(UIBindingPopupLayout.CalculateMaxWindowHeight(desktopHeight), Is.EqualTo(640f));
        }

        [Test]
        public void PopupHeightClamp_NeverExceedsCurrentWorkAreaBudget()
        {
            Assert.That(UIBindingPopupLayout.ClampRequestedHeight(900f, 640f), Is.EqualTo(640f));
            Assert.That(UIBindingPopupLayout.ClampRequestedHeight(320f, 640f), Is.EqualTo(320f));
            Assert.That(UIBindingPopupLayout.ClampRequestedHeight(-10f, 640f), Is.Zero);
        }

        [Test]
        public void PopupBodyViewport_ReservesHeaderAndMakesTailScrollable()
        {
            Assert.That(UIBindingPopupLayout.CalculateBodyViewportHeight(900f, 240f, 30f),
                Is.EqualTo(210f));
            Assert.That(UIBindingPopupLayout.CalculateBodyViewportHeight(120f, 240f, 30f),
                Is.EqualTo(120f));
            Assert.That(UIBindingPopupLayout.CalculateBodyViewportHeight(120f, 20f, 30f),
                Is.Zero);
        }

        [Test]
        public void NodePopup_ManyComponentsClampWithoutResolvingOrCreatingProfileAssets()
        {
            float requested = UIBindingNodePopup.CalculateRequestedHeight(
                editable: true,
                rowCount: 50,
                lineHeight: 20f);

            Assert.That(requested, Is.EqualTo(1134f));
            Assert.That(UIBindingPopupLayout.ClampRequestedHeight(requested, 320f), Is.EqualTo(320f));
        }

        [Test]
        public void OverlayContent_CanShrinkAndWrapWithoutChangingPreferenceSemantics()
        {
            bool preferenceExisted = EditorPrefs.HasKey(UIBindingAutoGenOverlay.AutoGeneratePreferenceKey);
            bool previous = UIBindingAutoGenOverlay.AutoGenerate;
            var overlay = new UIBindingAutoGenOverlay();
            try
            {
                UIBindingAutoGenOverlay.AutoGenerate = false;
                VisualElement root = overlay.CreatePanelContent();
                var toggle = root.Q<Toggle>("ui-binding-autogen-toggle");

                Assert.That(root.name, Is.EqualTo("ui-binding-autogen-root"));
                Assert.That(root.style.minWidth.value.value, Is.Zero);
                Assert.That(root.style.flexShrink.value, Is.EqualTo(1f));
                Assert.That(toggle, Is.Not.Null);
                Assert.That(toggle.style.minWidth.value.value, Is.Zero);
                Assert.That(toggle.style.flexShrink.value, Is.EqualTo(1f));
                Assert.That(toggle.labelElement.style.minWidth.value.value, Is.Zero);
                Assert.That(toggle.labelElement.style.whiteSpace.value, Is.EqualTo(WhiteSpace.Normal));

                Assert.That(toggle.value, Is.False);
                UIBindingAutoGenOverlay.AutoGenerate = true;
                Assert.That(UIBindingAutoGenOverlay.AutoGenerate, Is.True);
            }
            finally
            {
                if (preferenceExisted)
                    UIBindingAutoGenOverlay.AutoGenerate = previous;
                else
                    EditorPrefs.DeleteKey(UIBindingAutoGenOverlay.AutoGeneratePreferenceKey);
            }
        }
    }
}
