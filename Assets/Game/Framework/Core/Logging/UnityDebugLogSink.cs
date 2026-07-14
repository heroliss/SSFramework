using UnityEngine;

namespace Game.Framework.Logging
{
    /// <summary>
    /// 默认 sink：把日志转发到 Unity <c>Debug.Log / LogWarning / LogError</c>——
    /// 保留 Console 观感、双击定位源码行、stack trace。<c>FrameworkLog</c> 出厂即装配一个。
    /// </summary>
    /// <remarks>
    /// 不做额外条件编译：<see cref="LogLevel.Trace"/> 的「仅 Editor/Development + Verbose」门控在门面层完成
    /// （发布版 Trace 根本不会到达 sink），本 sink 收到什么就转发什么，输出与否交给 Unity 的 <c>Debug</c> 既有行为。
    /// 因此「把 <c>Debug.Log</c> 迁移到接缝」对 Console 输出零行为变化。<br/>
    /// <see cref="MinLevel"/> 可调（如设为 <see cref="LogLevel.Warning"/> 让 Console 只留警告以上，细粒度日志交给文件 sink）。
    /// </remarks>
    public sealed class UnityDebugLogSink : ILogSink
    {
        /// <summary>最低级别，默认 <see cref="LogLevel.Trace"/>（全收）。</summary>
        public LogLevel MinLevel { get; set; } = LogLevel.Trace;

        public void Log(in LogEntry entry)
        {
            string text = entry.Category != null ? $"[{entry.Category}] {entry.Message}" : entry.Message;
            switch (entry.Level)
            {
                case LogLevel.Warning:
                    Debug.LogWarning(text);
                    break;
                case LogLevel.Error:
                    Debug.LogError(text);
                    // 附带异常单独走 LogException，保留 Unity 对堆栈的定位能力。
                    if (entry.Exception != null) Debug.LogException(entry.Exception);
                    break;
                default: // Trace / Info
                    Debug.Log(text);
                    break;
            }
        }
    }
}
