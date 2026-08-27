using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;

namespace Game.Framework.Network.Proto.Editor
{
    /// <summary>
    /// Protobuf 工作台的生成动作层：逐套生成工程内 profile；定位、打开目录与按钮说明由
    /// <see cref="ProtoConfigOverviewWindow"/> 承担。
    /// 路径读 <see cref="ProtoConfigProfile"/>（每套一份），生成逻辑在 <see cref="ProtoCodeGenerator"/>。
    /// </summary>
    public static class ProtoBuildMenu
    {
        /// <summary>
        /// 逐套生成给定 profile——工作台的「生成全部 / 生成这套」共用入口。
        /// 先挡 Play 模式（生成改源码会触发重编译、打断运行 / 产生半新半旧状态），再逐套调
        /// <see cref="ProtoCodeGenerator.Generate"/>；protoc 全量输出合并进一条非阻塞 Console 结果。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<ProtoConfigProfile> profiles)
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("Protobuf 生成")) return;
            if (profiles == null || profiles.Count == 0)
            {
                FrameworkEditorFeedback.Warn(
                    "Protobuf 生成未启动",
                    "影响：没有生成代码。\n原因：工程里还没有 ProtoConfigProfile。\n" +
                    "下一步：打开 SSFramework/代码生成/Protobuf，点“新建 Profile…”并配置源 / 输出目录后重试。");
                return;
            }

            var allProfiles = ProtoConfigProfile.ResolveAll().Concat(profiles).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ProtoCodeGenerator.ValidateOutputOwnership(allProfiles);
            if (!ownershipOk)
            {
                FrameworkEditorFeedback.ReportResult(
                    "Protobuf 生成预检",
                    false,
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
                    (ok, message) = ProtoCodeGenerator.Generate(profile);
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
                $"生成 Protobuf 协议（{profiles.Count} 套）",
                allOk,
                report.ToString());
        }
    }
}
