#if UNITY_EDITOR
using Game.Framework.Logging;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 框架日志总闸门的编辑器会话持久化。人工入口收敛在“运行时诊断”窗口的日志栏。
    /// 设成 <see cref="LogLevel.Trace"/> 即打印容器注册 / 注入 / 资源初始化逐条 CDN 尝试等框架诊断噪音
    /// （早期俗称「开 Verbose」）。
    /// </summary>
    /// <remarks>
    /// 「Verbose」曾是一个独立布尔开关，sink + <c>MinLevel</c> 体系落地后被吸收进 <see cref="Log.MinLevel"/>
    /// （「Verbose=false」≡「总闸门 ≥ Info」，做的是同一件事）。<br/>
    /// 状态存 <see cref="SessionState"/>：仅本次 Editor 会话有效，<b>重启 Unity 自动归默认（Info）</b>，避免忘关后长期刷屏。
    /// 静态构造在每次域重载（编辑器加载 / 脚本重编译 / 进入 Play 前的域重载）里把会话值写回运行期静态属性，
    /// 因此 Play 模式也按它生效，且早于场景 <c>Awake</c>（资源初始化在那时才触发），能看到首次初始化的 CDN 日志。
    /// </remarks>
    [InitializeOnLoad]
    internal static class FrameworkLogMenu
    {
        private const string StateKey = "SSFramework.Log.MinLevel";

        static FrameworkLogMenu()
        {
            Log.MinLevel = (LogLevel)SessionState.GetInt(StateKey, (int)LogLevel.Info);
        }

        /// <summary>
        /// 设置本次 Editor 会话的全局最低日志级别，并立即同步运行时静态属性。
        /// </summary>
        internal static void SetMinLevel(LogLevel level)
        {
            SessionState.SetInt(StateKey, (int)level);
            Log.MinLevel = level;
        }
    }
}
#endif
