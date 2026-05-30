using System.Collections;
using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 1 — 分层架构可视化演示。
    /// </summary>
    /// <remarks>
    /// 点击 "Click Me"（View 行为）→ 依次点亮 View / Command / System / Model 四个层标签 + 流过的箭头，
    /// 最后 Model 数字变化。把"单向数据流"从文字变成 30 秒动画。
    /// </remarks>
    public sealed class Page_ArchitectureView : MonoViewBase
    {
        [Header("触发")]
        [SerializeField] private Button _triggerBtn;

        [Header("四层高亮目标（Image）")]
        [SerializeField] private Image _viewBox;
        [SerializeField] private Image _commandBox;
        [SerializeField] private Image _systemBox;
        [SerializeField] private Image _modelBox;

        [Header("状态文本")]
        [SerializeField] private TMP_Text _statusLabel;
        [Tooltip("Model 实时数字（订阅 GetCountStateCommand）。")]
        [SerializeField] private TMP_Text _modelValueLabel;

        [Header("颜色")]
        [SerializeField] private Color _idleColor   = new(0.18f, 0.20f, 0.26f, 1f);
        [SerializeField] private Color _activeColor = new(0.35f, 0.62f, 1.00f, 1f);
        [SerializeField] private float _stepDelay = 0.30f;

        protected override void Awake()
        {
            base.Awake();

            ResetVisuals();

            Bag.Subscribe(_triggerBtn.onClick, OnTrigger);

            var count = this.ExecuteCommand(new Command.GetCountStateCommand());
            Bag.Subscribe(count, v => _modelValueLabel.text = v.ToString());
        }

        private void OnTrigger()
        {
            StopAllCoroutines();
            StartCoroutine(PlayFlow());
        }

        private IEnumerator PlayFlow()
        {
            ResetVisuals();

            Highlight(_viewBox, "View 响应点击");
            yield return new WaitForSeconds(_stepDelay);

            Highlight(_commandBox, "View 发 IncrementCommand");
            yield return new WaitForSeconds(_stepDelay);

            Highlight(_systemBox, "Command 调 ICounterSystem.Increment()");
            yield return new WaitForSeconds(_stepDelay);

            Highlight(_modelBox, "System 写 CounterModel.Count.Value++");
            this.ExecuteCommand(new Command.IncrementCommand());
            yield return new WaitForSeconds(_stepDelay);

            _statusLabel.text = "RP 推送 → View 订阅者自动刷新";
        }

        private void Highlight(Image box, string status)
        {
            box.color = _activeColor;
            _statusLabel.text = status;
        }

        private void ResetVisuals()
        {
            _viewBox.color    = _idleColor;
            _commandBox.color = _idleColor;
            _systemBox.color  = _idleColor;
            _modelBox.color   = _idleColor;
            _statusLabel.text = "点击按钮观察一次数据流";
        }
    }
}
