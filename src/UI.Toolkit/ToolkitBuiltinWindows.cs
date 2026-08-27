using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.UI.Toolkit
{
    /// <summary>
    /// UI Toolkit 版 Toast 内置窗口（ADR-0020 §4）：底部居中的半透明文字条、不拦截任何输入；自动关闭时序由 UI 核心统一持有。
    /// 业务经 <see cref="IUIUtility.ShowToast"/> 使用，不直接 Open 本类型；连续 Toast 复用同一实例（刷新文本、重置计时）。
    /// </summary>
    [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache)]
    public sealed class ToolkitToastWindow : UIToolkitWindowBase
    {
        private Label _label;

        protected override void OnCreated()
        {
            // Root 由 backend 全屏拉伸且 Ignore；用 flex 把条压到底部居中，整棵树不吃事件。
            Root.style.justifyContent = Justify.FlexEnd;
            Root.style.alignItems = Align.Center;
            Root.style.paddingBottom = 120;

            var panel = new VisualElement { name = "ToastPanel", pickingMode = PickingMode.Ignore };
            panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.75f);
            panel.style.borderTopLeftRadius = panel.style.borderTopRightRadius = 8;
            panel.style.borderBottomLeftRadius = panel.style.borderBottomRightRadius = 8;
            panel.style.paddingLeft = panel.style.paddingRight = 24;
            panel.style.paddingTop = panel.style.paddingBottom = 12;
            Root.Add(panel);

            _label = new Label { pickingMode = PickingMode.Ignore };
            _label.style.color = Color.white;
            _label.style.fontSize = 20;
            panel.Add(_label);
        }

        protected override void OnOpen(object args)
        {
            var toast = args as UIToastArgs;
            _label.text = toast?.Text ?? string.Empty;
        }
    }

    /// <summary>
    /// UI Toolkit 版全局 Loading 内置窗口（ADR-0020 §4）：模态挡输入 + 中央提示文本 + 旋转指示块。
    /// 业务优先经 <see cref="IUIUtility.AcquireLoading"/> 取得所有权句柄；兼容的 Show/Hide 调用仍可刷新与关闭。
    /// </summary>
    [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache, Modal = true, BackClosable = false)]
    public sealed class ToolkitLoadingWindow : UIToolkitWindowBase
    {
        private Label _label;
        private VisualElement _spinner;
        private float _angle;

        protected override void OnCreated()
        {
            Root.style.justifyContent = Justify.Center;
            Root.style.alignItems = Align.Center;

            _spinner = new VisualElement { name = "Spinner", pickingMode = PickingMode.Ignore };
            _spinner.style.width = 48;
            _spinner.style.height = 48;
            _spinner.style.backgroundColor = new Color(1f, 1f, 1f, 0.9f);
            Root.Add(_spinner);

            _label = new Label { pickingMode = PickingMode.Ignore };
            _label.style.color = Color.white;
            _label.style.fontSize = 20;
            _label.style.marginTop = 16;
            Root.Add(_label);

            // 简单旋转指示（schedule 随元素在面板上时执行）：无美术资源下的默认表现，
            // 正式项目通常用带资产的自定义 Loading 替代本内置件。
            _spinner.schedule.Execute(() =>
            {
                _angle = (_angle + 270f * Time.unscaledDeltaTime) % 360f;
                _spinner.style.rotate = new Rotate(_angle);
            }).Every(16);
        }

        protected override void OnOpen(object args)
            => _label.text = (args as UILoadingArgs)?.Text ?? string.Empty;
    }
}
