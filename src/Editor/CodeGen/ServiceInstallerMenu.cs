using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 服务安装器生成的交互外壳：菜单「生成服务安装器代码」逐份生成工程内所有 <see cref="ServiceInstallerProfile"/>；
    /// 「配置总览」打开 <see cref="ServiceInstallerOverviewWindow"/>，按份定位 / 单独生成都在那；
    /// profile 资产 Inspector 上另有就地生成按钮（见 <see cref="ServiceInstallerProfileEditor"/>）。
    /// 生成逻辑全在 <see cref="ServiceInstallerGenerator"/>，这里只做定位与结果展示。
    /// </summary>
    public static class ServiceInstallerMenu
    {
        private const string Root = "SSFramework/服务注册/";

        // priority 跨度 ≥11 让 Unity 在「生成」与「配置总览」间自动插一条分割线（与配置表构建菜单同布局）。
        [MenuItem(Root + "生成服务安装器代码", priority = 1)]
        private static void Menu_GenerateAll()
        {
            var profiles = ServiceInstallerProfile.ResolveAll();
            if (profiles.Count == 0)
            {
                EditorUtility.DisplayDialog("没有找到配置",
                    "工程里没有 ServiceInstallerProfile 资产。\n\n" +
                    "经 Assets/Create/SSFramework/服务安装器配置 创建一个，配置「扫描目录 → 输出路径/命名空间」后再生成。",
                    "好");
                return;
            }
            GenerateProfiles(profiles);
        }

        [MenuItem(Root + "配置总览", priority = 20)]
        private static void Menu_Overview() => ServiceInstallerOverviewWindow.Open();

        /// <summary>
        /// 逐份生成给定 profile——菜单「生成服务安装器代码」、总览窗口与 Inspector 生成按钮共用入口。
        /// 先挡 Play 模式（生成改 .g.cs 会触发重编译、打断运行 / 产生半新半旧状态），再逐份调
        /// <see cref="ServiceInstallerGenerator.Generate"/>，汇总结果进 Console + 弹窗。
        /// </summary>
        internal static void GenerateProfiles(IReadOnlyList<ServiceInstallerProfile> profiles)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("服务安装器",
                    "Play 模式下不能生成（会改代码触发重编译），请先停止运行。", "好");
                return;
            }

            bool allOk = true;
            var sb = new StringBuilder();
            foreach (var profile in profiles)
            {
                var (ok, message) = ServiceInstallerGenerator.Generate(profile);
                allOk &= ok;
                if (sb.Length > 0) sb.AppendLine();
                if (profiles.Count > 1) sb.Append('【').Append(profile.name).AppendLine("】");
                sb.Append(message);
            }

            string summary = sb.ToString();
            Debug.Log("[服务安装器] 生成结果：\n" + summary);
            EditorUtility.DisplayDialog(allOk ? "生成完成" : "生成有失败项", summary, "好");
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
