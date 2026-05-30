using Game.Framework.Event;

namespace Game.Framework.Demo.Event
{
    /// <summary>
    /// 日志事件——一发送者 → 多独立接收者。用于 Chapter 3 演示订阅生命周期。
    /// </summary>
    public readonly struct LogEvent : IEvent
    {
        public readonly string Message;
        public readonly float SentAt;
        public LogEvent(string message, float sentAt) { Message = message; SentAt = sentAt; }
    }

    /// <summary>无数据通知事件——演示 <c>invokeImmediately</c> 与无参 handler。</summary>
    public readonly struct PingEvent : IEvent { }
}
