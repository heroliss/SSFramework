using Game.Framework.Editor;
using UnityEditor;

namespace Game.Framework.Build
{
    /// <summary>
    /// 代码热更新工作台的动作层——本类只是交互外壳：
    /// 配置读 <see cref="FrameworkHotUpdateProfile"/>（单一真源），校验/排序在 <see cref="HotUpdateAssemblyGraph"/>。
    /// 改了热更列表后必须执行「同步热更设置」——HybridCLR 的 Generate / CompileDll 读的是 HybridCLRSettings，
    /// 同步就是把单一真源派生过去（不要去 HybridCLR Settings 手填）。
    /// </summary>
    public static class HotUpdateBuildMenu
    {
        internal static void SyncSettings()
        {
            if (!TryGetProfile("同步热更设置", out var profile)) return;
            if (!FrameworkEditorOperationGate.EnsureCanStart("同步热更设置")) return;
            string summary = profile.SyncToHybridCLRSettings();
            FrameworkEditorFeedback.ReportSummary("同步热更设置", summary);
        }

        internal static void GenerateBridgeAndLinker()
        {
            if (!TryGetProfile("热更 Generate", out var profile)) return;
            // Generate 内部要跑迷你构建（产裁剪 AOT DLL），与资源构建同样要求 Edit 模式 + 场景已存。
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var (ok, message) = FrameworkHotUpdateBuilder.Generate(profile);
            FrameworkEditorFeedback.ReportResult("热更 Generate", ok, message);
        }

        internal static void BuildCodePackage()
        {
            if (!TryGetProfile("构建热更代码包", out var profile)) return;
            if (!FrameworkAssetBuildProfile.TryResolve(out var assetProfile))
            {
                FrameworkEditorFeedback.Warn(
                    "构建热更代码包未启动",
                    "影响：没有创建配置，也没有构建代码包。\n原因：代码包与资源包共用版本号格式，但工程里还没有资源构建 Profile。\n" +
                    $"下一步：打开“{FrameworkMenuPaths.AssetBuild}”，创建并复核资源构建配置后重试。");
                return;
            }
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            string version = assetProfile.ResolveVersionNow(); // 与资源包共用版本号格式
            var (ok, message) = FrameworkHotUpdateBuilder.BuildCodePackage(profile, version);
            FrameworkEditorFeedback.ReportResult("构建热更代码包", ok, message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.BundlesRoot);
        }

        internal static void DeployCodePackage()
        {
            if (!TryGetProfile("部署热更代码包", out var profile)) return;
            if (!FrameworkEditorOperationGate.EnsureCanStart("部署热更代码包", requireEditMode: false)) return;
            var (ok, message) = FrameworkAssetBuilder.Deploy(new[] { profile.CodePackageName }, AssetBuildLayout.DeployRoot);
            FrameworkEditorFeedback.ReportResult("部署热更代码包", ok, message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.DeployRoot);
        }

        // ───────────── 配置与诊断 ─────────────

        internal static void SelectProfile()
        {
            if (!FrameworkHotUpdateProfile.TryResolve(out _) &&
                !FrameworkEditorOperationGate.EnsureCanStart("创建热更构建配置")) return;
            var profile = FrameworkHotUpdateProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        internal static void ValidateAssemblies()
        {
            if (!FrameworkHotUpdateProfile.TryResolve(out var profile))
            {
                FrameworkEditorFeedback.Warn(
                    "热更程序集校验未启动",
                    "影响：没有创建配置，也没有执行校验。\n原因：工程里还没有 HotUpdate Profile。\n下一步：在代码热更新工作台明确创建配置后重试。");
                return;
            }
            var (ok, summary) = HotUpdateAssemblyGraph.Validate(profile.HotUpdateAssemblyNames);
            FrameworkEditorFeedback.ReportResult("校验热更程序集列表", ok, summary);
        }

        private static bool TryGetProfile(string operation, out FrameworkHotUpdateProfile profile)
        {
            if (FrameworkHotUpdateProfile.TryResolve(out profile)) return true;
            FrameworkEditorFeedback.Warn(
                operation + "未启动",
                "影响：没有创建配置，也没有执行操作。\n原因：工程里还没有 HotUpdate Profile。\n" +
                $"下一步：打开“{FrameworkMenuPaths.HotUpdateBuild}”，点击“创建默认热更配置”并复核后重试。");
            return false;
        }
    }
}
