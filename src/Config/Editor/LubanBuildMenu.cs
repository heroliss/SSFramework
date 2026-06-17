using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 配置表构建菜单 <c>SSFramework/配置表构建/*</c>——只有两项：「生成全部」逐套生成工程内所有 profile；
    /// 「配置总览」打开 <see cref="LubanConfigOverviewWindow"/>，按各套定位 / 打开目录 / 单独生成都在那。
    /// 路径与目标读 <see cref="LubanConfigProfile"/>（每套一份），生成逻辑在 <see cref="LubanCodeGenerator"/>。
    /// </summary>
    public static class LubanBuildMenu
    {
        private const string Root = "SSFramework/配置表构建/";

        // priority 跨度 ≥11 让 Unity 在「生成全部」与「配置总览」间自动插一条分割线。
        [MenuItem(Root + "生成全部 (代码 + 数据)", priority = 1)]
        private static void Menu_Generate() => GenerateProfiles(LubanConfigProfile.ResolveAll());

        [MenuItem(Root + "配置总览 (定位 · 打开目录 · 生成)", priority = 20)]
        private static void Menu_Overview() => LubanConfigOverviewWindow.Open();

        /// <summary>
        /// 逐套生成给定 profile——菜单「生成全部」与总览窗口的「生成全部 / 生成这套」共用入口。
        /// 先挡 Play 模式（生成改源码会触发重编译、打断运行 / 产生半新半旧状态），再逐套调
        /// <see cref="LubanCodeGenerator.Generate"/>；CLI 全量输出进 Console，弹窗只给每套成败结论。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<LubanConfigProfile> profiles)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("配置表构建", "Play 模式下不能生成配置（会改代码触发重编译），请先停止运行。", "好");
                return;
            }

            var report = new StringBuilder();
            bool allOk = true;
            foreach (var profile in profiles)
            {
                var (ok, message) = LubanCodeGenerator.Generate(profile);
                allOk &= ok;
                report.AppendLine($"【{profile.name}】{(ok ? "成功" : "失败")}");
                report.AppendLine(message);
                report.AppendLine();
            }

            Debug.Log("[配置表构建] 生成：\n" + report);
            string dialog = allOk ? $"已生成 {profiles.Count} 套配置。\n\n详情见 Console。" : "部分配置生成失败，详情见 Console。";
            EditorUtility.DisplayDialog(allOk ? "配置生成完成" : "配置生成失败", dialog, "好");
        }
    }
}
