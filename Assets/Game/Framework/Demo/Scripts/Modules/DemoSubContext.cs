using Game.Framework.Context;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「多 Context · 作用域树」章的子作用域节点（场景里的 ChapterAssets/SubContext，父级就是 demo 根 Context，不另造父节点）。
    /// 自身不注册任何东西——覆盖演示由挂在它子节点下的 <see cref="MonoScoreModel"/> 走 Mono 路径自动注册完成。
    /// 单独成类型只为给章节代码一个能 <c>FindFirstObjectByType</c> 精确定位的锚（直接找 MonoGameContextBase 会命中 demo 根）。
    /// </summary>
    public sealed class DemoSubContext : MonoGameContextBase
    {
    }
}
