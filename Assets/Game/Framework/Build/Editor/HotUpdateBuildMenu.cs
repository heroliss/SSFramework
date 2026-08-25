using Game.Framework.Editor;
using UnityEditor;

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
            FrameworkEditorFeedback.ReportSummary("同步热更设置", summary);
        }

        [MenuItem(Root + "2. 生成桥接与裁剪文件（Generate All，慢）", priority = 2)]
        private static void Menu_Generate()
        {
            // Generate 内部要跑迷你构建（产裁剪 AOT DLL），与资源构建同样要求 Edit 模式 + 场景已存。
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var profile = FrameworkHotUpdateProfile.Resolve();
            var (ok, message) = FrameworkHotUpdateBuilder.Generate(profile);
            FrameworkEditorFeedback.ReportResult("热更 Generate", ok, message);
        }

        [MenuItem(Root + "3. 构建代码包（CompileDll → RawFile 包）", priority = 3)]
        private static void Menu_BuildCodePackage()
        {
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var profile = FrameworkHotUpdateProfile.Resolve();
            string version = FrameworkAssetBuildProfile.Resolve().ResolveVersionNow(); // 与资源包共用版本号格式
            var (ok, message) = FrameworkHotUpdateBuilder.BuildCodePackage(profile, version);
            FrameworkEditorFeedback.ReportResult("构建热更代码包", ok, message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.BundlesRoot);
        }

        [MenuItem(Root + "4. 部署代码包（平铺到 Deploy）", priority = 4)]
        private static void Menu_DeployCodePackage()
        {
            var profile = FrameworkHotUpdateProfile.Resolve();
            var (ok, message) = FrameworkAssetBuilder.Deploy(new[] { profile.CodePackageName }, AssetBuildLayout.DeployRoot);
            FrameworkEditorFeedback.ReportResult("部署热更代码包", ok, message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.DeployRoot);
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
            FrameworkEditorFeedback.ReportResult("校验热更程序集列表", ok, summary);
        }
    }
}
