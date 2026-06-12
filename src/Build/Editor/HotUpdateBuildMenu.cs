using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// 统一热更构建菜单 <c>SSFramework/热更构建/*</c>——菜单只是交互外壳：
    /// 配置读 <see cref="FrameworkHotUpdateProfile"/>（单一真源），校验/排序在 <see cref="HotUpdateAssemblyGraph"/>。
    /// 改了热更列表后必须执行「同步热更设置」——HybridCLR 的 Generate / CompileDll 读的是 HybridCLRSettings，
    /// 同步就是把单一真源派生过去（不要去 HybridCLR Settings 手填）。
    /// </summary>
    public static class HotUpdateBuildMenu
    {
        private const string Root = "SSFramework/热更构建/";

        [MenuItem(Root + "1. 同步热更设置（校验 + 写入 HybridCLRSettings）", priority = 1)]
        private static void Menu_Sync()
        {
            var profile = FrameworkHotUpdateProfile.Resolve();
            string summary = profile.SyncToHybridCLRSettings();
            Debug.Log("[热更构建] 同步热更设置：\n" + summary);
            EditorUtility.DisplayDialog("同步热更设置", summary, "好");
        }

        [MenuItem(Root + "2. 生成桥接与裁剪文件（Generate All，慢）", priority = 2)]
        private static void Menu_Generate()
        {
            // Generate 内部要跑迷你构建（产裁剪 AOT DLL），与资源构建同样要求 Edit 模式 + 场景已存。
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var profile = FrameworkHotUpdateProfile.Resolve();
            var (ok, message) = FrameworkHotUpdateBuilder.Generate(profile);
            Debug.Log("[热更构建] Generate：\n" + message);
            EditorUtility.DisplayDialog(ok ? "Generate 完成" : "Generate 失败", message, "好");
        }

        [MenuItem(Root + "3. 构建代码包（CompileDll → RawFile 包）", priority = 3)]
        private static void Menu_BuildCodePackage()
        {
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var profile = FrameworkHotUpdateProfile.Resolve();
            string version = FrameworkAssetBuildProfile.Resolve().ResolveVersionNow(); // 与资源包共用版本号格式
            var (ok, message) = FrameworkHotUpdateBuilder.BuildCodePackage(profile, version);
            Debug.Log("[热更构建] 构建代码包：\n" + message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.BundlesRoot);
            EditorUtility.DisplayDialog(ok ? "代码包构建完成" : "代码包构建失败", message, "好");
        }

        [MenuItem(Root + "4. 部署代码包（平铺到 Deploy）", priority = 4)]
        private static void Menu_DeployCodePackage()
        {
            var profile = FrameworkHotUpdateProfile.Resolve();
            var (ok, message) = FrameworkAssetBuilder.Deploy(new[] { profile.CodePackageName }, AssetBuildLayout.DeployRoot);
            Debug.Log("[热更构建] 部署代码包：\n" + message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.DeployRoot);
            EditorUtility.DisplayDialog(ok ? "部署完成（与资源包同一 Deploy 目录）" : "部署失败", message, "好");
        }

        // ───────────── 配置与诊断 ─────────────

        [MenuItem(Root + "热更配置 (HotUpdate Profile)", priority = 20)]
        private static void Menu_SelectProfile()
        {
            var profile = FrameworkHotUpdateProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        [MenuItem(Root + "校验热更程序集列表", priority = 21)]
        private static void Menu_Validate()
        {
            var profile = FrameworkHotUpdateProfile.Resolve();
            var (ok, summary) = HotUpdateAssemblyGraph.Validate(profile.HotUpdateAssemblyNames);
            Debug.Log("[热更构建] 校验热更程序集列表：\n" + summary);
            EditorUtility.DisplayDialog(ok ? "校验通过" : "校验未通过", summary, "好");
        }
    }
}
