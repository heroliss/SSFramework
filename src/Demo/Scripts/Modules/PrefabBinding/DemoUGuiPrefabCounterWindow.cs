using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.UGui;
using Game.Framework.Utility;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「UI 框架」章 UGUI 后端的 <b>prefab 演示窗口</b>：控件在 prefab 上摆好、脚本挂根上，节点引用由
    /// <c>DemoUGuiPrefabCounterWindow.nodes.g.cs</c> 自动生成（右键 prefab「生成 UI 绑定代码」产出，重生成覆盖）；
    /// 本文件只写业务逻辑、不会被覆盖。与代码搭建版 <see cref="UGuiCounterWindow"/> 复用同一份 MonoScoreModel——
    /// 说明「prefab 摆控件 + 生成绑定」和「代码搭建」两种接法在框架眼里完全一致。
    /// </summary>
    [UIWindow(Asset = "DemoUGuiPrefabCounterWindow", Layer = UILayer.Window)]
    public partial class DemoUGuiPrefabCounterWindow : UGuiWindowBase
    {
        protected override void OnCreated()
        {
            // 绑定字段（ScoreText / AddButton / CloseButton）在 BindNodes 已就绪——读写分离照旧。
            Bag.Subscribe(this.ExecuteCommand(new GetMonoScoreCommand()), v => ScoreText.text = $"分数：{v}");
            Bag.Subscribe(AddButton.onClick, () => this.ExecuteCommand(new RaiseMonoScoreCommand()));
            Bag.Subscribe(CloseButton.onClick, () => this.GetUtility<IUIUtility>().Close(this));
        }
    }
}
