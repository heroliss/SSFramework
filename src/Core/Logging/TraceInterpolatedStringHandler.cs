using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Game.Framework.Logging
{
    /// <summary>
    /// <see cref="Log.Trace(TraceInterpolatedStringHandler,string,UnityEngine.Object)"/> 的插值字符串处理器：
    /// 让 <c>Log.Trace($"...")</c> 在 <see cref="LogLevel.Trace"/> 没开时**连插值表达式都不求值**（真·零成本）。
    /// </summary>
    /// <remarks>
    /// <b>原理（编译期改写，不是运行时技巧）</b>：参数声明成本类型后，编译器把调用点
    /// <c>Log.Trace($"解析 {type.Name} 耗时 {ms}ms")</c> 改写成：
    /// <code><![CDATA[
    /// var h = new TraceInterpolatedStringHandler(12, 2, out bool shouldAppend);
    /// if (shouldAppend) {                 // ← 编译器自动插入的守卫
    ///     h.AppendLiteral("解析 ");
    ///     h.AppendFormatted(type.Name);   // ← Trace 没开时根本不执行
    ///     h.AppendLiteral(" 耗时 ");
    ///     h.AppendFormatted(ms);
    ///     h.AppendLiteral("ms");
    /// }
    /// Log.Trace(h);
    /// ]]></code>
    /// 构造函数把 <c>shouldAppend</c> 置为 <see cref="Log.IsEnabled"/> 的结果，于是级别没开时整个 <c>if</c> 块被跳过——
    /// 字符串一个字符都不拼、<c>ToString()</c> 一次都不调。对比之下，普通 <c>string</c> 参数是**先拼好再丢弃**。
    /// <br/><br/>
    /// ⚠ <b>求值语义会变（唯一需要守的纪律）</b>：级别没开时插值表达式不执行，因此参数里**只能放纯读取**
    /// （属性、<c>ToString()</c>、字符串拼接——这些正是要省掉的开销），<b>不要放有副作用的表达式</b>
    /// （<c>i++</c> / <c>list.Pop()</c> / <c>Interlocked.Increment</c>）：<c>Log.Trace($"值 {i++}")</c> 在
    /// Verbose 关时不会自增。这与手写 <c>if (Log.Verbose) Log.Trace(...)</c> 是**完全相同**的语义，
    /// 处理器只是把这个守卫自动化了；而"日志开不开会改变程序行为"本身就是 bug，故此语义是刻意的。
    /// <br/><br/>
    /// 依赖的两个 C# 10 attribute 由 <c>InterpolatedStringHandlerPolyfill.cs</c> 自带（Unity BCL 缺失）。
    /// </remarks>
    [InterpolatedStringHandler]
    public ref struct TraceInterpolatedStringHandler
    {
        // 线程私有的复用缓冲：日志可能来自任意线程（网络接收循环等），ThreadStatic 既免锁又免每条日志重新分配 StringBuilder。
        [ThreadStatic] private static StringBuilder _cached;

        // null = 本级别没开。此时编译器根本不会调用任何 Append*，故所有方法可直接解引用而不必判空。
        private readonly StringBuilder _sb;

        /// <param name="literalLength">字面量总长度（编译器传入）。</param>
        /// <param name="formattedCount">插值洞的个数（编译器传入）。</param>
        /// <param name="shouldAppend">
        /// 回传给编译器：false 时它会跳过全部 <c>Append*</c> 调用——惰性求值就在这里发生。
        /// </param>
        public TraceInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            shouldAppend = Log.IsEnabled(LogLevel.Trace);
            if (!shouldAppend)
            {
                _sb = null;
                return;
            }

            var sb = _cached ??= new StringBuilder(256);
            sb.Clear();
            sb.EnsureCapacity(literalLength + formattedCount * 8);
            _sb = sb;
        }

        public void AppendLiteral(string value) => _sb.Append(value);

        public void AppendFormatted<T>(T value) => _sb.Append(value?.ToString());

        public void AppendFormatted<T>(T value, string format)
            => _sb.Append(value is IFormattable f ? f.ToString(format, null) : value?.ToString());

        public void AppendFormatted<T>(T value, int alignment)
            => AppendFormatted(value, alignment, null);

        public void AppendFormatted<T>(T value, int alignment, string format)
        {
            string text = value is IFormattable f ? f.ToString(format, null) : value?.ToString();
            text ??= string.Empty;
            int pad = Math.Abs(alignment) - text.Length;
            if (pad <= 0) { _sb.Append(text); return; }
            if (alignment > 0) _sb.Append(' ', pad).Append(text);   // 正数右对齐
            else _sb.Append(text).Append(' ', pad);                 // 负数左对齐
        }

        /// <summary>取出拼好的文本；级别没开时返回 <c>null</c>（调用方据此短路，连 Dispatch 都不进）。</summary>
        internal string GetTextOrNull() => _sb?.ToString();
    }
}
