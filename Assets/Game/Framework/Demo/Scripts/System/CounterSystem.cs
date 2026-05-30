using Game.Framework.Common;
using Game.Framework.Model;
using Game.Framework.System;

namespace Game.Framework.Demo.System
{
    /// <summary>
    /// 计数器 System：唯一允许写 <see cref="Model.CounterModel"/> 的层。
    /// </summary>
    /// <remarks>
    /// View 想改计数 → 必须发 Command → Command 调本 System 的方法 → System 改 Model。
    /// 这一链条是框架"单向数据流"的物理体现。
    /// </remarks>
    public sealed class CounterSystem : MonoSystemBase, ICounterSystem
    {
        [Inject] private Model.CounterModel _model;

        public void Increment()
        {
            _model.Count.Value++;
            _model.CommandCount.Value++;
        }

        public void Decrement()
        {
            _model.Count.Value--;
            _model.CommandCount.Value++;
        }

        public void Reset()
        {
            _model.Count.Value = 0;
            _model.CommandCount.Value++;
        }
    }
}
