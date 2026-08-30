using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// UI Toolkit 渲染纹理元素（ADR-0033）的纯函数回归：纹理尺寸换算
    /// （DPI 缩放 + 向上取整 + 等比降采样上限）与「同尺寸不重建」判定。
    /// 只覆盖不触 GPU 的逻辑；渲染本身交 Demo Play 抽查。
    /// </summary>
    public sealed class RenderTextureElementTests
    {
        [Test]
        public void ComputeTextureSize_ScalesPointsByDpi()
        {
            // 100×50 面板点，2x DPI → 200×100 设备像素，保证高 DPI 下不发虚。
            var size = RenderTextureElement.ComputeTextureSize(new Vector2(100f, 50f), new Vector2(2f, 2f), 4096);
            Assert.AreEqual(new Vector2Int(200, 100), size);
        }

        [Test]
        public void ComputeTextureSize_CeilsFractionalPixels()
        {
            // 非整数像素向上取整，免少一行 / 一列采样点导致边缘发虚。
            var size = RenderTextureElement.ComputeTextureSize(new Vector2(100.2f, 50f), Vector2.one, 4096);
            Assert.AreEqual(new Vector2Int(101, 50), size);
        }

        [Test]
        public void ComputeTextureSize_SquareHitsMaxDimension()
        {
            var size = RenderTextureElement.ComputeTextureSize(new Vector2(10000f, 10000f), Vector2.one, 2048);
            Assert.AreEqual(new Vector2Int(2048, 2048), size);
        }

        [Test]
        public void ComputeTextureSize_LowBudgetPreservesWideAndTallAspectRatio()
        {
            // 宽高必须共用同一降采样比例：最长边命中预算，短边随之按比例收缩，显示时才不会被拉伸。
            var wide = RenderTextureElement.ComputeTextureSize(new Vector2(908f, 190f), Vector2.one, 128);
            var tall = RenderTextureElement.ComputeTextureSize(new Vector2(190f, 908f), Vector2.one, 128);

            Assert.AreEqual(new Vector2Int(128, 27), wide);
            Assert.AreEqual(new Vector2Int(27, 128), tall);
            Assert.That((float)wide.x / wide.y, Is.EqualTo(908f / 190f).Within(0.05f));
            Assert.That((float)tall.y / tall.x, Is.EqualTo(908f / 190f).Within(0.05f));
        }

        [Test]
        public void ComputeTextureSize_ZeroSizeStaysZero()
        {
            // 未布局 / 折叠时内容框为 0，纹理尺寸也应为 0（驱动方据此不申请纹理）。
            var size = RenderTextureElement.ComputeTextureSize(Vector2.zero, new Vector2(2f, 2f), 2048);
            Assert.AreEqual(Vector2Int.zero, size);
        }

        [Test]
        public void ShouldRecreate_TrueOnFirstValidSize()
        {
            Assert.IsTrue(CameraTextureRenderer.ShouldRecreate(0, 0, 256, 256));
        }

        [Test]
        public void ShouldRecreate_FalseOnSameSize()
        {
            Assert.IsFalse(CameraTextureRenderer.ShouldRecreate(256, 256, 256, 256));
        }

        [Test]
        public void ShouldRecreate_TrueWhenSizeChanges()
        {
            Assert.IsTrue(CameraTextureRenderer.ShouldRecreate(256, 256, 256, 512));
        }

        [Test]
        public void ShouldRecreate_FalseOnInvalidTargetSize()
        {
            // 宽或高为 0 / 负（元素折叠或未布局）时不重建，避免申请非法尺寸纹理。
            Assert.IsFalse(CameraTextureRenderer.ShouldRecreate(256, 256, 0, 256));
            Assert.IsFalse(CameraTextureRenderer.ShouldRecreate(0, 0, -1, -1));
        }
    }
}
