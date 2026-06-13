namespace Game.Framework.UI
{
    /// <summary>
    /// UI 层级。固定有序——枚举值<b>从下到上</b>决定堆叠顺序（后者盖前者），backend 按此顺序建层根。
    /// 窗口经 <see cref="UIWindowAttribute.Layer"/> 落到某一层；层内多窗口按打开先后堆叠。
    /// </summary>
    public enum UILayer
    {
        /// <summary>背景层：常驻底图 / 场景化背景，一般不参与栈。</summary>
        Background = 0,

        /// <summary>主界面层：全屏、互斥的"页"（大厅 / 关卡选择等）。配合返回栈（<see cref="IUIUtility.Back"/>）做页面导航，下层页被盖住时收到 OnCover。</summary>
        Page = 1,

        /// <summary>窗口层：浮在页面之上的功能窗口（背包 / 设置等），可多开。</summary>
        Window = 2,

        /// <summary>弹窗层：模态对话框 / 确认框。常配 <see cref="UIWindowAttribute.Modal"/> 弹遮罩拦截下层输入。</summary>
        Popup = 3,

        /// <summary>顶层：Loading / Toast / 新手引导等需要压住一切的临时界面。</summary>
        Top = 4,

        /// <summary>系统层：调试面板 / 断网提示等永远在最顶的界面。</summary>
        System = 5,
    }
}
