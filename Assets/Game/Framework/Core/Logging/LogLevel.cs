namespace Game.Framework.Logging
{
    /// <summary>
    /// 框架日志级别。数值递增表示越重要，<see cref="ILogSink.MinLevel"/> 按 <c>&gt;=</c> 过滤。
    /// </summary>
    /// <remarks>
    /// <see cref="Trace"/> 是诊断噪音（注册/解析/重试等），受 <c>Log.MinLevel</c>（放行到 Trace）+ 仅 Editor/Development 输出双重门控
    /// （发布版整个调用被 <c>[Conditional]</c> 从 IL 删除）。<see cref="Info"/> 及以上始终广播给 sink，由各 sink 自行决定去向
    /// （默认 <see cref="UnityDebugLogSink"/> 转 <c>Debug.Log</c>，发布版 Warning/Error 照常进 player.log）。
    /// </remarks>
    public enum LogLevel
    {
        /// <summary>诊断噪音：仅 Verbose 开启且非 Release 构建时输出。</summary>
        Trace = 0,

        /// <summary>常规信息。</summary>
        Info = 1,

        /// <summary>警告：非致命但值得注意（降级、可疑配置等）。</summary>
        Warning = 2,

        /// <summary>错误：操作失败、异常，通常伴随 <c>Exception</c>。</summary>
        Error = 3,
    }
}
