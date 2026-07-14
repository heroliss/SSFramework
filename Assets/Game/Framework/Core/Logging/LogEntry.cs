using System;
using System.Collections.Generic;

namespace Game.Framework.Logging
{
    /// <summary>
    /// 一条日志的不可变载荷，由 <c>FrameworkLog</c> 门面构造、以 <c>in</c> 传给每个 <see cref="ILogSink"/>。
    /// </summary>
    /// <remarks>
    /// 设计为 <c>readonly struct</c> 且以 <c>in</c> 传递：广播到多个 sink 时不产生装箱 / 拷贝。<br/>
    /// <see cref="Fields"/> 是**可选**的结构化键值（给 ZLogger / JSON 等结构化 sink 用）——绝大多数日志不带，
    /// 此时为 <c>null</c>，热路径零额外分配；带结构化字段时才有列表分配（值 <c>object</c> 会装箱，属预期成本）。
    /// <see cref="Category"/> 供 sink 分类 / 过滤（如 ZLogger 的 category filter）；为空时约定用消息内前缀（<c>[Xxx]</c>）区分来源。
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

        public LogEntry(
            LogLevel level,
            string message,
            string category = null,
            Exception exception = null,
            IReadOnlyList<KeyValuePair<string, object>> fields = null)
        {
            Level = level;
            Message = message;
            Category = category;
            Exception = exception;
            Fields = fields;
            TimestampUtc = DateTime.UtcNow;
        }
    }
}
