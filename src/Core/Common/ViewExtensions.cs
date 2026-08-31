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
    /// 异步入口始终包含 Context 生命周期。无参或显式传 <see cref="CancellationToken.None"/> 时，
    /// <see cref="MonoBehaviour"/> View 还会自动包含销毁令牌，纯 C# View 则只包含 Context；
    /// 显式传入可取消 token 时，它作为 View 侧的<b>生命周期覆盖</b>替代 Mono 销毁令牌，但不会替代 Context。
    /// 命令实现只需关心收到的单一 <c>cancellationToken</c>，不必再访问 <c>ctx.CancellationToken</c>。
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

        /// <summary>
        /// 异步执行 Command。总是跟随 Context；若 <paramref name="self"/> 是 <see cref="MonoBehaviour"/>，
        /// 还会自动跟随其销毁生命周期。纯 C# View 如需更短生命周期，应使用显式 token 重载。
        /// </summary>
        public static async UniTask ExecuteCommandAsync<T>(this ICanSendCommand self, T command) where T : IAsyncCommand
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, default);
            await ctx.ExecuteCommandAsync(command, link.Token);
        }

        /// <summary>
        /// 带调用方生命周期覆盖的异步 Command 执行。Context 生命周期始终保留；当
        /// <paramref name="cancellationToken"/> 可取消时，它替代 Mono View 的自动销毁令牌，而不是再追加第三个令牌。
        /// 传 <see cref="CancellationToken.None"/> / <c>default</c> 等同无参重载，仍采用 Mono 销毁默认值。
        /// </summary>
        public static async UniTask ExecuteCommandAsync<T>(this ICanSendCommand self, T command, CancellationToken cancellationToken) where T : IAsyncCommand
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, cancellationToken);
            await ctx.ExecuteCommandAsync(command, link.Token);
        }

        /// <summary>
        /// 异步执行接口形式的带返回值 Command。总是跟随 Context；Mono View 无参时还自动跟随销毁生命周期。
        /// </summary>
        public static async UniTask<TResult> ExecuteCommandAsync<TResult>(this ICanSendCommand self, IAsyncCommand<TResult> command)
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, default);
            return await ctx.ExecuteCommandAsync(command, link.Token);
        }

        /// <summary>
        /// 带调用方生命周期覆盖的接口形式带返回值 Command。可取消的显式 token 替代 Mono 销毁令牌，
        /// Context 生命周期始终保留；<see cref="CancellationToken.None"/> / <c>default</c> 仍走 Mono 销毁默认值。
        /// </summary>
        public static async UniTask<TResult> ExecuteCommandAsync<TResult>(this ICanSendCommand self, IAsyncCommand<TResult> command, CancellationToken cancellationToken)
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, cancellationToken);
            return await ctx.ExecuteCommandAsync(command, link.Token);
        }

        /// <summary>
        /// 异步执行双泛型带返回值 Command。总是跟随 Context；Mono View 无参时还自动跟随销毁生命周期。
        /// </summary>
        public static async UniTask<TResult> ExecuteCommandAsync<T, TResult>(this ICanSendCommand self, T command) where T : IAsyncCommand<TResult>
        {
            var ctx = GameContext.ResolveFrom(self);
            using var link = LinkExecutionToken(self, ctx, default);
            return await ctx.ExecuteCommandAsync<T, TResult>(command, link.Token);
        }

        /// <summary>
        /// 带调用方生命周期覆盖的双泛型带返回值 Command。可取消的显式 token 替代 Mono 销毁令牌，
        /// Context 生命周期始终保留；<see cref="CancellationToken.None"/> / <c>default</c> 仍走 Mono 销毁默认值。
        /// </summary>
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
        ///   <item>若 <paramref name="external"/> 可被取消，它就是调用方选择的 View 侧生命周期覆盖；包含它且不再附加 View 销毁令牌。</item>
        ///   <item><see cref="CancellationToken.None"/> / <c>default</c> 不构成覆盖，仍按无参规则选择 Mono 销毁令牌。</item>
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
