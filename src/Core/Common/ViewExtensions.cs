using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Command;
using UnityEngine;

namespace Game.Framework.Common
{
    /// <summary>
    /// View 层扩展方法，View 只能发送 Command。
    /// 上下文通过 <see cref="GameContext.ResolveFrom"/> 解析。
    /// </summary>
    /// <remarks>
    /// 异步重载会自动合并多重生命周期为单一 <see cref="CancellationToken"/>，命令实现
    /// 内部只需关心传入的 <c>cancellationToken</c>，不必再访问 <c>ctx.CancellationToken</c>。
    /// 合并规则见 <see cref="LinkExecutionToken"/>。
    /// </remarks>
    public static class ViewExtensions
    {
        public static void ExecuteCommand<T>(this ICanSendCommand self, T command) where T : ICommand
        {
            GameContext.ResolveFrom(self).ExecuteCommand(command);
        }

        public static TResult ExecuteCommand<TResult>(this ICanSendCommand self, ICommand<TResult> command)
        {
            return GameContext.ResolveFrom(self).ExecuteCommand(command);
        }

        public static TResult ExecuteCommand<T, TResult>(this ICanSendCommand self, T command) where T : ICommand<TResult>
        {
            return GameContext.ResolveFrom(self).ExecuteCommand<T, TResult>(command);
        }

        public static async UniTask ExecuteCommandAsync<T>(this ICanSendCommand self, T command) where T : IAsyncCommand
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, default);
            await ctx.ExecuteCommandAsync(command, link.Token);
        }

        /// <summary>
        /// 带调用方取消令牌的异步 Command 执行。<paramref name="cancellationToken"/> 与 Context 生命周期令牌
        /// 链接为单一 token，任意一方取消即中止命令。典型用法见 <see cref="LinkExecutionToken"/>。
        /// </summary>
        public static async UniTask ExecuteCommandAsync<T>(this ICanSendCommand self, T command, CancellationToken cancellationToken) where T : IAsyncCommand
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, cancellationToken);
            await ctx.ExecuteCommandAsync(command, link.Token);
        }

        public static async UniTask<TResult> ExecuteCommandAsync<TResult>(this ICanSendCommand self, IAsyncCommand<TResult> command)
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, default);
            return await ctx.ExecuteCommandAsync(command, link.Token);
        }

        public static async UniTask<TResult> ExecuteCommandAsync<TResult>(this ICanSendCommand self, IAsyncCommand<TResult> command, CancellationToken cancellationToken)
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, cancellationToken);
            return await ctx.ExecuteCommandAsync(command, link.Token);
        }

        public static async UniTask<TResult> ExecuteCommandAsync<T, TResult>(this ICanSendCommand self, T command) where T : IAsyncCommand<TResult>
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, default);
            return await ctx.ExecuteCommandAsync<T, TResult>(command, link.Token);
        }

        public static async UniTask<TResult> ExecuteCommandAsync<T, TResult>(this ICanSendCommand self, T command, CancellationToken cancellationToken) where T : IAsyncCommand<TResult>
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, cancellationToken);
            return await ctx.ExecuteCommandAsync<T, TResult>(command, link.Token);
        }

        /// <summary>
        /// 按以下规则合并异步 Command 执行的取消令牌，返回的 <see cref="LinkedExecutionToken"/>
        /// 用 <c>using</c> 释放底层 <see cref="CancellationTokenSource"/>：
        /// <list type="bullet">
        ///   <item>总是包含 <c>ctx.CancellationToken</c>（Context 生命周期）。</item>
        ///   <item>若 <paramref name="external"/> 可被取消，包含 <paramref name="external"/>，此时不再附加 View 销毁令牌。</item>
        ///   <item>若 <paramref name="external"/> 不可取消且 <paramref name="self"/> 是 <see cref="MonoBehaviour"/>，附加 <c>GetCancellationTokenOnDestroy()</c>。</item>
        ///   <item>若合并后只剩 <c>ctx.CancellationToken</c>，直接返回该 token，不分配 CTS。</item>
        /// </list>
        /// </summary>
        private static LinkedExecutionToken LinkExecutionToken(ICanSendCommand self, IGameContext ctx, CancellationToken external)
        {
            if (external.CanBeCanceled)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken, external);
                return new LinkedExecutionToken(cts);
            }

            if (self is MonoBehaviour mono)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken, mono.GetCancellationTokenOnDestroy());
                return new LinkedExecutionToken(cts);
            }

            return new LinkedExecutionToken(ctx.CancellationToken);
        }

        /// <summary>
        /// 异步 Command 执行的合并令牌。可能持有底层 <see cref="CancellationTokenSource"/>（需 Dispose），
        /// 也可能仅持有 token（无 CTS 分配的 fast path）。
        /// </summary>
        private readonly struct LinkedExecutionToken : IDisposable
        {
            private readonly CancellationTokenSource _cts;
            public CancellationToken Token { get; }

            public LinkedExecutionToken(CancellationTokenSource cts)
            {
                _cts = cts;
                Token = cts.Token;
            }

            public LinkedExecutionToken(CancellationToken token)
            {
                _cts = null;
                Token = token;
            }

            public void Dispose() => _cts?.Dispose();
        }
    }
}
