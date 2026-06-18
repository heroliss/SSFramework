using System;
using Game.Framework.Context;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 框架内部辅助：从 <see cref="IGameContext"/> 拿到底层 <see cref="Container"/>。
    /// <see cref="IGameContext"/> 接口故意不暴露 <c>Container</c>（避免业务绕过层标记直接 RegisterFor），
    /// 框架内部需要时通过此 helper 访问。<b>仅同程序集（含 InternalsVisibleTo 的 Test 程序集）可见。</b>
    /// </summary>
    internal static class ContextInternals
    {
        /// <summary>取 IGameContext 背后的 Container。未识别的实现抛 <see cref="InvalidOperationException"/>。</summary>
        internal static Container GetContainer(IGameContext ctx)
        {
            return ctx switch
            {
                GameContext gc => gc.Container,
                MonoGameContextBase mc => mc.Container,
                null => throw new ArgumentNullException(nameof(ctx)),
                _ => throw new InvalidOperationException(
                    $"[ContextInternals] Unknown IGameContext implementation: {ctx.GetType().Name}"),
            };
        }
    }
}
