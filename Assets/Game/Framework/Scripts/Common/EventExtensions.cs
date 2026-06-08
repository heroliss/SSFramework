using System;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Internal;
using R3;

namespace Game.Framework.Common
{
    /// <summary>
    /// Event 事件系统扩展。包含发送（<see cref="SendEvent"/>）、监听（<see cref="RegisterEvent"/>）和 R3 桥接（<see cref="OnEvent"/>）。
    /// </summary>
    /// <remarks>
    /// <b>权限：</b>
    /// <list type="bullet">
    ///   <item>发送：<see cref="ICanSendEvent"/>——System。View 不能发，因为事件本质是数据层的瞬时通知（详见 framework-guide §1.3）。Command 不走本扩展，经 <c>ctx.SendEvent</c>。</item>
    ///   <item>监听：<see cref="ICanRegisterEvent"/>——View / System。Command 不监听事件（瞬时、不持订阅；<see cref="Game.Framework.Command.ICommandContext"/> 也刻意不提供 RegisterEvent）。</item>
    /// </list>
    /// <b>怎么用：</b>
    /// <list type="bullet">
    ///   <item><b>View/Mono*Base 内</b>优先用 <c>Bag.Subscribe&lt;T&gt;(handler)</c>——自动登记到 Bag，OnDestroy 释放。</item>
    ///   <item><b>纯 C# 单次订阅</b>用 <c>this.RegisterEvent&lt;T&gt;(handler)</c>，返回 <c>IDisposable</c>，调用方负责 Dispose。</item>
    ///   <item><b>需要 R3 操作符</b>（Prepend / Where / Throttle / CombineLatest）走 <c>this.OnEvent&lt;T&gt;()</c> 转 <see cref="Observable{T}"/>。</item>
    /// </list>
    /// <b>上下文解析：</b>通过 <see cref="IHasGameContext"/> 拿，要求 self 已附加到 Context。
    /// </remarks>
    public static class EventExtensions
    {
        public static void SendEvent<T>(this ICanSendEvent self, T evt = default) where T : IEvent
        {
            GameContext.ResolveFrom(self).SendEvent(evt);
        }

        public static IDisposable RegisterEvent<T>(this ICanRegisterEvent self, Action<T> handler) where T : IEvent
        {
            return GameContext.ResolveFrom(self).RegisterEvent(handler);
        }

        /// <summary>
        /// 把 Framework Event 桥接为 R3 <see cref="Observable{T}"/>，与 UnityEvent.AsObservable() / ReactiveProperty
        /// 形成统一的"Observable 入口"，可链式接所有 R3 操作符（Prepend / Where / Throttle / CombineLatest 等）。
        /// 订阅时初始化用 <c>this.OnEvent&lt;T&gt;().Prepend(new T { ... })</c>，比专门的 init 重载更通用。
        /// </summary>
        public static Observable<T> OnEvent<T>(this ICanRegisterEvent self) where T : IEvent
            => Observable.Create<T, ICanRegisterEvent>(self,
                (observer, source) => GameContext.ResolveFrom(source).RegisterEvent<T>(observer.OnNext));
    }
}
