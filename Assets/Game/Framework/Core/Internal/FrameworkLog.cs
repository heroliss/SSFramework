using System;
using System.Collections.Generic;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 框架日志门面：分级记录 + 广播到一组可插拔 <see cref="ILogSink"/>（Console / 文件 / ZLogger…）。
    /// 统一框架内所有诊断输出的入口——按模块过滤、落文件、测试捕获、遥测重定向都在这一层着力（ADR-0034）。
    /// </summary>
    /// <remarks>
    /// <b>为什么是静态门面而非 DI 服务</b>：日志要在**任何地方**可用，包括身处 DI 之下、没有 <c>Context</c> 的内核基础设施
    /// （<c>Container</c> / <c>InjectionPlan</c> / 构造期）——它们不能反向依赖容器去取 logger。<br/>
    /// <b>出厂即用</b>：默认装配一个 <see cref="UnityDebugLogSink"/>（转 <c>Debug.Log</c>，Console 观感 / 定位不变）。
    /// 启动时按需 <see cref="AddSink"/> 追加 <see cref="FileLogSink"/>（落盘）或 ZLogger 模块的结构化 sink。<br/>
    /// <b>级别语义</b>：<see cref="LogLevel.Trace"/>（含旧 <see cref="LogVerbose"/>）是诊断噪音——受 <see cref="Verbose"/> 开关
    /// + 仅 <c>UNITY_EDITOR || DEVELOPMENT_BUILD</c> 双重门控，发布版零成本、根本不到 sink；<see cref="LogLevel.Info"/> 及以上始终广播。<br/>
    /// 用法：代码 <c>FrameworkLog.Verbose = true</c> 临时开诊断；Editor 菜单 <c>SSFramework/诊断/Verbose 日志</c> 勾选（本会话有效）。
    /// </remarks>
    public static class FrameworkLog
    {
        /// <summary>是否放行 <see cref="LogLevel.Trace"/>（框架诊断噪音：注册/覆盖/解析/重试等）。默认关闭。</summary>
        public static bool Verbose = false;

        // sink 列表用 copy-on-write：广播（热路径）读快照无锁，仅增删时在锁内重建数组。
        // volatile 保证其它线程看到新数组引用；元素不就地修改，故引用级 volatile 足够。
        private static volatile ILogSink[] _sinks = { new UnityDebugLogSink() };
        private static readonly object _gate = new();

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

        // ── 便利门面 ──

        /// <summary>诊断噪音（旧 <see cref="LogVerbose"/> 的语义别名）：仅 Verbose 开启且非 Release 构建时输出。</summary>
        public static void Trace(string message, string category = null)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Verbose) Dispatch(LogLevel.Trace, message, category, null, null);
#endif
        }

        /// <summary>兼容旧调用点：等价 <see cref="Trace"/>（无 category）。</summary>
        public static void LogVerbose(string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Verbose) Dispatch(LogLevel.Trace, message, null, null, null);
#endif
        }

        /// <summary>常规信息。</summary>
        public static void Info(string message, string category = null)
            => Dispatch(LogLevel.Info, message, category, null, null);

        /// <summary>警告：非致命但值得注意。</summary>
        public static void Warning(string message, string category = null)
            => Dispatch(LogLevel.Warning, message, category, null, null);

        /// <summary>错误。</summary>
        public static void Error(string message, string category = null)
            => Dispatch(LogLevel.Error, message, category, null, null);

        /// <summary>错误 + 关联异常（默认 sink 会额外 <c>Debug.LogException</c> 保留堆栈定位）。</summary>
        public static void Error(string message, Exception exception, string category = null)
            => Dispatch(LogLevel.Error, message, category, exception, null);

        /// <summary>
        /// 通用入口：可带结构化字段（供 ZLogger / JSON 等结构化 sink 消费，文本 sink 忽略）。
        /// 便利方法内部都转发到这里。
        /// </summary>
        public static void Log(
            LogLevel level,
            string message,
            IReadOnlyList<KeyValuePair<string, object>> fields = null,
            string category = null,
            Exception exception = null)
        {
#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
            if (level == LogLevel.Trace) return; // 发布版 Trace 直接短路
#endif
            if (level == LogLevel.Trace && !Verbose) return;
            Dispatch(level, message, category, exception, fields);
        }

        // ── 广播 ──

        private static void Dispatch(
            LogLevel level, string message, string category,
            Exception exception, IReadOnlyList<KeyValuePair<string, object>> fields)
        {
            var sinks = _sinks; // 快照
            if (sinks.Length == 0) return;

            var entry = new LogEntry(level, message, category, exception, fields);
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
                    // sink 故障不得扩散、也不能递归回本门面——直接走 Debug。
                    Debug.LogWarning($"[FrameworkLog] sink {sink.GetType().Name} 抛异常：{e}");
                }
            }
        }
    }
}
