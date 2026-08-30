using Game.Framework.Context;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// Mono 宿主的 Context 解析辅助。只负责“查找 + 可用性/归属预检”，不执行用户注入或 Container 写入；
    /// 具体基类据此把初始化组织成最后才发布的事务。
    /// </summary>
    internal static class MonoLayerExtensions
    {
        /// <summary>
        /// 为 Model/System/Utility 类层级解析目标 Context，并在任何用户回调前验证单一归属。
        /// 查找顺序：explicitContext → GetComponentInParent → GameContext.Main（全局兜底）。
        /// 三者都找不到才报错返回 null。
        /// </summary>
        internal static IGameContext ResolveLayerContext<TLayer>(
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

            // Mono 自动挂接同样先守住 Context 单一归属；否则重复挂接会先污染目标 Container，
            // 随后的 Inject 才发现实例仍属于旧 Context，留下无法自动回滚的半注册状态。
            ContextInternals.ValidateContextAffinity(contextProvider, self);
            return contextProvider;
        }

        /// <summary>
        /// View 不注册自己；这里只解析目标上下文，注入与资源绑定由 MonoViewBase 在可回滚边界内执行。
        /// 查找顺序：explicitContext → GetComponentInParent → GameContext.Main（全局兜底）。
        /// </summary>
        internal static IGameContext ResolveViewContext(
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
