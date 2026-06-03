using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.View;
using R3;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// View 章演示用的真实 <see cref="MonoViewBase"/>（UGUI）。运行时被实例化到 demo Context 下：
    /// 基类 Awake 自动注入 + 绑 Bag；本 View 在 Awake 里接线——只读订阅查询 Command、只写经 ExecuteCommand、关闭即销毁自己（Bag 随之释放订阅）。
    /// </summary>
    public sealed class UGuiDemoView : MonoViewBase
    {
        [SerializeField] private Text _scoreText;
        [SerializeField] private Button _addButton;
        [SerializeField] private Button _closeButton;

        // View 的 DefaultExecutionOrder 是 -100（最晚）：Awake 跑时 Model/System/Utility 都已注册就绪，
        // 所以 View 可以直接在 Awake 里接线——这是框架刻意的设计。前提是先 base.Awake()：它负责注入 + 绑定 Context。
        protected override void Awake()
        {
            base.Awake();

            // 只读：查询 Command 返回状态流，订阅即得当前值——View 不直接读 Model。
            Bag.Subscribe(this.ExecuteCommand(new GetViewScoreCommand()), v => _scoreText.text = $"Score: {v}");

            // 只写：所有外发动作只能 ExecuteCommand（View 拿不到 GetModel/SendEvent 权限）。
            Bag.Subscribe(_addButton.onClick, () => this.ExecuteCommand(new RaiseViewScoreCommand()));

            // 关闭：销毁自己 → OnDestroy → Bag.Dispose → 退订。
            Bag.Subscribe(_closeButton.onClick, () => Destroy(gameObject));
        }
    }
}
