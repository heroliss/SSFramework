using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// UI Toolkit 安全区容器（ADR-0020 §3）：一个全屏拉伸的 <see cref="VisualElement"/>，
    /// 把 <see cref="Screen.safeArea"/> 与整屏的差值换算成面板单位设为自身 padding——子元素自然避开
    /// 刘海 / 挖孔 / 圆角。窗口把内容放进本容器即可；层根 / 背景刻意不做安全区（背景该出血铺满整屏）。
    /// </summary>
    /// <remarks>
    /// 挂上面板与几何变化（转屏 / 分辨率变化会触发重布局）时重算 padding，无逐帧轮询。
    /// 换算经 <see cref="RuntimePanelUtils.ScreenToPanel(IPanel, Vector2)"/>，正确处理 PanelSettings 的缩放模式。
    /// </remarks>
    [UxmlElement]
    public partial class SafeAreaContainer : VisualElement
    {
        /// <summary>创建一个随面板几何变化自动更新边距的安全区容器。</summary>
        public SafeAreaContainer()
        {
            pickingMode = PickingMode.Ignore; // 容器自身不吃事件，交互由子元素负责
            style.flexGrow = 1;
            RegisterCallback<AttachToPanelEvent>(_ => ApplyPadding());
            RegisterCallback<GeometryChangedEvent>(_ => ApplyPadding());
        }

        private void ApplyPadding()
        {
            if (panel == null) return;

            var sa = Screen.safeArea;
            // 屏幕像素（左下原点）→ 各边 inset；y 轴翻转：顶部 inset = 屏高 - safeArea 顶边。
            var insets = new Vector4(
                sa.xMin,                    // left
                Screen.height - sa.yMax,    // top
                Screen.width - sa.xMax,     // right
                sa.yMin);                   // bottom

            // 屏幕像素 → 面板单位（按 PanelSettings 缩放）：用两点差值取比例，避免面板原点偏移干扰。
            var origin = RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero);
            var unit = RuntimePanelUtils.ScreenToPanel(panel, Vector2.one) - origin;

            style.paddingLeft = insets.x * unit.x;
            style.paddingTop = insets.y * unit.y;
            style.paddingRight = insets.z * unit.x;
            style.paddingBottom = insets.w * unit.y;
        }
    }
}
