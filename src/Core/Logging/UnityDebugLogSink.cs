using UnityEngine;

namespace Game.Framework.Logging
{
    /// <summary>
    /// 默认 sink：把日志转发到 Unity <c>Debug.Log / LogWarning / LogError</c>——
    /// 保留 Console 观感、双击定位源码行、stack trace。<see cref="Log"/> 出厂即装配一个。
    /// </summary>
    /// <remarks>
    /// <see cref="LogLevel.Trace"/> 的「仅 Editor/Development + Verbose」门控在门面层完成（发布版根本到不了这里），
    /// 本 sink 收到什么就转发什么——因此「把 <c>Debug.Log</c> 迁移到接缝」对 Console 输出零行为变化。<br/>
    /// <see cref="MinLevel"/> 可调（如设为 <see cref="LogLevel.Warning"/> 让 Console 只留警告以上，细粒度交给文件 sink）。<br/>
    /// <b>双击定位</b>靠门面方法上的 <c>[HideInCallstack]</c> 保住：没有它，Console 双击会跳进框架的转发方法而不是真正的调用点。
    /// </remarks>
    public sealed class UnityDebugLogSink : ILogSink
    {
        /// <summary>最低级别，默认 <see cref="LogLevel.Trace"/>（全收）。</summary>
        public LogLevel MinLevel { get; set; } = LogLevel.Trace;

        public void Log(in LogEntry entry)
        {
            // 桥接自 Unity 日志流的条目（引擎报错 / 第三方 / 裸 Debug.Log）：Console 里**已经有了**。
            // 再转发一次会重复刷屏，而且这次 Debug.Log 又会触发桥接回调 → 无限回环。文件 / 遥测 sink 照常收它们。
            if (entry.FromUnity) return;

            string text = entry.Category != null ? $"[{entry.Category}] {entry.Message}" : entry.Message;

            // 标记「本线程正在由框架往 Console 写」，让 UnityLogBridge 忽略接下来这几次 Debug.* 的回声，
            // 否则同一条日志会被桥接回来、再广播一遍（重复落盘）。
            UnityLogBridge.BeginEmit();
            try
            {
                switch (entry.Level)
                {
                    case LogLevel.Warning:
                        Debug.LogWarning(text, entry.Context);
                        break;
                    case LogLevel.Error:
                        Debug.LogError(text, entry.Context);
                        // 附带异常单独走 LogException，保留 Unity 对堆栈的定位能力。
                        if (entry.Exception != null) Debug.LogException(entry.Exception, entry.Context);
                        break;
                    default: // Trace / Info
                        Debug.Log(text, entry.Context);
                        break;
                }
            }
            finally
            {
                UnityLogBridge.EndEmit();
            }
        }
    }
}
