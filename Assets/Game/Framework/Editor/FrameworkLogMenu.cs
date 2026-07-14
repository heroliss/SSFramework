#if UNITY_EDITOR
using Game.Framework.Logging;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 框架日志的编辑器侧设置：全局 <see cref="Log.MinLevel"/>（总闸门）的会话持久化 + 菜单
    /// <c>SSFramework/诊断/日志级别 ▸</c>——一个 4 档单选子菜单，勾中项 = 当前总闸门级别。
    /// 设成 <see cref="LogLevel.Trace"/> 即打印容器注册 / 注入 / 资源初始化逐条 CDN 尝试等框架诊断噪音
    /// （早期俗称「开 Verbose」）。
    /// </summary>
    /// <remarks>
    /// 「Verbose」曾是一个独立布尔开关，sink + <c>MinLevel</c> 体系落地后被吸收进 <see cref="Log.MinLevel"/>
    /// （「Verbose=false」≡「总闸门 ≥ Info」，做的是同一件事）；这里遂从二元开关升级为覆盖全 4 档的级别选择器，
    /// 与「框架诊断面板」顶部日志栏的下拉是**同一个控件的两处入口**（都经 <see cref="SetMinLevel"/> 写入）。<br/>
    /// 菜单存在的意义是**免开窗快速切级别**：调试时人在 Console 而非诊断面板，一键切档比「开面板→找下拉」省事。<br/>
    /// 状态存 <see cref="SessionState"/>：仅本次 Editor 会话有效，<b>重启 Unity 自动归默认（Info）</b>，避免忘关后长期刷屏。
    /// 静态构造在每次域重载（编辑器加载 / 脚本重编译 / 进入 Play 前的域重载）里把会话值写回运行期静态字段，
    /// 因此 Play 模式也按它生效，且早于场景 <c>Awake</c>（资源初始化在那时才触发），能看到首次初始化的 CDN 日志。
    /// </remarks>
    [InitializeOnLoad]
    internal static class FrameworkLogMenu
    {
        // 4 档单选。priority 连号让它们在子菜单里按 Trace→Error（严重度升序）排列、且成一组不被分隔线打断。
        private const string Root = "SSFramework/诊断/日志级别/";
        private const string PathTrace   = Root + "Trace (全部)";
        private const string PathInfo    = Root + "Info (默认)";
        private const string PathWarning = Root + "Warning";
        private const string PathError   = Root + "Error";
        private const string StateKey = "SSFramework.Log.MinLevel";

        static FrameworkLogMenu()
        {
            Log.MinLevel = (LogLevel)SessionState.GetInt(StateKey, (int)LogLevel.Info);
            // 域重载期间菜单尚未就绪，勾选状态延后一帧同步。
            EditorApplication.delayCall += SyncChecks;
        }

        /// <summary>
        /// 设全局最低级别。**菜单与「框架诊断面板」共用这一个入口**——两处各自写状态必然会漂移
        /// （面板改了但菜单没打钩、或域重载后运行期字段没跟上），故收敛到一处：
        /// 同时写会话状态、运行期字段与菜单单选勾选。
        /// </summary>
        internal static void SetMinLevel(LogLevel level)
        {
            SessionState.SetInt(StateKey, (int)level);
            Log.MinLevel = level;
            SyncChecks();
        }

        // 单选语义：仅当前级别打钩，其余清空。菜单打开时（validate）与每次写入后都调，保证与实际一致
        // ——面板改过级别之后，菜单勾选也得跟上。
        private static void SyncChecks()
        {
            var lv = Log.MinLevel;
            Menu.SetChecked(PathTrace,   lv == LogLevel.Trace);
            Menu.SetChecked(PathInfo,    lv == LogLevel.Info);
            Menu.SetChecked(PathWarning, lv == LogLevel.Warning);
            Menu.SetChecked(PathError,   lv == LogLevel.Error);
        }

        [MenuItem(PathTrace, priority = 20)]   private static void PickTrace()   => SetMinLevel(LogLevel.Trace);
        [MenuItem(PathInfo, priority = 21)]    private static void PickInfo()    => SetMinLevel(LogLevel.Info);
        [MenuItem(PathWarning, priority = 22)] private static void PickWarning() => SetMinLevel(LogLevel.Warning);
        [MenuItem(PathError, priority = 23)]   private static void PickError()   => SetMinLevel(LogLevel.Error);

        // validate 只负责在菜单打开时同步单选勾选（始终返回 true，四档恒可选）。
        [MenuItem(PathTrace, validate = true)]   private static bool ValidateTrace()   { SyncChecks(); return true; }
        [MenuItem(PathInfo, validate = true)]    private static bool ValidateInfo()    { SyncChecks(); return true; }
        [MenuItem(PathWarning, validate = true)] private static bool ValidateWarning() { SyncChecks(); return true; }
        [MenuItem(PathError, validate = true)]   private static bool ValidateError()   { SyncChecks(); return true; }
    }
}
#endif
