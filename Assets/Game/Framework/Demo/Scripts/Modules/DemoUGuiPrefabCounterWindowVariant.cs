using Game.Framework.Common;
using Game.Framework.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「UI 框架」章 UGUI 后端的 <b>prefab 变体演示窗口</b>：它是 <see cref="DemoUGuiPrefabCounterWindow"/> 的预制体变体，
    /// 只在 Card 下多加一个「归零」按钮。变体窗口类<b>继承</b>基窗口类，节点绑定<b>只生成净新增字段</b>（ResetButton，见
    /// <c>DemoUGuiPrefabCounterWindowVariant.nodes.g.cs</c>）——基类的 Score 显示 / +1 / 关闭三条接线由 <c>base.OnCreated()</c>
    /// 复用、零重复。演示「变体 = 基窗口 + 增量」：改一处基窗口，所有变体自动跟随。
    /// </summary>
    [UIWindow(Asset = "DemoUGuiPrefabCounterWindowVariant", Layer = UILayer.Window)]
    public partial class DemoUGuiPrefabCounterWindowVariant : DemoUGuiPrefabCounterWindow
    {
        protected override void OnCreated()
        {
            base.OnCreated(); // 复用基类接线：Score 订阅 + AddButton(+1) + CloseButton(关闭)
            // 变体只接自己多出来的控件：归零按钮 → ResetMonoScoreCommand（与基类共享同一份 MonoScoreModel）。
            Bag.Subscribe(ResetButton.onClick, () => this.ExecuteCommand(new ResetMonoScoreCommand()));
        }
    }
}
