using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Framework.Logging
{
    /// <summary>
    /// 框架统一日志门面：分级记录 + 广播到一组可插拔 <see cref="ILogSink"/>（Console / 文件 / 遥测…）。
    /// **框架和业务共用同一个入口**——新代码写日志一律走这里，不要裸 <c>Debug.Log</c>（ADR-0034）。
    /// </summary>
    /// <remarks>
    /// <b>为什么是静态门面而非 DI 服务</b>：日志要在**任何地方**可用，包括身处 DI 之下、没有 <c>Context</c> 的内核基础设施
    /// （<c>Container</c> / <c>InjectionPlan</c> / 构造期）——它们不能反向依赖容器去取 logger。<br/>
    /// <b>出厂即用</b>：默认装配一个 <see cref="UnityDebugLogSink"/>（转 <c>Debug.Log</c>，Console 观感 / 双击定位 / 堆栈不变）。
    /// 启动时按需 <see cref="AddSink"/> 追加 <see cref="FileLogSink"/>（落盘）或自定义遥测 sink。<br/>
    /// <b>捞全量日志</b>：<see cref="CaptureUnityLogs"/> 把 Unity 自己的日志流（引擎报错 / 第三方包 / 业务裸
    /// <c>Debug.Log</c> / 未捕获异常）也灌进 sink——**不接管的话，玩家崩溃的那个 NullReferenceException 根本不在你的日志文件里**。<br/>
    /// <b>级别语义</b>：<see cref="LogLevel.Trace"/> 是诊断噪音，受 <see cref="Verbose"/> 开关 + 仅
    /// <c>UNITY_EDITOR || DEVELOPMENT_BUILD</c> 双重门控（发布版整个调用被 <see cref="ConditionalAttribute"/> 从 IL 删除）；
    /// <see cref="LogLevel.Info"/> 及以上始终广播。<br/>
    /// <b>线程</b>：可从任意线程调用；sink 列表用 copy-on-write，广播无锁。
    /// </remarks>
    public static class Log
    {
        /// <summary>是否放行 <see cref="LogLevel.Trace"/>（框架诊断噪音：注册/覆盖/解析/重试等）。默认关闭。</summary>
        public static bool Verbose = false;

        // sink 列表用 copy-on-write：广播（热路径）读快照无锁，仅增删时在锁内重建数组。
        // volatile 保证其它线程看到新数组引用；元素不就地修改，故引用级 volatile 足够。
        private static volatile ILogSink[] _sinks = { new UnityDebugLogSink() };
        private static readonly object _gate = new();

        // ── sink 管理 ──────────────────────────────────────────────────────

        /// <summary>追加一个日志去向。可多次调用形成广播（Console + 文件 + 遥测…）。<c>null</c> 忽略。</summary>
        public static void AddSink(ILogSink sink)
        {
            if (sink == null) return;
            lock (_gate)
            {
                var old = _sinks;
                var arr = new ILogSink[old.Length + 1];
                Array.Copy(old, arr, old.Length);
                arr[old.Length] = sink;
                _sinks = arr;
            }
        }

        /// <summary>移除一个 sink（引用相等）。返回是否移除到。</summary>
        public static bool RemoveSink(ILogSink sink)
        {
            if (sink == null) return false;
            lock (_gate)
            {
                var old = _sinks;
                int idx = Array.IndexOf(old, sink);
                if (idx < 0) return false;
                var arr = new ILogSink[old.Length - 1];
                Array.Copy(old, 0, arr, 0, idx);
                Array.Copy(old, idx + 1, arr, idx, old.Length - idx - 1);
                _sinks = arr;
                return true;
            }
        }

        /// <summary>清空所有 sink（含默认 Console）。测试常用——静音后装一个可捕获 sink。</summary>
        public static void ClearSinks()
        {
            lock (_gate) _sinks = Array.Empty<ILogSink>();
        }

        /// <summary>
        /// 该级别当前是否会被**任何** sink 消费。用于在调用点跳过昂贵的消息构造：
        /// <c>if (Log.IsEnabled(LogLevel.Info)) Log.Info(BuildExpensiveReport());</c>
        /// </summary>
        /// <remarks>
        /// <c>Log.Trace($"...")</c> 已由插值处理器自动做到这一点（<see cref="TraceInterpolatedStringHandler"/>），无需手写守卫；
        /// 本方法是给「其它级别 + 参数确实昂贵」的少数场景准备的逃生舱。
        /// </remarks>
        public static bool IsEnabled(LogLevel level)
        {
#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
            if (level == LogLevel.Trace) return false;   // 发布版 Trace 恒关
#endif
            if (level == LogLevel.Trace && !Verbose) return false;

            var sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++)
                if (level >= sinks[i].MinLevel) return true;
            return false;   // 全部 sink 的 MinLevel 都高于它 —— 记了也没人收
        }

        /// <summary>
        /// 接管 Unity 自己的日志流（<c>Application.logMessageReceivedThreaded</c>）：把**引擎报错、第三方包日志、
        /// 业务裸 <c>Debug.Log</c>、未捕获异常**统统灌进已注册的 sink。启动时开一次即可。
        /// </summary>
        /// <remarks>
        /// <b>为什么重要</b>：不开的话，<see cref="FileLogSink"/> 只收显式调用本门面的日志——玩家崩在一个
        /// <c>NullReferenceException</c> 上时，那条崩溃**根本不在日志文件里**，而它恰恰是最该捞到的东西。
        /// 开了之后，一行调用点都不用改，全量日志自动落盘 / 上报。<br/>
        /// <b>防回声</b>：桥接来的条目标记 <see cref="LogEntry.FromUnity"/>，<see cref="UnityDebugLogSink"/> 会跳过它们
        /// （这些日志 Console 里已经有了，再转发一次会重复刷屏并触发无限回环）；文件 / 遥测 sink 则照常收。
        /// </remarks>
        public static void CaptureUnityLogs(bool enabled = true) => UnityLogBridge.SetEnabled(enabled);

        // ── 便利门面 ───────────────────────────────────────────────────────
        // [HideInCallstack]：把这些转发方法从 Console 的调用栈里隐去，双击日志才会跳到**真正的调用点**
        // 而不是本文件——没有它，任何包一层 Debug.Log 的门面都会毁掉双击定位，这是此类封装最常见的死因。

        /// <summary>
        /// 诊断噪音（容器注册 / 解析、资源重试…）：仅 <see cref="Verbose"/> 开启且非 Release 构建时输出。
        /// </summary>
        /// <remarks>
        /// 发布版整个调用（含实参求值）被 <see cref="ConditionalAttribute"/> 从 IL 中删除，零成本。
        /// 带插值的用 <c>Log.Trace($"...")</c> 走 <see cref="TraceInterpolatedStringHandler"/> 重载——
        /// Verbose 关时连字符串都不拼。
        /// </remarks>
        [HideInCallstack]
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Trace(string message, string category = null, UnityEngine.Object context = null)
        {
            if (!Verbose) return;
            Dispatch(LogLevel.Trace, message, category, null, null, context, false, null);
        }

        /// <summary>
        /// 诊断噪音的**插值版**：<c>Log.Trace($"解析 {type.Name} 耗时 {ms}ms")</c>。
        /// Trace 没开时插值表达式**根本不求值**（详见 <see cref="TraceInterpolatedStringHandler"/>）。
        /// </summary>
        /// <remarks>
        /// ⚠ 参数里只放纯读取，<b>不要放有副作用的表达式</b>（<c>i++</c> 等）——级别没开时它们不会执行。
        /// </remarks>
        [HideInCallstack]
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Trace(TraceInterpolatedStringHandler message, string category = null, UnityEngine.Object context = null)
        {
            string text = message.GetTextOrNull();
            if (text == null) return;   // 级别没开——插值一个字符都没拼
            Dispatch(LogLevel.Trace, text, category, null, null, context, false, null);
        }

        /// <summary>常规信息（正常运行也该记的事：进入战斗、存档成功…）。始终广播给 sink。</summary>
        /// <param name="context">可选的关联 Unity 对象：Console 里点这条日志会高亮定位到它。</param>
        [HideInCallstack]
        public static void Info(string message, string category = null, UnityEngine.Object context = null)
            => Dispatch(LogLevel.Info, message, category, null, null, context, false, null);

        /// <summary>警告：非致命但值得注意（降级、可疑配置…）。</summary>
        [HideInCallstack]
        public static void Warning(string message, string category = null, UnityEngine.Object context = null)
            => Dispatch(LogLevel.Warning, message, category, null, null, context, false, null);

        /// <summary>
        /// 错误。未提供 <paramref name="exception"/> 时门面会自动抓取调用栈存入
        /// <see cref="LogEntry.StackTrace"/>——落盘的 error 若既无异常又无栈，事后基本无法定位。
        /// </summary>
        [HideInCallstack]
        public static void Error(string message, Exception exception = null, string category = null, UnityEngine.Object context = null)
            => Dispatch(LogLevel.Error, message, category, exception, null, context, false, null);

        /// <summary>
        /// 通用入口：可带结构化字段（供 JSON / 遥测等结构化 sink 消费，文本 sink 忽略）。
        /// 便利方法覆盖 99% 场景，要结构化时走这里，不必换 API。
        /// </summary>
        [HideInCallstack]
        public static void Write(
            LogLevel level,
            string message,
            IReadOnlyList<KeyValuePair<string, object>> fields = null,
            string category = null,
            Exception exception = null,
            UnityEngine.Object context = null)
        {
#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
            if (level == LogLevel.Trace) return;
#endif
            if (level == LogLevel.Trace && !Verbose) return;
            Dispatch(level, message, category, exception, fields, context, false, null);
        }

        // ── 广播 ───────────────────────────────────────────────────────────

        /// <summary>桥接入口：由 <see cref="UnityLogBridge"/> 把 Unity 日志流转进来（标记 <c>fromUnity</c>）。</summary>
        [HideInCallstack]
        internal static void DispatchFromUnity(LogLevel level, string message, string stackTrace)
            => Dispatch(level, message, "Unity", null, null, null, true, stackTrace);

        // [HideInCallstack] 必须标在**调用链上的每一层**，不能只标最外层门面：
        // Unity 是从 Debug.Log 那一帧往外走、跳过所有标了该特性的帧、停在**第一个没标的**帧上做双击定位。
        // 链条是 调用点 → Info/Warning/Error/Trace → Dispatch → UnityDebugLogSink.Log → Debug.Log，
        // 中间任何一层漏标，双击就会落进框架内部而不是业务的调用点。
        [HideInCallstack]
        private static void Dispatch(
            LogLevel level, string message, string category, Exception exception,
            IReadOnlyList<KeyValuePair<string, object>> fields, UnityEngine.Object context,
            bool fromUnity, string stackTrace)
        {
            var sinks = _sinks;   // 快照
            if (sinks.Length == 0) return;

            // error 且既没异常（异常自带栈）也没现成的栈（桥接条目由 Unity 传栈）→ 现抓一份。
            // 只对 Error 做：抓栈不便宜，而 Info/Warning 没栈也基本能靠消息定位。
            if (stackTrace == null && level == LogLevel.Error && exception == null)
                stackTrace = StackTraceUtility.ExtractStackTrace();

            var entry = new LogEntry(level, message, category, exception, fields, context, stackTrace, fromUnity);
            for (int i = 0; i < sinks.Length; i++)
            {
                var sink = sinks[i];
                if (level < sink.MinLevel) continue;
                try
                {
                    sink.Log(in entry);
                }
                catch (Exception e)
                {
                    // sink 故障不得扩散、也不能递归回本门面。
                    // 必须裹在 BeginEmit/EndEmit 里：否则这条 Debug.LogWarning 会被 Unity 桥接回调再抓回来 →
                    // 再广播 → 同一个坏 sink 再抛 → 无限递归。
                    UnityLogBridge.BeginEmit();
                    try { Debug.LogWarning($"[Log] sink {sink.GetType().Name} 抛异常：{e}"); }
                    finally { UnityLogBridge.EndEmit(); }
                }
            }
        }
    }
}
