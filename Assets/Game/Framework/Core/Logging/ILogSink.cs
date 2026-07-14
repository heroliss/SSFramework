namespace Game.Framework.Logging
{
    /// <summary>
    /// 日志去向的接缝：把一条 <see cref="LogEntry"/> 送到某个后端（Unity Console / 文件 / ZLogger / 遥测…）。
    /// </summary>
    /// <remarks>
    /// <c>FrameworkLog</c> 门面持有一组 sink 并**广播**——一条日志可同时进 Console + 落文件 + 上报。
    /// 注册 / 移除经 <c>FrameworkLog.AddSink / RemoveSink / ClearSinks</c>。<br/>
    /// <b>线程约定</b>：<see cref="Log"/> 可能从任意线程被调用（如网络接收循环在后台线程记日志），
    /// 实现若持有可变状态（文件句柄、缓冲）**必须自行加锁**（见 <see cref="FileLogSink"/>）。
    /// 门面对 sink 列表用 copy-on-write，广播本身无锁。<br/>
    /// 实现应吞掉自身异常、绝不让日志故障冒泡打断业务（sink 内部 try/catch）。
    /// </remarks>
    public interface ILogSink
    {
        /// <summary>本 sink 接收的最低级别；门面按 <c>entry.Level &gt;= MinLevel</c> 决定是否投递。</summary>
        LogLevel MinLevel { get; }

        /// <summary>投递一条日志。以 <c>in</c> 接收避免结构体拷贝；实现不得持有 <paramref name="entry"/> 引用逃逸。</summary>
        void Log(in LogEntry entry);
    }
}
