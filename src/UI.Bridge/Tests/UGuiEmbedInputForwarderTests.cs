using Game.Framework.UI.Bridge;
using NUnit.Framework;
using UnityEngine;

namespace Game.Framework.Test
{
    /// <summary>
    /// UGUI 嵌入输入桥（ADR-0033）的纯函数回归：Toolkit 局部坐标到 RenderTexture
    /// 屏幕坐标的换算，以及从点击升级为拖拽的阈值。
    /// </summary>
    public sealed class UGuiEmbedInputForwarderTests
    {
        [Test]
        public void ComputeRtScreenPoint_CenterMapsToCenter()
        {
            var point = UGuiEmbedInputForwarder.ComputeRtScreenPoint(
                new Vector2(50f, 25f), new Vector2(100f, 50f), new Vector2Int(200, 100));

            Assert.AreEqual(new Vector2(100f, 50f), point);
        }

        [Test]
        public void ComputeRtScreenPoint_FlipsYAxis()
        {
            var topLeft = UGuiEmbedInputForwarder.ComputeRtScreenPoint(
                Vector2.zero, new Vector2(100f, 50f), new Vector2Int(200, 100));
            var bottomRight = UGuiEmbedInputForwarder.ComputeRtScreenPoint(
                new Vector2(100f, 50f), new Vector2(100f, 50f), new Vector2Int(200, 100));

            Assert.AreEqual(new Vector2(0f, 100f), topLeft);
            Assert.AreEqual(new Vector2(200f, 0f), bottomRight);
        }

        [Test]
        public void ComputeRtScreenPoint_ZeroContentStaysAtOriginSide()
        {
            var point = UGuiEmbedInputForwarder.ComputeRtScreenPoint(
                new Vector2(10f, 10f), Vector2.zero, new Vector2Int(200, 100));

            Assert.AreEqual(new Vector2(0f, 100f), point);
        }

        [Test]
        public void ExceedsDragThreshold_FalseBelowTrueAtOrAbove()
        {
            var press = Vector2.zero;

            Assert.IsFalse(UGuiEmbedInputForwarder.ExceedsDragThreshold(
                press, new Vector2(5f, 0f), 10f));
            Assert.IsTrue(UGuiEmbedInputForwarder.ExceedsDragThreshold(
                press, new Vector2(10f, 0f), 10f));
            Assert.IsTrue(UGuiEmbedInputForwarder.ExceedsDragThreshold(
                press, new Vector2(8f, 8f), 10f));
        }
    }
}
