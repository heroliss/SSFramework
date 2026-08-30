using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定 AssetReference 的包作用域提示，以及极窄 Inspector 下的布局边界。</summary>
    public sealed class AssetReferenceDrawerTests
    {
        [TestCase(null, "运行时默认包")]
        [TestCase("", "运行时默认包")]
        [TestCase("HotUpdate", "HotUpdate")]
        public void PackageDisplay_DoesNotGuessOneGlobalDefault(string packageName, string expected)
        {
            Assert.That(AssetReferenceDrawer.GetPackageDisplayText(packageName), Is.EqualTo(expected));
            string tooltip = AssetReferenceDrawer.GetPackageTooltip(packageName);
            if (string.IsNullOrEmpty(packageName))
            {
                Assert.That(tooltip, Does.Contain(nameof(IAssetUtility)));
                Assert.That(tooltip, Does.Contain("Context"));
            }
            else
            {
                Assert.That(tooltip, Does.Contain(packageName));
            }
        }

        [Test]
        public void ResolveLayoutMode_PreservesReadableControlsAtEveryBoundary()
        {
            Assert.That(AssetReferenceDrawer.ResolveLayoutMode(false, true, 300f),
                Is.EqualTo(AssetReferenceDrawer.LayoutMode.Compact));
            Assert.That(AssetReferenceDrawer.ResolveLayoutMode(true, true, 147.99f),
                Is.EqualTo(AssetReferenceDrawer.LayoutMode.Compact));
            Assert.That(AssetReferenceDrawer.ResolveLayoutMode(true, true, 148f),
                Is.EqualTo(AssetReferenceDrawer.LayoutMode.Inline));
            Assert.That(AssetReferenceDrawer.ResolveLayoutMode(false, false, 147.99f),
                Is.EqualTo(AssetReferenceDrawer.LayoutMode.Compact));
            Assert.That(AssetReferenceDrawer.ResolveLayoutMode(false, false, 148f),
                Is.EqualTo(AssetReferenceDrawer.LayoutMode.Inline));
            Assert.That(AssetReferenceDrawer.ResolveLayoutMode(true, false, -1f),
                Is.EqualTo(AssetReferenceDrawer.LayoutMode.Compact));
        }

        [TestCase(-10f)]
        [TestCase(0f)]
        [TestCase(2f)]
        [TestCase(75f)]
        [TestCase(76f)]
        [TestCase(147.99f)]
        [TestCase(148f)]
        [TestCase(300f)]
        [TestCase(2000f)]
        public void CalculateInlineWidths_NeverProducesNegativeOrOverflowingRects(float available)
        {
            var widths = AssetReferenceDrawer.CalculateInlineWidths(available);

            Assert.That(widths.Object, Is.GreaterThanOrEqualTo(0f));
            Assert.That(widths.Gap, Is.GreaterThanOrEqualTo(0f));
            Assert.That(widths.Package, Is.GreaterThanOrEqualTo(0f));
            Assert.That(widths.Package, Is.LessThanOrEqualTo(120f));
            Assert.That(widths.Object + widths.Gap + widths.Package,
                Is.LessThanOrEqualTo(System.Math.Max(0f, available) + 0.001f));
        }

        [TestCase(148f, 72f, 4f, 72f)]
        [TestCase(300f, 212f, 4f, 84f)]
        [TestCase(2000f, 1876f, 4f, 120f)]
        public void CalculateInlineWidths_PreservesObjectAndPackageAllocationContract(
            float available,
            float expectedObject,
            float expectedGap,
            float expectedPackage)
        {
            var widths = AssetReferenceDrawer.CalculateInlineWidths(available);

            Assert.That(widths.Object, Is.EqualTo(expectedObject).Within(0.001f));
            Assert.That(widths.Gap, Is.EqualTo(expectedGap).Within(0.001f));
            Assert.That(widths.Package, Is.EqualTo(expectedPackage).Within(0.001f));
        }

        [Test]
        public void CompactHeight_UsesTwoLinesWithoutLabel_AndThreeWithLabel()
        {
            Assert.That(AssetReferenceDrawer.CalculateHeight(
                AssetReferenceDrawer.LayoutMode.Inline, true, 18f, 2f), Is.EqualTo(18f));
            Assert.That(AssetReferenceDrawer.CalculateHeight(
                AssetReferenceDrawer.LayoutMode.Compact, false, 18f, 2f), Is.EqualTo(38f));
            Assert.That(AssetReferenceDrawer.CalculateHeight(
                AssetReferenceDrawer.LayoutMode.Compact, true, 18f, 2f), Is.EqualTo(58f));
        }

        [Test]
        public void UtilityPopup_StaysNearAnchorAndInsideDesktop()
        {
            var desktop = new Rect(0f, 0f, 1920f, 1080f);
            var size = new Vector2(340f, 96f);

            Rect below = AssetReferenceDrawer.CalculateUtilityPopupRect(
                new Vector2(100f, 100f), desktop, size);
            Rect flipped = AssetReferenceDrawer.CalculateUtilityPopupRect(
                new Vector2(1800f, 1060f), desktop, size);

            Assert.That(below.position, Is.EqualTo(new Vector2(100f, 104f)));
            AssertContained(flipped, desktop);
            Assert.That(flipped.y, Is.LessThan(1060f));
        }

        [Test]
        public void UtilityPopup_SupportsNegativeCoordinateMonitorsAndOversizedWindows()
        {
            var desktop = new Rect(-1920f, 0f, 1920f, 1080f);
            var size = new Vector2(340f, 96f);

            Rect topLeft = AssetReferenceDrawer.CalculateUtilityPopupRect(
                new Vector2(-1910f, 10f), desktop, size);
            Rect bottomRight = AssetReferenceDrawer.CalculateUtilityPopupRect(
                new Vector2(-10f, 1070f), desktop, size);
            AssertContained(topLeft, desktop);
            AssertContained(bottomRight, desktop);
            Assert.That(topLeft.size, Is.EqualTo(size));
            Assert.That(bottomRight.size, Is.EqualTo(size));

            var oversized = new Vector2(2200f, 1200f);
            Rect oversizedRect = AssetReferenceDrawer.CalculateUtilityPopupRect(
                desktop.center, desktop, oversized);
            Assert.That(oversizedRect.position, Is.EqualTo(desktop.position));
            Assert.That(oversizedRect.size, Is.EqualTo(oversized));
        }

        private static void AssertContained(Rect actual, Rect bounds)
        {
            Assert.That(actual.xMin, Is.GreaterThanOrEqualTo(bounds.xMin));
            Assert.That(actual.yMin, Is.GreaterThanOrEqualTo(bounds.yMin));
            Assert.That(actual.xMax, Is.LessThanOrEqualTo(bounds.xMax));
            Assert.That(actual.yMax, Is.LessThanOrEqualTo(bounds.yMax));
        }
    }
}
