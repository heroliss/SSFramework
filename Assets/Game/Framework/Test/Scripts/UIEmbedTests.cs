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
    }
}
