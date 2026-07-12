using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

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
        /// <see cref="ProtoCodeGenerator.Generate"/>；protoc 全量输出进 Console，弹窗只给每套成败结论。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<ProtoConfigProfile> profiles)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Protobuf 生成", "Play 模式下不能生成（会改代码触发重编译），请先停止运行。", "好");
                return;
            }
            if (profiles.Count == 0)
            {
                EditorUtility.DisplayDialog("Protobuf 生成",
                    "工程里还没有 Proto profile。\n经 Assets/Create/SSFramework/Protobuf 生成配置 创建，" +
                    "或打开「配置总览」窗口新建。", "好");
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

            Debug.Log("[Protobuf 生成] 生成：\n" + report);
            string dialog = allOk ? $"已生成 {profiles.Count} 套协议代码。\n\n详情见 Console。" : "部分协议生成失败，详情见 Console。";
            EditorUtility.DisplayDialog(allOk ? "Protobuf 生成完成" : "Protobuf 生成失败", dialog, "好");
        }
    }
}
