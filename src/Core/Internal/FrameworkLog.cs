using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 框架内部日志开关。统一控制 MonoGameContextBase / Container / InjectionPlan
    /// 等基础设施的诊断输出，避免散落在多个组件的 SerializeField 中。
    ///
    /// 用法：
    /// - 代码：`FrameworkLog.Verbose = true;` 临时开启
    /// - Editor：菜单 SSFramework/诊断/Verbose 日志 勾选开关（本次 Editor 会话有效）
    ///
    /// 仅在 UNITY_EDITOR || DEVELOPMENT_BUILD 下产生日志；发布版直接编译期消除。
    /// </summary>
    public static class FrameworkLog
    {
        /// <summary>是否打印框架诊断日志（注册/覆盖/解析等）。默认关闭。</summary>
        public static bool Verbose = false;

        /// <summary>仅在开启 Verbose 且非 Release 构建下打印。</summary>
        public static void LogVerbose(string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Verbose) Debug.Log(message);
#endif
        }
    }
}
