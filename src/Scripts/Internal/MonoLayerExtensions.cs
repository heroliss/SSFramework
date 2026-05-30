using Game.Framework.Context;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// Mono 层注册辅助。把 MonoXxxBase Awake 里"查目标 Context + 注册 + 注入"的样板收敛到一处。
    /// </summary>
    internal static class MonoLayerExtensions
    {
        /// <summary>
        /// 注册并注入一个 Model/System/Utility 类层级。
        /// 查找顺序：explicitContext → GetComponentInParent → GameContext.Main（全局兜底）。
        /// 三者都找不到才报错返回 null。
        /// </summary>
        internal static IGameContext AttachLayer<TLayer>(
            this MonoBehaviour self,
            IGameContext explicitContext = null) where TLayer : class
        {
            IGameContext contextProvider = explicitContext
                ?? self.GetComponentInParent<MonoGameContextBase>(includeInactive: true);

            if (contextProvider == null && GameContext.Main != null)
            {
                FrameworkLog.LogVerbose(
                    $"[{typeof(TLayer).Name}] '{self.name}': no parent context, falling back to GameContext.Main.");
                contextProvider = GameContext.Main;
            }

            if (contextProvider == null)
            {
                Debug.LogError(
                    $"[{typeof(TLayer).Name}] No IGameContext found for '{self.name}'. " +
                    "Set _targetContext in Inspector, ensure a parent MonoGameContextBase exists, " +
                    "or initialize a MonoGlobalContext before this Awake.");
                return null;
            }

            ContextInternals.GetContainer(contextProvider).RegisterFor<TLayer>(self, $"{self.GetType().Name}({self.name})");
            contextProvider.Inject(self);
            return contextProvider;
        }

        /// <summary>
        /// View 不注册自己，只查目标上下文并执行 [Inject]。
        /// 查找顺序：explicitContext → GetComponentInParent → GameContext.Main（全局兜底）。
        /// </summary>
        internal static IGameContext AttachView(
            this MonoBehaviour self,
            IGameContext explicitContext = null)
        {
            IGameContext contextProvider = explicitContext
                ?? self.GetComponentInParent<MonoGameContextBase>(includeInactive: true);

            if (contextProvider == null && GameContext.Main != null)
            {
                FrameworkLog.LogVerbose(
                    $"[View] '{self.name}': no parent context, falling back to GameContext.Main.");
                contextProvider = GameContext.Main;
            }

            if (contextProvider == null)
            {
                Debug.LogError(
                    $"[View] No IGameContext found for '{self.name}'. " +
                    "Set _targetContext in Inspector, ensure a parent MonoGameContextBase exists, " +
                    "or initialize a MonoGlobalContext before this Awake.");
                return null;
            }

            contextProvider.Inject(self);
            return contextProvider;
        }
    }
}
