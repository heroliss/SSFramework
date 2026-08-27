using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 编辑器写入与生成操作的共享状态门禁。它只判断 Unity 当前是否适合启动操作；具体 Builder / Generator
    /// 仍需在入口再次调用，不能只依赖按钮禁用状态。
    /// </summary>
    public static class FrameworkEditorOperationGate
    {
        /// <summary>
        /// 无副作用检查 Unity 是否适合启动写入操作。失败时返回 <c>false</c> 并给出可展示原因；
        /// <paramref name="requireEditMode"/> 为 <c>true</c> 时 Play 及 Play 切换阶段也会被拒绝。
        /// </summary>
        public static bool CanStart(bool requireEditMode, out string reason)
        {
            if (EditorApplication.isCompiling)
            {
                reason = "Unity 正在编译脚本，请等待编译完成。";
                return false;
            }
            if (EditorApplication.isUpdating)
            {
                reason = "Unity 正在导入或刷新资源，请等待资源更新完成。";
                return false;
            }
            if (requireEditMode && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                reason = "当前处于 Play 或即将切换 Play；该操作会写项目或触发重编译。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// 执行动作入口的二次门禁。失败时通过统一反馈输出“未启动、内容未改变”和下一步，并返回 <c>false</c>；
        /// 成功只表示可开始，不捕获后续 Builder / Generator 的异常。
        /// </summary>
        public static bool EnsureCanStart(string operationName, bool requireEditMode = true)
        {
            if (CanStart(requireEditMode, out string reason)) return true;
            FrameworkEditorFeedback.Warn(
                operationName + "已阻止",
                $"影响：操作没有启动，项目内容未改变。\n原因：{reason}\n下一步：等待 Unity 空闲" +
                (requireEditMode ? "并停止 Play" : string.Empty) + "后重试。");
            return false;
        }
    }
}
