using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI.UGui
{
    /// <summary>
    /// UGUI 版 Toast 内置窗口（ADR-0020 §4）：底部居中的半透明文字条，自动超时关闭、不拦截任何输入。
    /// 业务经 <see cref="IUIUtility.ShowToast"/> 使用，不直接 Open 本类型；连续 Toast 复用同一实例（刷新文本、重置计时）。
    /// 纯代码搭建（Asset 留空）、Cache 复用（高频件不反复建）。
    /// </summary>
    [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache)]
    public sealed class UGuiToastWindow : UGuiWindowBase
    {
        private Text _text;
        private CancellationTokenSource _autoClose;

        protected override void OnCreated()
        {
            // 底部居中的条：横向布局自适应文本宽度；整棵树不吃 raycast（Toast 不拦输入）。
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image),
                typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            var rt = (RectTransform)panel.transform;
            rt.SetParent(transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 120f);

            var bg = panel.GetComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.75f);
            bg.raycastTarget = false;

            var layout = panel.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 12, 12);
            layout.childAlignment = TextAnchor.MiddleCenter;

            var fitter = panel.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(panel.transform, false);
            _text = textGo.GetComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = 28;
            _text.color = Color.white;
            _text.alignment = TextAnchor.MiddleCenter;
            _text.raycastTarget = false;
        }

        protected override void OnOpen(object args)
        {
            var toast = args as UIToastArgs;
            _text.text = toast?.Text ?? string.Empty;

            // 重开刷新计时；令牌链接 Context（Context 销毁级联取消挂起的 Delay）。
            _autoClose?.Cancel();
            _autoClose?.Dispose();
            _autoClose = CancellationTokenSource.CreateLinkedTokenSource(
                ((IHasGameContext)this).Context.CancellationToken);
            AutoClose(toast?.Duration ?? 2f, _autoClose.Token).Forget();
        }

        protected override void OnClose() => _autoClose?.Cancel();

        private async UniTaskVoid AutoClose(float seconds, CancellationToken ct)
        {
            try { await UniTask.Delay(TimeSpan.FromSeconds(seconds), ignoreTimeScale: true, cancellationToken: ct); }
            catch (OperationCanceledException) { return; }
            this.GetUtility<IUIUtility>().Close(this);
        }
    }

    /// <summary>
    /// UGUI 版全局 Loading 内置窗口（ADR-0020 §4）：模态挡输入 + 中央提示文本 + 旋转指示块。
    /// 业务优先经 <see cref="IUIUtility.AcquireLoading"/> 取得所有权句柄；兼容的 Show/Hide 调用仍可刷新与关闭。
    /// </summary>
    [UIWindow(Layer = UILayer.Top, Cache = UICachePolicy.Cache, Modal = true, BackClosable = false)]
    public sealed class UGuiLoadingWindow : UGuiWindowBase
    {
        private Text _text;
        private RectTransform _spinner;

        protected override void OnCreated()
        {
            var spinnerGo = new GameObject("Spinner", typeof(RectTransform), typeof(Image));
            _spinner = (RectTransform)spinnerGo.transform;
            _spinner.SetParent(transform, false);
            _spinner.sizeDelta = new Vector2(48f, 48f);
            _spinner.anchoredPosition = new Vector2(0f, 30f);
            var spinnerImg = spinnerGo.GetComponent<Image>();
            spinnerImg.color = new Color(1f, 1f, 1f, 0.9f);
            spinnerImg.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var textRt = (RectTransform)textGo.transform;
            textRt.SetParent(transform, false);
            textRt.sizeDelta = new Vector2(600f, 60f);
            textRt.anchoredPosition = new Vector2(0f, -40f);
            _text = textGo.GetComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.fontSize = 28;
            _text.color = Color.white;
            _text.alignment = TextAnchor.MiddleCenter;
            _text.raycastTarget = false;
        }

        protected override void OnOpen(object args)
            => _text.text = (args as UILoadingArgs)?.Text ?? string.Empty;

        private void Update()
        {
            // 简单旋转指示：无美术资源下的默认表现，正式项目通常用带资产的自定义 Loading 替代本内置件。
            if (_spinner != null) _spinner.Rotate(0f, 0f, -270f * Time.unscaledDeltaTime);
        }
    }
}
