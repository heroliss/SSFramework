using System.Collections.Generic;
using System;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;

namespace Game.Framework.Config.Editor
{
    /// <summary>
    /// 配置表工作台的生成动作层：逐套生成工程内 profile；定位、打开目录与按钮说明由
    /// <see cref="LubanConfigOverviewWindow"/> 承担。
    /// 路径与目标读 <see cref="LubanConfigProfile"/>（每套一份），生成逻辑在 <see cref="LubanCodeGenerator"/>。
    /// </summary>
    public static class LubanBuildMenu
    {
        /// <summary>
        /// 逐套生成给定 profile——工作台的「生成全部 / 生成这套」共用入口。
        /// 先挡 Play 模式（即使差量事务保持产物一致，源码变化与刷新仍会重编译并破坏运行现场），再逐套调
        /// <see cref="LubanCodeGenerator.Generate"/>；CLI 全量输出合并进一条非阻塞 Console 结果。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<LubanConfigProfile> profiles)
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("Luban 配置生成")) return;
            if (profiles == null || profiles.Count == 0)
            {
                FrameworkEditorFeedback.Warn(
                    "Luban 配置生成未启动",
                    "影响：没有生成代码或数据。\n原因：工程里没有 LubanConfigProfile。\n" +
                    "下一步：打开 SSFramework/代码生成/配置表 (Luban)，新建或定位配置后重试。");
                return;
            }

            var allProfiles = LubanConfigProfile.ResolveAll().Concat(profiles).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = LubanCodeGenerator.ValidateOutputOwnership(allProfiles);
            if (!ownershipOk)
            {
                FrameworkEditorFeedback.ReportResult(
                    "Luban 生成预检", false,
                    ownershipMessage + "\n影响：所有配置均未开始生成，现有产物保持不变。");
                return;
            }

            var report = new StringBuilder();
            bool allOk = true;
            foreach (var profile in profiles)
            {
                bool ok;
                string message;
                try
                {
                    (ok, message) = LubanCodeGenerator.Generate(profile);
                }
                catch (Exception exception)
                {
                    ok = false;
                    message = $"发生未预期错误：{exception.GetType().Name}: {exception.Message}";
                }
                allOk &= ok;
                report.AppendLine($"【{profile.name}】{(ok ? "成功" : "失败")}");
                report.AppendLine(message);
                report.AppendLine();
            }

            FrameworkEditorFeedback.ReportResult(
                $"生成 Luban 配置（{profiles.Count} 套）",
                allOk,
                report.ToString());
        }
    }
}
