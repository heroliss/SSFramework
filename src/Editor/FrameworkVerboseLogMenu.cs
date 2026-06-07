#if UNITY_EDITOR
using Game.Framework.Internal;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 框架诊断日志开关（<see cref="FrameworkLog.Verbose"/>）的编辑器菜单。
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
        private const string StateKey = "SSFramework.FrameworkLog.Verbose";

        static FrameworkVerboseLogMenu()
        {
            FrameworkLog.Verbose = SessionState.GetBool(StateKey, false);
            // 域重载期间菜单尚未就绪，勾选状态延后一帧设置。
            EditorApplication.delayCall += () => Menu.SetChecked(MenuPath, FrameworkLog.Verbose);
        }

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            bool enabled = !SessionState.GetBool(StateKey, false);
            SessionState.SetBool(StateKey, enabled);
            FrameworkLog.Verbose = enabled;
            Menu.SetChecked(MenuPath, enabled);
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            // 打开菜单时同步勾选状态，保证显示与实际一致。
            Menu.SetChecked(MenuPath, SessionState.GetBool(StateKey, false));
            return true;
        }
    }
}
#endif
