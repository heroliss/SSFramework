using UnityEngine;

namespace Game.Framework.Logging
{
    /// <summary>
    /// Unity 日志流 → <see cref="Log"/> sink 的桥：订阅 <c>Application.logMessageReceivedThreaded</c>，
    /// 把**引擎报错、第三方包日志（YooAsset / UniTask / R3…）、业务裸 <c>Debug.Log</c>、未捕获异常**
    /// 统统转成 <see cref="LogEntry"/> 广播给已注册的 sink。开关见 <see cref="Log.CaptureUnityLogs"/>。
    /// </summary>
    /// <remarks>
    /// <b>它解决的问题</b>：不接管的话，<see cref="FileLogSink"/> 只收显式调用门面的日志——玩家崩在
    /// <c>NullReferenceException</c> 上时那条崩溃**不在日志文件里**，而它恰恰最该捞到。接管后一行调用点都不用改。<br/>
    /// <b>防回声（本类存在的关键）</b>：<see cref="UnityDebugLogSink"/> 会把门面日志转发成 <c>Debug.Log</c>，
    /// 而那次 <c>Debug.Log</c> 又会触发本回调 → 若不拦，同一条日志会被重复落盘，且坏 sink 的告警会无限递归。
    /// 故用 <b>线程私有</b>的 <see cref="Emitting"/> 标记「本线程此刻正在由框架往 Console 写」，回调见到就忽略。
    /// 用 <c>[ThreadStatic]</c> 而非普通静态：<c>logMessageReceivedThreaded</c> 会在**产生日志的那个线程**上同步回调，
    /// 而框架日志可能来自任意线程（网络接收循环等），普通静态标记会被并发线程互相踩。
    /// </remarks>
    internal static class UnityLogBridge
    {
        // 「本线程正在由框架往 Unity Console 写」——回调据此忽略自己的回声。
        [System.ThreadStatic] private static bool _emitting;

        internal static bool Emitting => _emitting;

        internal static void BeginEmit() => _emitting = true;

        internal static void EndEmit() => _emitting = false;

        private static bool _enabled;

        /// <summary>当前是否已接管 Unity 日志流。</summary>
        internal static bool Enabled => _enabled;

        /// <summary>幂等地开 / 关接管。</summary>
        internal static void SetEnabled(bool on)
        {
            if (on == _enabled) return;
            _enabled = on;
            if (on) Application.logMessageReceivedThreaded += OnUnityLog;
            else Application.logMessageReceivedThreaded -= OnUnityLog;
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            // 这条是框架自己刚写进 Console 的回声（UnityDebugLogSink 转发）——Console 里已经有了，
            // 且原始条目早已广播给各 sink，再走一遍就是重复落盘 + 潜在无限递归。
            if (_emitting) return;

            Log.DispatchFromUnity(ToLevel(type), condition, stackTrace);
        }

        // Unity 的 LogType 收敛到框架的四级：Assert / Exception 都按 Error 处理（都是「出事了」）。
        private static LogLevel ToLevel(LogType type) => type switch
        {
            LogType.Error or LogType.Exception or LogType.Assert => LogLevel.Error,
            LogType.Warning => LogLevel.Warning,
            _ => LogLevel.Info,   // LogType.Log
        };
    }
}
