using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Context;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Systems
{
    /// <summary>
    /// 记录命令流水的 <see cref="ICommandSystem"/> 装饰器：每条命令（同步 / 异步、有无返回值）执行完成后
    /// 落一条记录到静态环形缓冲（类型、时刻、帧号、耗时、异常、Context 名），供编辑器「框架诊断面板」
    /// 展示最近命令时间线，或经 <c>echoToConsole</c> 逐条打进 Console（Development Build 真机排查）。
    /// </summary>
    /// <remarks>
    /// <b>接入（opt-in）</b>：根 Context 的 <c>InstallBindings</c> 里替换默认注册即可——
    /// <c>builder.RegisterValue(new LoggingCommandSystem(), typeof(ICommandSystem))</c>。
    /// 多个 Context 各自注册也共写同一条时间线（缓冲是静态的），可观察全局命令顺序。<br/>
    /// <b>装饰语义</b>：六个重载全部泛型直转发内层 dispatcher（默认 <see cref="CommandSystem"/>），
    /// struct Command 路径保持零装箱；只记 <c>typeof(T).Name</c>，<b>不</b>对命令调 <c>ToString()</c>（struct 会装箱）。
    /// 异常照原样冒出（记录后 rethrow），并遵守 <see cref="ICommandSystem"/> 的主线程完成契约。<br/>
    /// <b>落账时机</b>：完成时——同步命令执行返回即记；异步命令 await 完成（含异常 / 取消）后记，耗时才有意义；
    /// 在途异步不出现在流水里。<br/>
    /// <b>线程契约</b>：主线程独占，缓冲不加锁。即使自定义内层 dispatcher 在工作线程完成，
    /// 装饰器也会先切回 Unity 主线程，再落账并交付成功 / 异常 / 取消终态。
    /// </remarks>
    public sealed class LoggingCommandSystem : ICommandSystem
    {
        /// <summary>一条命令流水记录（完成时落账）。</summary>
        public readonly struct Entry
        {
            /// <summary>开始时刻（<c>Time.realtimeSinceStartup</c>）。</summary>
            public readonly float StartTime;

            /// <summary>开始帧号（<c>Time.frameCount</c>）。</summary>
            public readonly int Frame;

            /// <summary>命令类型短名。</summary>
            public readonly string CommandType;

            /// <summary>执行命令的 Context 诊断名（<see cref="GameContext.DebugName"/>；未命名为 <c>#哈希</c>）。</summary>
            public readonly string ContextName;

            /// <summary>从分发到完成的耗时（毫秒）；异步命令含全部 await 时间。</summary>
            public readonly float DurationMs;

            /// <summary>失败信息；null = 成功完成。取消显示「已取消」。</summary>
            public readonly string Error;

            /// <summary>是否异步命令（IAsyncCommand 系）。</summary>
            public readonly bool IsAsync;

            internal Entry(float startTime, int frame, string commandType, string contextName,
                float durationMs, string error, bool isAsync)
            {
                StartTime = startTime;
                Frame = frame;
                CommandType = commandType;
                ContextName = contextName;
                DurationMs = durationMs;
                Error = error;
                IsAsync = isAsync;
            }
        }

        /// <summary>环形缓冲容量：只保留最近这么多条，旧记录被覆盖。</summary>
        public const int Capacity = 256;

        private static readonly Entry[] _entries = new Entry[Capacity];
        private static long _total; // 累计落账数：写入槽位 = _total % Capacity，同时充当「有没有新记录」的版本号

        /// <summary>累计落账条数（含已被环形覆盖的）。读取端可用它判断「有没有新记录」以决定是否重绘。</summary>
        public static long TotalRecorded => _total;

        /// <summary>清空流水（累计计数归零）。</summary>
        public static void ClearLog() => _total = 0;

        /// <summary>
        /// 把最近至多 <paramref name="max"/> 条记录按时间顺序（旧 → 新）拷进 <paramref name="into"/>（先清空）。
        /// 拷贝语义：调用后再落账不影响已取出的列表。
        /// </summary>
        public static void CopyRecent(List<Entry> into, int max = Capacity)
        {
            into.Clear();
            long available = Math.Min(_total, Capacity);
            long take = Math.Min(max, available);
            for (long i = _total - take; i < _total; i++)
                into.Add(_entries[i % Capacity]);
        }

#if UNITY_EDITOR
        // 编辑器下每次进 Play 清空：关闭 Domain Reload 的 Enter Play Mode 设置下静态缓冲会跨会话残留，
        // 上一局的流水混进本局会误导排查。真机无此问题（进程级生命周期）。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlaySession() => _total = 0;
#endif

        private readonly ICommandSystem _inner;
        private readonly bool _echoToConsole;

        /// <param name="inner">被装饰的内层 dispatcher；null = 默认 <see cref="CommandSystem"/>。装饰器可继续嵌套。</param>
        /// <param name="echoToConsole">true 时每条记录同时经 <see cref="Log"/> 写一条 Info（默认关——缓冲照记，面板照看，不刷屏）。</param>
        public LoggingCommandSystem(ICommandSystem inner = null, bool echoToConsole = false)
        {
            _inner = inner ?? new CommandSystem();
            _echoToConsole = echoToConsole;
        }

        // ── 同步 ────────────────────────────────────────────────────────────

        public void ExecuteCommand<T>(T command, GameContext ctx) where T : ICommand
        {
            var p = Pending.Begin();
            try { _inner.ExecuteCommand(command, ctx); }
            catch (Exception e) { Record(typeof(T).Name, ctx, p, e, isAsync: false); throw; }
            Record(typeof(T).Name, ctx, p, null, isAsync: false);
        }

        public TResult ExecuteCommand<TResult>(ICommand<TResult> command, GameContext ctx)
        {
            var p = Pending.Begin();
            TResult result;
            try { result = _inner.ExecuteCommand(command, ctx); }
            catch (Exception e) { Record(command.GetType().Name, ctx, p, e, isAsync: false); throw; }
            Record(command.GetType().Name, ctx, p, null, isAsync: false);
            return result;
        }

        public TResult ExecuteCommand<T, TResult>(T command, GameContext ctx) where T : ICommand<TResult>
        {
            var p = Pending.Begin();
            TResult result;
            try { result = _inner.ExecuteCommand<T, TResult>(command, ctx); }
            catch (Exception e) { Record(typeof(T).Name, ctx, p, e, isAsync: false); throw; }
            Record(typeof(T).Name, ctx, p, null, isAsync: false);
            return result;
        }

        // ── 异步 ────────────────────────────────────────────────────────────
        // 内层调用本身可能同步抛（如 [Inject] 解析失败），与 await 阶段的异常分开捕获，两条路径都落账。

        public UniTask ExecuteCommandAsync<T>(T command, GameContext ctx, CancellationToken cancellationToken)
            where T : IAsyncCommand
        {
            var p = Pending.Begin();
            UniTask task;
            try { task = _inner.ExecuteCommandAsync(command, ctx, cancellationToken); }
            catch (Exception e) { Record(typeof(T).Name, ctx, p, e, isAsync: true); throw; }
            return Await(typeof(T).Name, ctx, p, task);
        }

        public UniTask<TResult> ExecuteCommandAsync<TResult>(IAsyncCommand<TResult> command, GameContext ctx, CancellationToken cancellationToken)
        {
            var p = Pending.Begin();
            UniTask<TResult> task;
            try { task = _inner.ExecuteCommandAsync(command, ctx, cancellationToken); }
            catch (Exception e) { Record(command.GetType().Name, ctx, p, e, isAsync: true); throw; }
            return Await(command.GetType().Name, ctx, p, task);
        }

        public UniTask<TResult> ExecuteCommandAsync<T, TResult>(T command, GameContext ctx, CancellationToken cancellationToken)
            where T : IAsyncCommand<TResult>
        {
            var p = Pending.Begin();
            UniTask<TResult> task;
            try { task = _inner.ExecuteCommandAsync<T, TResult>(command, ctx, cancellationToken); }
            catch (Exception e) { Record(typeof(T).Name, ctx, p, e, isAsync: true); throw; }
            return Await(typeof(T).Name, ctx, p, task);
        }

        private async UniTask Await(string commandType, GameContext ctx, Pending p, UniTask task)
        {
            Exception error = null;
            try { await task; }
            catch (Exception e) { error = e; throw; }
            finally
            {
                await UniTask.SwitchToMainThread();
                Record(commandType, ctx, p, error, isAsync: true);
            }
        }

        private async UniTask<TResult> Await<TResult>(string commandType, GameContext ctx, Pending p, UniTask<TResult> task)
        {
            Exception error = null;
            try { return await task; }
            catch (Exception e) { error = e; throw; }
            finally
            {
                await UniTask.SwitchToMainThread();
                Record(commandType, ctx, p, error, isAsync: true);
            }
        }

        // ── 落账 ────────────────────────────────────────────────────────────

        // 一次分发的起点快照。Stopwatch 时间戳算耗时（帧率无关的高精度单调钟）。
        private readonly struct Pending
        {
            public readonly float StartTime;
            public readonly int Frame;
            public readonly long StartTimestamp;

            private Pending(float startTime, int frame, long startTimestamp)
            {
                StartTime = startTime;
                Frame = frame;
                StartTimestamp = startTimestamp;
            }

            public static Pending Begin()
                => new(Time.realtimeSinceStartup, Time.frameCount, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        private void Record(string commandType, GameContext ctx, in Pending p, Exception error, bool isAsync)
        {
            float durationMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - p.StartTimestamp)
                                       * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            string errorText = error switch
            {
                null => null,
                OperationCanceledException => "已取消",
                _ => $"{error.GetType().Name}: {error.Message}",
            };
            string contextName = ctx.DebugName ?? $"#{ctx.GetHashCode():X}";

            _entries[_total % Capacity] = new Entry(p.StartTime, p.Frame, commandType, contextName, durationMs, errorText, isAsync);
            _total++;

            if (_echoToConsole)
                Log.Info(
                    $"[Command] {commandType} @{contextName} {(isAsync ? "async " : "")}{durationMs:F2}ms" +
                    (errorText != null ? $" ✗ {errorText}" : ""),
                    nameof(LoggingCommandSystem));
        }
    }
}
