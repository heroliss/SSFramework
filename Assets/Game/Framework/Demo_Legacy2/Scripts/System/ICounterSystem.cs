using Game.Framework.System;

namespace Game.Framework.Demo.System
{
    /// <summary>计数器行为接口：System 暴露"动作"，不直接暴露 Model。</summary>
    public interface ICounterSystem : ISystem
    {
        void Increment();
        void Decrement();
        void Reset();
    }
}
