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
                    $"[ContextInternals] 未知的 IGameContext 实现：{ctx.GetType().Name}"),
            };
        }

        /// <summary>
        /// 在内部 Mono 自动注册写入 Container 前验证实例归属。公开 GameContext API 会自行执行同一检查；
        /// 这里避免 Mono 注册辅助直接访问 Container 时绕过 Context Affinity 事务边界。
        /// </summary>
        internal static void ValidateContextAffinity(IGameContext ctx, object target)
        {
            switch (ctx)
            {
                case GameContext gameContext:
                    gameContext.ValidateContextAffinity(target);
                    return;
                case MonoGameContextBase monoContext when monoContext.RawContext != null:
                    monoContext.RawContext.ValidateContextAffinity(target);
                    return;
                case null:
                    throw new ArgumentNullException(nameof(ctx));
                default:
                    throw new InvalidOperationException(
                        $"[ContextInternals] 未知或尚未初始化的 IGameContext 实现：{ctx.GetType().Name}");
            }
        }
    }
}
