using Game.Framework.Common;
using Game.Framework.Systems;

namespace Game.Framework.Demo.Modules.Services
{
    /// <summary>演示用问候 System：返回一句带时间戳的问候。</summary>
    public interface IDemoGreeterSystem : ISystem
    {
        string Greet();
    }

    /// <summary>
    /// 实现：<c>[Inject]</c> 字段依赖同一安装器注册的时间工具——构建期值绑定实例在 Context 构造时
    /// 自动完成注入（ADR-0019），这个纯 C# 服务从注册到依赖就绪全程零手写样板。
    /// </summary>
    public sealed class DemoGreeterSystem : IDemoGreeterSystem
    {
        [Inject] private IDemoTimeUtility _time;

        public string Greet() => $"你好！现在是 {_time.NowText()}——时间来自 [Inject] 自动注入的 IDemoTimeUtility。";
    }
}
