using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 统一配置表构建菜单 <c>SSFramework/配置表构建/*</c>——菜单只是交互外壳：
    /// 路径与目标读 <see cref="LubanConfigProfile"/>（单一真源），生成逻辑在 <see cref="LubanCodeGenerator"/>。
    /// 改完表定义（Defines/）或数据（Datas/）后执行「生成」，代码 / 数据 / 清单一次刷新。
    /// </summary>
    public static class LubanBuildMenu
    {
        private const string Root = "SSFramework/配置表构建/";

        [MenuItem(Root + "1. 生成配置代码 + 数据 (Luban)", priority = 1)]
        private static void Menu_Generate()
        {
            // 生成会改源码并触发重编译，Play 中执行会打断运行 / 产生半新半旧状态。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("配置表构建", "Play 模式下不能生成配置（会改代码触发重编译），请先停止运行。", "好");
                return;
            }

            var profile = LubanConfigProfile.Resolve();
            var (ok, message) = LubanCodeGenerator.Generate(profile);
            Debug.Log("[配置表构建] 生成：\n" + message);
            // CLI 全量输出可能很长，弹窗只放结论行，细节看 Console。
            string dialog = ok ? message.Split('\n')[0] + "\n\n详情见 Console。" : "生成失败，详情见 Console。";
            EditorUtility.DisplayDialog(ok ? "配置生成完成" : "配置生成失败", dialog, "好");
        }

        // ───────────── 配置与目录 ─────────────

        [MenuItem(Root + "配置 (Luban Profile)", priority = 20)]
        private static void Menu_SelectProfile()
        {
            var profile = LubanConfigProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        [MenuItem(Root + "打开目录/表定义与数据 (Configs)", priority = 40)]
        private static void Menu_OpenConfigs()
        {
            var profile = LubanConfigProfile.Resolve();
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(profile.ConfPath));
            EditorUtility.RevealInFinder(dir);
        }

        [MenuItem(Root + "打开目录/生成代码 (Gen)", priority = 41)]
        private static void Menu_OpenGenCode()
        {
            var profile = LubanConfigProfile.Resolve();
            EditorUtility.RevealInFinder(System.IO.Path.GetFullPath(profile.OutputCodeDir));
        }

        [MenuItem(Root + "打开目录/生成数据 (Res)", priority = 42)]
        private static void Menu_OpenGenData()
        {
            var profile = LubanConfigProfile.Resolve();
            EditorUtility.RevealInFinder(System.IO.Path.GetFullPath(profile.OutputDataDir));
        }
    }
}
