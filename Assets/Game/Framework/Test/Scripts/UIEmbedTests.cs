using Game.Framework.UI.Bridge;
using Game.Framework.UI.Toolkit;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// UI 嵌入桥（ADR-0033）的纯函数回归：纹理尺寸换算（DPI 缩放 + 向上取整 + 钳上限）与
    /// 「同尺寸不重建」判定。只覆盖不触 GPU 的逻辑；渲染本身不单测（交 demo Play 抽查）。
    /// </summary>
    public sealed class UIEmbedTests
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
        public void ComputeTextureSize_ClampsToMaxDimension()
        {
            var size = RenderTextureElement.ComputeTextureSize(new Vector2(10000f, 10000f), Vector2.one, 2048);
            Assert.AreEqual(new Vector2Int(2048, 2048), size);
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

        // ── 输入穿透（v2）坐标换算与拖拽阈值 ──

        [Test]
        public void ComputeRtScreenPoint_CenterMapsToCenter()
        {
            // 元素中心 → RT 中心；y 轴翻转（Toolkit y 向下 → 屏幕 y 向上）中心仍是中心。
            var p = UGuiEmbedInputForwarder.ComputeRtScreenPoint(new Vector2(50f, 25f), new Vector2(100f, 50f), new Vector2Int(200, 100));
            Assert.AreEqual(new Vector2(100f, 50f), p);
        }

        [Test]
        public void ComputeRtScreenPoint_FlipsYAxis()
        {
            // 元素左上角 (0,0) → 屏幕左上 = (0, rtH)；右下角 → (rtW, 0)。
            var topLeft = UGuiEmbedInputForwarder.ComputeRtScreenPoint(Vector2.zero, new Vector2(100f, 50f), new Vector2Int(200, 100));
            Assert.AreEqual(new Vector2(0f, 100f), topLeft);
            var bottomRight = UGuiEmbedInputForwarder.ComputeRtScreenPoint(new Vector2(100f, 50f), new Vector2(100f, 50f), new Vector2Int(200, 100));
            Assert.AreEqual(new Vector2(200f, 0f), bottomRight);
        }

        [Test]
        public void ComputeRtScreenPoint_ZeroContentStaysAtOrigin()
        {
            // 未布局（内容框为 0）时不除零，退化到原点侧。
            var p = UGuiEmbedInputForwarder.ComputeRtScreenPoint(new Vector2(10f, 10f), Vector2.zero, new Vector2Int(200, 100));
            Assert.AreEqual(new Vector2(0f, 100f), p); // u=v=0 → (0, rtH)
        }

        [Test]
        public void ExceedsDragThreshold_FalseBelowTrueAtOrAbove()
        {
            var press = Vector2.zero;
            Assert.IsFalse(UGuiEmbedInputForwarder.ExceedsDragThreshold(press, new Vector2(5f, 0f), 10f));   // 5 < 10
            Assert.IsTrue(UGuiEmbedInputForwarder.ExceedsDragThreshold(press, new Vector2(10f, 0f), 10f));   // 10 >= 10
            Assert.IsTrue(UGuiEmbedInputForwarder.ExceedsDragThreshold(press, new Vector2(8f, 8f), 10f));    // |(8,8)|≈11.3 >= 10
        }
    }
}
