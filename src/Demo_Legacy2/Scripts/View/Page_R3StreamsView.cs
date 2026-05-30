using System;
using Game.Framework.Common;
using Game.Framework.View;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 4 — R3 流派生演示。
    /// </summary>
    /// <remarks>
    /// 两个滑条 → <c>onValueChanged.AsObservable()</c> 转 Observable → 用 R3 操作符 CombineLatest / Throttle
    /// 派生出 Sum / Throttled Sum / Max。所有派生都是声明式表达，View 不写状态机、不写 Update。
    /// </remarks>
    public sealed class Page_R3StreamsView : MonoViewBase
    {
        [Header("输入")]
        [SerializeField] private Slider _sliderA;
        [SerializeField] private Slider _sliderB;

        [Header("输出文本")]
        [SerializeField] private TMP_Text _sumLabel;
        [Tooltip("CombineLatest 后 ThrottleLast(500ms)——演示节流。")]
        [SerializeField] private TMP_Text _throttledSumLabel;
        [SerializeField] private TMP_Text _maxLabel;

        protected override void Awake()
        {
            base.Awake();

            var a = _sliderA.onValueChanged.AsObservable().Prepend(_sliderA.value);
            var b = _sliderB.onValueChanged.AsObservable().Prepend(_sliderB.value);

            // CombineLatest → 任一变化即重算
            var sum = a.CombineLatest(b, (x, y) => x + y);
            var max = a.CombineLatest(b, (x, y) => Mathf.Max(x, y));

            // Throttle：连续滑动停下 500ms 后才更新文本
            var throttled = sum.ThrottleLast(TimeSpan.FromMilliseconds(500));

            Bag.Subscribe(sum,       v => _sumLabel.text          = $"Sum = {v:F2}");
            Bag.Subscribe(throttled, v => _throttledSumLabel.text = $"Throttled Sum = {v:F2}");
            Bag.Subscribe(max,       v => _maxLabel.text          = $"Max = {v:F2}");
        }
    }
}
