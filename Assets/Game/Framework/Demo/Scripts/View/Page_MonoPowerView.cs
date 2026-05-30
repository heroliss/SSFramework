using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 5 — Mono 深度融合：两个并列子 Context 各自有独立的 Counter。
    /// </summary>
    /// <remarks>
    /// 关键演示：把 <see cref="MonoGameContextBase"/> 当作普通 GameObject 嵌进 Hierarchy，框架自动按
    /// Transform 父子识别 DI 树。本 View 持有 <c>_contextA</c> / <c>_contextB</c>（两个并列子 Context），
    /// 点 A/B 按钮时分别用 <see cref="GameContext.AttachTo"/> 临时切换执行环境，证明同一 Command 在不同
    /// Context 下访问到不同 Model 实例。
    /// </remarks>
    public sealed class Page_MonoPowerView : MonoViewBase
    {
        [Header("两个子 Context")]
        [Tooltip("拖入场景中 PageContainer/Page_04/SubContextA（挂 MonoGameContextBase + 子层 CounterModel/CounterSystem）。")]
        [SerializeField] private MonoGameContextBase _contextA;
        [Tooltip("拖入场景中 PageContainer/Page_04/SubContextB（独立子层）。")]
        [SerializeField] private MonoGameContextBase _contextB;

        [Header("按钮")]
        [SerializeField] private Button _incABtn;
        [SerializeField] private Button _incBBtn;
        [SerializeField] private Button _resetBothBtn;

        [Header("文本")]
        [SerializeField] private TMP_Text _countALabel;
        [SerializeField] private TMP_Text _countBLabel;

        protected override void Awake()
        {
            base.Awake();

            Bag.Subscribe(_incABtn.onClick, () => _contextA.ExecuteCommand(new Command.IncrementCommand()));
            var countA = _contextA.ExecuteCommand(new Command.GetCountStateCommand());
            Bag.Subscribe(countA, v => _countALabel.text = $"A: {v}");

            Bag.Subscribe(_incBBtn.onClick, () => _contextB.ExecuteCommand(new Command.IncrementCommand()));
            var countB = _contextB.ExecuteCommand(new Command.GetCountStateCommand());
            Bag.Subscribe(countB, v => _countBLabel.text = $"B: {v}");

            Bag.Subscribe(_resetBothBtn.onClick, () =>
            {
                _contextA.ExecuteCommand(new Command.ResetCounterCommand());
                _contextB.ExecuteCommand(new Command.ResetCounterCommand());
            });
        }
    }
}
