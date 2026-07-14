#if UNITY_EDITOR
using Game.Framework.Logging;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 框架日志的编辑器侧设置：全局 <see cref="Log.MinLevel"/>（总闸门）的会话持久化 + 菜单
    /// <c>SSFramework/诊断/Verbose 日志</c>——勾选 = 把总闸门放行到 <see cref="LogLevel.Trace"/>，
    /// 即打印容器注册 / 注入 / 资源初始化逐条 CDN 尝试等框架诊断噪音。
    /// </summary>
    /// <remarks>
    /// 「Verbose」**不再是一个独立开关**，只是「总闸门放行到 Trace」的俗称——早期确实有个 <c>Log.Verbose</c> 布尔，
    /// 但它与 sink 的 <c>MinLevel</c> 体系语义重叠（「Verbose=false」≡「所有 sink 的 MinLevel ≥ Info」），
    /// 并存会制造「sink 明明收 Trace 却怎么调都不出来」的陷阱，已收敛成单一的级别概念（见 <see cref="Log.MinLevel"/>）。
    /// 菜单名保留「Verbose 日志」是因为它就是大家嘴里那个开关；更细的档位（如全局压到 Warning）走诊断面板的下拉。<br/>
    /// 状态存 <see cref="SessionState"/>：仅本次 Editor 会话有效，<b>重启 Unity 自动归默认（Info）</b>，避免忘关后长期刷屏。
    /// 静态构造在每次域重载（编辑器加载 / 脚本重编译 / 进入 Play 前的域重载）里把会话值写回运行期静态字段，
    /// 因此 Play 模式也按它生效，且早于场景 <c>Awake</c>（资源初始化在那时才触发），能看到首次初始化的 CDN 日志。
    /// </remarks>
    [InitializeOnLoad]
    internal static class FrameworkLogMenu
    {
        private const string MenuPath = "SSFramework/诊断/Verbose 日志";
        private const string StateKey = "SSFramework.Log.MinLevel";

        static FrameworkLogMenu()
        {
            Log.MinLevel = (LogLevel)SessionState.GetInt(StateKey, (int)LogLevel.Info);
            // 域重载期间菜单尚未就绪，勾选状态延后一帧设置。
            EditorApplication.delayCall += () => Menu.SetChecked(MenuPath, IsVerbose);
        }

        /// <summary>「Verbose」= 总闸门已放行到 <see cref="LogLevel.Trace"/>。</summary>
        internal static bool IsVerbose => Log.MinLevel <= LogLevel.Trace;

        /// <summary>
        /// 设全局最低级别。**菜单与「框架诊断面板」共用这一个入口**——两处各自写状态必然会漂移
        /// （面板改了但菜单没打钩、或域重载后运行期字段没跟上），故收敛到一处：
        /// 同时写会话状态、运行期字段与菜单勾选。
        /// </summary>
        internal static void SetMinLevel(LogLevel level)
        {
            SessionState.SetInt(StateKey, (int)level);
            Log.MinLevel = level;
            Menu.SetChecked(MenuPath, level <= LogLevel.Trace);
        }

        // 勾上 = 放行到 Trace（看框架诊断噪音）；取消 = 回到 Info（日常默认）。
        // 若用户先在面板里把总闸门调到了 Warning/Error，这里取消勾选会落回 Info——可预期，
        // 不做「记住上一次是 Warning」的隐式行为（那种"聪明"的状态记忆最难排查）。
        [MenuItem(MenuPath)]
        private static void ToggleVerbose() => SetMinLevel(IsVerbose ? LogLevel.Info : LogLevel.Trace);

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            // 打开菜单时同步勾选状态，保证显示与实际一致（面板改过之后尤其重要）。
            Menu.SetChecked(MenuPath, IsVerbose);
            return true;
        }
    }
}
#endif
