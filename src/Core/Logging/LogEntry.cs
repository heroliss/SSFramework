using System;
using System.Collections.Generic;

namespace Game.Framework.Logging
{
    /// <summary>
    /// 一条日志的不可变载荷，由 <see cref="Log"/> 门面构造、以 <c>in</c> 传给每个 <see cref="ILogSink"/>。
    /// </summary>
    /// <remarks>
    /// 设计为 <c>readonly struct</c> 且以 <c>in</c> 传递：广播到多个 sink 时不产生装箱 / 拷贝。<br/>
    /// <see cref="Fields"/> 是**可选**的结构化键值（给 JSON / 遥测等结构化 sink 用）——绝大多数日志不带，
    /// 此时为 <c>null</c>，热路径零额外分配。<br/>
    /// <see cref="Category"/> 供 sink 分类 / 过滤；为空时约定用消息内前缀（<c>[Xxx]</c>）区分来源。
    /// </remarks>
    public readonly struct LogEntry
    {
        /// <summary>级别。</summary>
        public readonly LogLevel Level;

        /// <summary>来源分类（可空）；供结构化 sink 分组 / 过滤。</summary>
        public readonly string Category;

        /// <summary>正文。</summary>
        public readonly string Message;

        /// <summary>关联异常（可空，通常伴随 <see cref="LogLevel.Error"/>）。</summary>
        public readonly Exception Exception;

        /// <summary>UTC 时间戳（构造时刻）。</summary>
        public readonly DateTime TimestampUtc;

        /// <summary>可选结构化键值（可空）；仅结构化 sink 消费，文本 sink 可忽略。</summary>
        public readonly IReadOnlyList<KeyValuePair<string, object>> Fields;

        /// <summary>
        /// 关联的 Unity 对象（可空）：Console 里点这条日志会高亮 / 定位到它，
        /// 等价 <c>Debug.Log(msg, context)</c> 的第二参。仅 <see cref="UnityDebugLogSink"/> 用得上，其余 sink 忽略。
        /// </summary>
        public readonly UnityEngine.Object Context;

        /// <summary>
        /// 调用栈文本（可空）。<see cref="LogLevel.Error"/> 且无 <see cref="Exception"/> 时由门面自动抓取——
        /// 落盘的 error 若既无异常又无栈，事后基本无法定位。文本 sink 应把它附在消息后。
        /// </summary>
        public readonly string StackTrace;

        /// <summary>
        /// 本条是否**由 Unity 日志流桥接而来**（引擎报错 / 第三方包 / 业务裸 <c>Debug.Log</c>，见 <see cref="Log.CaptureUnityLogs"/>）。
        /// </summary>
        /// <remarks>
        /// 它存在的唯一目的是**防回声**：这类日志 Unity Console 里**已经有了**，
        /// <see cref="UnityDebugLogSink"/> 必须跳过它，否则会再 <c>Debug.Log</c> 一次 → 触发桥接回调 → 无限循环 / 重复刷屏。
        /// 文件 / 遥测等其它 sink 则应照常收（这正是桥接的价值：引擎错误和第三方日志也能落盘）。
        /// </remarks>
        public readonly bool FromUnity;

        public LogEntry(
            LogLevel level,
            string message,
            string category = null,
            Exception exception = null,
            IReadOnlyList<KeyValuePair<string, object>> fields = null,
            UnityEngine.Object context = null,
            string stackTrace = null,
            bool fromUnity = false)
        {
            Level = level;
            Message = message;
            Category = category;
            Exception = exception;
            Fields = fields;
            Context = context;
            StackTrace = stackTrace;
            FromUnity = fromUnity;
            TimestampUtc = DateTime.UtcNow;
        }
    }
}
