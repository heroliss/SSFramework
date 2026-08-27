using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 服务安装器生成的动作层：逐份生成工程内所有 <see cref="ServiceInstallerProfile"/>；
    /// <see cref="ServiceInstallerOverviewWindow"/> 负责按份定位、说明与触发；
    /// profile 资产 Inspector 上另有就地生成按钮（见 <see cref="ServiceInstallerProfileEditor"/>）。
    /// 生成逻辑全在 <see cref="ServiceInstallerGenerator"/>，这里只做定位与结果展示。
    /// </summary>
    public static class ServiceInstallerMenu
    {
        /// <summary>
        /// 逐份生成给定 profile——工作台与 Inspector 的生成按钮共用入口。
        /// 先挡 Play 模式（生成改 .g.cs 会触发重编译、打断运行 / 产生半新半旧状态），再逐份调
        /// <see cref="ServiceInstallerGenerator.Generate"/>，汇总为一条带稳定严重级别的非阻塞结果。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<ServiceInstallerProfile> profiles)
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("服务安装器生成")) return;
            if (profiles == null || profiles.Count == 0)
            {
                FrameworkEditorFeedback.Warn(
                    "服务安装器生成未启动",
                    "影响：没有生成代码。\n原因：工程里还没有 ServiceInstallerProfile。\n" +
                    "下一步：打开 SSFramework/代码生成/服务安装器，创建并填写扫描与输出配置后重试。");
                return;
            }

            var allProfiles = ServiceInstallerProfile.ResolveAll().Concat(profiles).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ServiceInstallerGenerator.ValidateOutputOwnership(allProfiles);
            if (!ownershipOk)
            {
                FrameworkEditorFeedback.ReportResult(
                    "服务安装器生成预检", false,
                    ownershipMessage + "\n影响：所有配置均未开始生成，现有产物保持不变。");
                return;
            }

            bool allOk = true;
            var sb = new StringBuilder();
            foreach (var profile in profiles)
            {
                bool ok;
                string message;
                try
                {
                    (ok, message) = ServiceInstallerGenerator.Generate(profile);
                }
                catch (Exception exception)
                {
                    ok = false;
                    message = $"发生未预期错误：{exception.GetType().Name}: {exception.Message}";
                }
                allOk &= ok;
                if (sb.Length > 0) sb.AppendLine();
                if (profiles.Count > 1) sb.Append('【').Append(profile.name).AppendLine("】");
                sb.Append(message);
            }

            string summary = sb.ToString();
            FrameworkEditorFeedback.ReportResult("生成服务安装器", allOk, summary);
        }
    }

    /// <summary>profile 的 Inspector：默认字段之外补一个就地生成按钮（与构建 profile 的交互习惯一致）。</summary>
    [CustomEditor(typeof(ServiceInstallerProfile))]
    public sealed class ServiceInstallerProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            GUILayout.Space(8);
            if (GUILayout.Button("生成服务安装器代码", GUILayout.Height(28)))
                ServiceInstallerMenu.GenerateProfiles(new[] { (ServiceInstallerProfile)target });
        }
    }
}
