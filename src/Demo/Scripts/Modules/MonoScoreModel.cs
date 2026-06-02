using Game.Framework.Model;
using R3;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「Model · 状态与 Inspector」章的 Mono 路径示例：作为 Hierarchy 节点存在（挂在 DemoRoot 下）。
    /// </summary>
    /// <remarks>
    /// 继承 <c>MonoModelBase</c>：Awake 自动按 Hierarchy 父子关系注册进 <c>MonoDemoContext</c>，无需写注册代码。
    /// <c>Score</c> 用 <c>[field: SerializeField] RP&lt;int&gt;</c> 声明——Inspector 由框架 RPDrawer 实时显示当前值，
    /// 运行时可见其跳动、停止后可直接设初值。对照纯 C# 的 <c>CodeScoreModel</c>（代码注册、Inspector 不可见）。
    /// </remarks>
    public sealed class MonoScoreModel : MonoModelBase
    {
        [field: SerializeField] public RP<int> Score { get; private set; } = new(0);
    }
}
