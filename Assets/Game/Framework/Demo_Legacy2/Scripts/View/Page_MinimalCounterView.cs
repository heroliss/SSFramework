using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 2 — 最小 Counter 互动演示。
    /// </summary>
    /// <remarks>
    /// 三个按钮（+ / - / Reset）通过 Command 写计数；两个文本订阅 Model RP 实时显示。
    /// View 没有 Inject 任何 Model/System——纯 Command + 订阅查询命令的闭环。
    /// </remarks>
    public sealed class Page_MinimalCounterView : MonoViewBase
    {
        [Header("按钮")]
        [SerializeField] private Button _incBtn;
        [SerializeField] private Button _decBtn;
        [SerializeField] private Button _resetBtn;

        [Header("文本")]
        [Tooltip("当前计数。订阅 GetCountStateCommand 返回的 RP。")]
        [SerializeField] private TMP_Text _countLabel;
        [Tooltip("累计 Command 执行次数。证明“View 只发 Command”是唯一入口。")]
        [SerializeField] private TMP_Text _commandCountLabel;

        protected override void Awake()
        {
            base.Awake();

            Bag.Subscribe(_incBtn.onClick,   () => this.ExecuteCommand(new Command.IncrementCommand()));
            Bag.Subscribe(_decBtn.onClick,   () => this.ExecuteCommand(new Command.DecrementCommand()));
            Bag.Subscribe(_resetBtn.onClick, () => this.ExecuteCommand(new Command.ResetCounterCommand()));

            var count = this.ExecuteCommand(new Command.GetCountStateCommand());
            Bag.Subscribe(count, v => _countLabel.text = v.ToString());

            var cmdCount = this.ExecuteCommand(new Command.GetCommandCountStateCommand());
            Bag.Subscribe(cmdCount, v => _commandCountLabel.text = $"Commands: {v}");
        }
    }
}
