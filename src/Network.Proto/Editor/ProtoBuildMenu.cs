using System.Collections.Generic;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;

namespace Game.Framework.Network.Proto.Editor
{
    /// <summary>
    /// Protobuf 协议生成菜单 <c>SSFramework/Protobuf/*</c>——只有两项：「生成全部」逐套生成工程内所有 profile；
    /// 「配置总览」打开 <see cref="ProtoConfigOverviewWindow"/>，按各套定位 / 打开目录 / 单独生成 / 新建都在那。
    /// 路径读 <see cref="ProtoConfigProfile"/>（每套一份），生成逻辑在 <see cref="ProtoCodeGenerator"/>。
    /// </summary>
    public static class ProtoBuildMenu
    {
        private const string Root = "SSFramework/Protobuf/";

        // priority 跨度 ≥11 让 Unity 在「生成全部」与「配置总览」间自动插一条分割线。
        [MenuItem(Root + "生成全部 (.proto → C#)", priority = 1)]
        private static void Menu_Generate() => GenerateProfiles(ProtoConfigProfile.ResolveAll());

        [MenuItem(Root + "配置总览 (定位 · 打开目录 · 生成)", priority = 20)]
        private static void Menu_Overview() => ProtoConfigOverviewWindow.Open();

        /// <summary>
        /// 逐套生成给定 profile——菜单「生成全部」与总览窗口的「生成全部 / 生成这套」共用入口。
        /// 先挡 Play 模式（生成改源码会触发重编译、打断运行 / 产生半新半旧状态），再逐套调
        /// <see cref="ProtoCodeGenerator.Generate"/>；protoc 全量输出合并进一条非阻塞 Console 结果。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<ProtoConfigProfile> profiles)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                FrameworkEditorFeedback.Warn(
                    "Protobuf 生成已阻止",
                    "影响：没有生成代码。\n原因：Play 模式下生成会改源码并触发重编译。\n下一步：停止 Play 后重试。");
                return;
            }
            if (profiles.Count == 0)
            {
                FrameworkEditorFeedback.Warn(
                    "Protobuf 生成未启动",
                    "影响：没有生成代码。\n原因：工程里还没有 ProtoConfigProfile。\n" +
                    "下一步：打开 SSFramework/Protobuf/配置总览，点“新建 Profile…”并配置源 / 输出目录后重试。");
                return;
            }

            var report = new StringBuilder();
            bool allOk = true;
            foreach (var profile in profiles)
            {
                var (ok, message) = ProtoCodeGenerator.Generate(profile);
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
