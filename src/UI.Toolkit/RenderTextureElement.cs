using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// 把一张 <see cref="RenderTexture"/> 当背景显示的 UI Toolkit 元素：让 UGUI / 相机内容以「真内容」
    /// 身份进入 Toolkit 布局流——可被 <see cref="ScrollView"/> 滚动、被后续元素裁剪 / 遮挡。这与「浮层对齐」
    /// （把 UGUI Canvas 盖在面板之上、每帧对齐 <c>worldBound</c>）的伪嵌入是两回事，后者做不到裁剪 / 滚动（ADR-0033）。
    /// </summary>
    /// <remarks>
    /// 本元素只负责「显示 + 报告需要多大的纹理」，<b>不拥有</b> RenderTexture / 相机：布局尺寸变化时按
    /// 「内容框（面板点）× 面板→屏幕缩放」算出清晰所需的设备像素数，经 <see cref="DesiredPixelSizeChanged"/> 上报；
    /// 驱动方（相机 / UGUI 桥）据此建好等大的 RenderTexture 再 <see cref="SetTexture"/> 回来，纹理生命周期归驱动方。<br/>
    /// 尺寸经 <see cref="GeometryChangedEvent"/> 触发（转屏 / 面板缩放 / ScrollView 重布局都会重算），无逐帧轮询；
    /// 缩放换算沿用 <see cref="SafeAreaContainer"/> 的 <see cref="RuntimePanelUtils.ScreenToPanel"/> 单位向量法。
    /// </remarks>
    [UxmlElement]
    public partial class RenderTextureElement : VisualElement
    {
        // RenderTexture 单边像素上限：内容框 × DPI 再大也钳在此，避免超大 / 高 DPI 面板意外申请巨型显存。
        private int _maxTextureSize = 2048;

        /// <summary>当前显示所需的设备像素尺寸（最近一次布局算得）。</summary>
        public Vector2Int DesiredPixelSize { get; private set; }

        /// <summary>
        /// 显示所需的设备像素尺寸变化时触发（含首次布局）。参数为 (width, height)，单位设备像素、已钳到
        /// <see cref="MaxTextureSize"/>。驱动方据此重建等大 RenderTexture 再 <see cref="SetTexture"/> 回来。
        /// </summary>
        public event Action<int, int> DesiredPixelSizeChanged;

        /// <summary>RenderTexture 单边像素上限（默认 2048）。</summary>
        public int MaxTextureSize
        {
            get => _maxTextureSize;
            set => _maxTextureSize = Mathf.Max(1, value);
        }

        public RenderTextureElement()
        {
            pickingMode = PickingMode.Ignore; // 只读显示，不吃事件（输入穿透留 v2，见 ADR-0033）
            RegisterCallback<GeometryChangedEvent>(_ => RecomputeDesiredSize());
        }

        /// <summary>把驱动方建好的 RenderTexture 设为背景显示；传 <c>null</c> 清空。</summary>
        public void SetTexture(RenderTexture texture)
        {
            if (texture != null) style.backgroundImage = Background.FromRenderTexture(texture);
            else style.backgroundImage = StyleKeyword.None;
        }

        private void RecomputeDesiredSize()
        {
            if (panel == null) return;
            var size = ComputeTextureSize(contentRect.size, PanelToScreenScale(), _maxTextureSize);
            if (size == DesiredPixelSize) return; // 尺寸没变不打扰驱动方，避免无谓重建纹理
            DesiredPixelSize = size;
            DesiredPixelSizeChanged?.Invoke(size.x, size.y);
        }

        // 面板点 → 屏幕设备像素的缩放（各轴）。ScreenToPanel 给「每屏幕像素折合多少面板点」，取倒数即所求；
        // 用两点差值消除面板原点偏移（同 SafeAreaContainer）；y 轴屏幕坐标方向相反，只取比例大小故加绝对值。
        private Vector2 PanelToScreenScale()
        {
            var origin = RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero);
            var unit = RuntimePanelUtils.ScreenToPanel(panel, Vector2.one) - origin; // 面板点 / 屏幕像素
            float sx = Mathf.Abs(unit.x) < 1e-6f ? 1f : 1f / unit.x;
            float sy = Mathf.Abs(unit.y) < 1e-6f ? 1f : 1f / unit.y;
            return new Vector2(Mathf.Abs(sx), Mathf.Abs(sy));
        }

        /// <summary>
        /// 由内容框尺寸（面板点）与面板→屏幕缩放算出清晰所需的设备像素尺寸：逐轴相乘、向上取整（免少一行像素发虚）、
        /// 钳到 <paramref name="maxDimension"/>。抽成纯函数便于单测（不触 GPU）。
        /// </summary>
        public static Vector2Int ComputeTextureSize(Vector2 contentSizePoints, Vector2 panelToScreenScale, int maxDimension)
        {
            int w = Mathf.Clamp(Mathf.CeilToInt(contentSizePoints.x * panelToScreenScale.x), 0, maxDimension);
            int h = Mathf.Clamp(Mathf.CeilToInt(contentSizePoints.y * panelToScreenScale.y), 0, maxDimension);
            return new Vector2Int(w, h);
        }
    }
}
