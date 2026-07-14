#if UNITY_EDITOR
using Game.Framework.Logging;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 框架诊断日志开关（<see cref="Log.Verbose"/>）的编辑器菜单。
    ///
    /// 菜单 <c>SSFramework/诊断/Verbose 日志</c> 勾选即开启：打印容器注册 / 注入 / 资源初始化逐条 CDN 尝试等框架诊断输出。
    /// 取代「在代码里硬编码 <c>Verbose = true</c>」或「靠脚本宏 + 重编译」——勾一下即生效、随手开关、全项目复用。
    /// </summary>
    /// <remarks>
    /// 状态存 <see cref="SessionState"/>：仅本次 Editor 会话有效，<b>重启 Unity 自动归零</b>，避免忘关后长期刷屏。
    /// 静态构造在每次域重载（编辑器加载 / 脚本重编译 / 进入 Play 前的域重载）里把会话值重新写回运行期静态字段，
    /// 因此 Play 模式也按勾选状态生效，且早于场景 <c>Awake</c>（资源初始化在那时才触发），能看到首次初始化的 CDN 日志。
    /// </remarks>
    [InitializeOnLoad]
    internal static class FrameworkVerboseLogMenu
    {
        private const string MenuPath = "SSFramework/诊断/Verbose 日志";
        private const string StateKey = "SSFramework.Log.Verbose";

        static FrameworkVerboseLogMenu()
        {
            Log.Verbose = SessionState.GetBool(StateKey, false);
            // 域重载期间菜单尚未就绪，勾选状态延后一帧设置。
            EditorApplication.delayCall += () => Menu.SetChecked(MenuPath, Log.Verbose);
        }

        /// <summary>会话内的 Verbose 开关状态（真源是 <see cref="SessionState"/>，跨域重载存活）。</summary>
        internal static bool Verbose => SessionState.GetBool(StateKey, false);

        /// <summary>
        /// 设开关。**菜单与「框架诊断面板」共用这一个入口**——两处各自写状态必然会漂移
        /// （面板勾了但菜单没打钩、或域重载后运行期字段没跟上），故收敛到一处：
        /// 同时写会话状态、运行期字段与菜单勾选。
        /// </summary>
        internal static void SetVerbose(bool enabled)
        {
            SessionState.SetBool(StateKey, enabled);
            Log.Verbose = enabled;
            Menu.SetChecked(MenuPath, enabled);
        }

        [MenuItem(MenuPath)]
        private static void Toggle() => SetVerbose(!Verbose);

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            // 打开菜单时同步勾选状态，保证显示与实际一致（面板改过之后尤其重要）。
            Menu.SetChecked(MenuPath, Verbose);
            return true;
        }
    }
}
#endif
