using Game.Framework.Context;
using Game.Framework.Logging;
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
            var contextProvider = FindContext(self, explicitContext, typeof(TLayer).Name);
            if (contextProvider == null) return null;
            if (contextProvider.IsDisposed)
            {
                // 最近的 Context 已失败或正处于销毁阶段时必须在此止步。回退 Main 会把本应属于该作用域的
                // Mono 层悄悄注册到全局，继续 Attach 则只会把一个根初始化错误扩散成一串 NRE。
                Log.Trace($"[{typeof(TLayer).Name}] '{self.name}'：最近的 Context 不可用，已跳过挂接。");
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
            var contextProvider = FindContext(self, explicitContext, "View");
            if (contextProvider == null) return null;
            if (contextProvider.IsDisposed)
            {
                Log.Trace($"[View] '{self.name}'：最近的 Context 不可用，已跳过注入。");
                return null;
            }

            contextProvider.Inject(self);
            return contextProvider;
        }

        // 两条 Attach 路径共享的 Context 查找：explicitContext → Transform 父链最近的 MonoGameContextBase → GameContext.Main。
        // roleLabel 只影响日志前缀（层标记名 / "View"），便于定位是哪类组件没找到 Context。
        private static IGameContext FindContext(MonoBehaviour self, IGameContext explicitContext, string roleLabel)
        {
            IGameContext contextProvider = explicitContext
                ?? self.GetComponentInParent<MonoGameContextBase>(includeInactive: true);

            if (contextProvider == null && GameContext.Main != null)
            {
                Log.Trace($"[{roleLabel}] '{self.name}'：未找到父级 Context，回退到 GameContext.Main。");
                contextProvider = GameContext.Main;
            }

            if (contextProvider == null)
            {
                Log.Error(
                    $"未找到 '{self.name}' 可用的 IGameContext。" +
                    "请在 Inspector 设置 _targetContext、确保父级存在 MonoGameContextBase，" +
                    "或在本组件 Awake 前初始化 MonoGlobalContext。",
                    category: roleLabel,
                    context: self);
            }
            return contextProvider;
        }
    }
}
