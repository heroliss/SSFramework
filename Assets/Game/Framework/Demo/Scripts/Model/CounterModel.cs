using Game.Framework.Model;
using R3;
using UnityEngine;

namespace Game.Framework.Demo.Model
{
    /// <summary>
    /// Demo 数据层：响应式计数器状态。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>Demo 章节 1/2（架构展示 + 最小 Counter）；其它章节按需引用。<br/>
    /// <b>为什么用 <c>RP&lt;T&gt;</c>：</b>框架封装的 <c>SerializableReactiveProperty</c>，Inspector 可见、可订阅，
    /// View 侧通过查询 Command 拿到只读引用订阅，单向数据流闭环。<br/>
    /// <b>不暴露接口：</b>Model 按具体类型注册，System 用 <c>this.GetModel&lt;CounterModel&gt;()</c> 访问。
    /// </remarks>
    public sealed class CounterModel : MonoModelBase
    {
        [Tooltip("当前计数；由 ICounterSystem 修改，View 通过 GetCountStateCommand 订阅。")]
        [field: SerializeField] public RP<int> Count { get; private set; } = new(0);

        [Tooltip("累计执行过的 Command 数；演示 Command 是唯一写入入口。")]
        [field: SerializeField] public RP<int> CommandCount { get; private set; } = new(0);
    }
}
