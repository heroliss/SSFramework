using System.IO;
using System.Linq;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

namespace Game.Framework.Build
{
    /// <summary>资源包从配置、构建、部署到本地联调的人工工作台；操作按钮调用既有 Builder Implementation。</summary>
    public sealed class AssetBuildWindow : EditorWindow
    {
        /// <summary>构建、部署与本地服务各自的可用态；三个动作的业务前置条件并不相同。</summary>
        internal readonly struct ActionAvailability
        {
            internal bool BuildReady { get; }
            internal string BuildReason { get; }
            internal bool DeployReady { get; }
            internal string DeployReason { get; }
            internal bool ServeReady { get; }
            internal string ServeReason { get; }

            internal ActionAvailability(
                bool buildReady,
                string buildReason,
                bool deployReady,
                string deployReason,
                bool serveReady,
                string serveReason)
            {
                BuildReady = buildReady;
                BuildReason = buildReason;
                DeployReady = deployReady;
                DeployReason = deployReason;
                ServeReady = serveReady;
                ServeReason = serveReason;
            }
        }

        [MenuItem(FrameworkMenuPaths.AssetBuild, priority = 20)]
        public static void Open() => GetWindow<AssetBuildWindow>("SSFramework 资源构建").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "asset-build", FrameworkToolCategory.BuildAndRelease, 10,
                "资源构建与本地 CDN", "分步构建 YooAsset 资源包、部署待发布目录并启动本地 HTTP 服务；不会把三步暗中捆成一键流程。",
                FrameworkMenuPaths.AssetBuild));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "asset-build", 50, "资源构建", typeof(FrameworkAssetBuildProfile), singleton: true,
                "全工程单例；只在工作台明确点击创建，按 YooAsset Collector 的包列表初始化。",
                FrameworkMenuPaths.AssetBuild));
            FrameworkGeneratedOutputClaimCatalog.Register(new FrameworkGeneratedOutputClaimSource(
                AssetPackageConstantsGenerator.OutputClaimSourceId,
                "资源包名常量",
                AssetPackageConstantsGenerator.CollectRegisteredOutputClaims));
        }

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(320, 420);

        private void OnGUI()
        {
            bool compact = position.width < 500f;
            bool canEditProject = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true, out string editModeReason);
            bool canRunAnyMode = FrameworkEditorOperationGate.CanStart(
                requireEditMode: false, out string anyModeReason);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("资源构建与本地联调", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "推荐顺序：①构建资源包 → ②部署到统一目录 → ③按需启动本地 CDN。步骤保持独立，正式发布可把部署目录交给 CI 上传，不依赖本地服务。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool hasProfile = FrameworkAssetBuildProfile.TryResolve(out var profile);
            int enabledPackageCount = profile?.EnabledPackageNames.Count() ?? 0;
            ActionAvailability availability = EvaluateActionAvailability(
                hasProfile,
                canEditProject,
                editModeReason,
                canRunAnyMode,
                anyModeReason,
                enabledPackageCount,
                Directory.Exists(AssetBuildLayout.DeployRoot));
            DrawProfile(profile, compact, canEditProject, editModeReason);
            DrawBuild(compact, availability);
            DrawDeployAndServe(compact, availability);
            DrawUtilities(compact);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawProfile(
            FrameworkAssetBuildProfile profile,
            bool compact,
            bool canEditProject,
            string operationReason)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
                if (profile == null)
                {
                    EditorGUILayout.HelpBox(
                        "尚无构建 Profile。创建时会读取 YooAsset Collector 的包列表作为初始值；这是一次明确的项目资产写入。",
                        MessageType.Warning);
                    if (!canEditProject)
                        EditorGUILayout.HelpBox("当前不能创建配置：\n" + operationReason, MessageType.Warning);
                    using (new EditorGUI.DisabledScope(!canEditProject))
                        if (GUILayout.Button("创建默认构建配置")) AssetBuildMenu.SelectProfile();
                    return;
                }

                string path = AssetDatabase.GetAssetPath(profile);
                GUILayout.Label($"{profile.EnabledPackageNames.Count()} 个启用包 · {path}", EditorStyles.wordWrappedMiniLabel);
                if (!canEditProject)
                    EditorGUILayout.HelpBox("当前不能同步配置或生成常量：\n" + operationReason, MessageType.Warning);
                if (compact)
                {
                    if (GUILayout.Button("定位配置")) AssetBuildMenu.SelectProfile();
                    using (new EditorGUI.DisabledScope(!canEditProject))
                    {
                        if (GUILayout.Button("同步 Collector 包列表")) AssetBuildMenu.SyncProfile();
                        if (GUILayout.Button("生成包名常量")) AssetBuildMenu.GeneratePackageConstants();
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("定位配置")) AssetBuildMenu.SelectProfile();
                        using (new EditorGUI.DisabledScope(!canEditProject))
                        {
                            if (GUILayout.Button("同步 Collector 包列表")) AssetBuildMenu.SyncProfile();
                            if (GUILayout.Button("生成包名常量")) AssetBuildMenu.GeneratePackageConstants();
                        }
                    }
                }
            }
        }

        private static void DrawBuild(bool compact, ActionAvailability availability)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("① 构建资源包", EditorStyles.boldLabel);
                GUILayout.Label(
                    "普通构建复用 SBP 增量缓存；全量重建会先清缓存，明显更慢，仅用于缓存损坏或产物异常排查。构建前 Unity 会询问如何处理脏场景。",
                    EditorStyles.wordWrappedMiniLabel);

                if (!availability.BuildReady)
                    EditorGUILayout.HelpBox(
                        "当前不能构建：\n" + availability.BuildReason,
                        MessageType.Warning);
                using (new EditorGUI.DisabledScope(!availability.BuildReady))
                {
                    if (compact)
                    {
                        if (GUILayout.Button("普通增量构建", GUILayout.Height(28))) AssetBuildMenu.Build();
                        if (GUILayout.Button("全量重建（会再次确认）")) AssetBuildMenu.FullRebuild();
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("普通增量构建", GUILayout.Height(28))) AssetBuildMenu.Build();
                            if (GUILayout.Button("全量重建（会再次确认）", GUILayout.Height(28))) AssetBuildMenu.FullRebuild();
                        }
                    }
                }

                bool useDb = EditorGUILayout.ToggleLeft(
                    new GUIContent("使用资源依赖数据库加速收集", "仅保存在本机 EditorPrefs；影响收集速度，不改变构建产物。CI 使用命令行参数单独控制。"),
                    AssetBuildMenu.UseDependencyDB);
                if (useDb != AssetBuildMenu.UseDependencyDB) AssetBuildMenu.SetUseDependencyDB(useDb);
            }
        }

        private static void DrawDeployAndServe(bool compact, ActionAvailability availability)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("② 部署 · ③ 本地服务", EditorStyles.boldLabel);
                GUILayout.Label(
                    "部署会用最新构建产物重建 Deploy 下对应包目录；本地服务只用于开发联调，会启动外部 Python 进程。",
                    EditorStyles.wordWrappedMiniLabel);
                if (!availability.DeployReady && !availability.ServeReady &&
                    string.Equals(
                        availability.DeployReason,
                        availability.ServeReason,
                        System.StringComparison.Ordinal))
                {
                    EditorGUILayout.HelpBox(
                        "部署与本地服务当前都不可执行：\n" + availability.DeployReason,
                        MessageType.Warning);
                }
                else
                {
                    if (!availability.DeployReady)
                        EditorGUILayout.HelpBox(
                            "当前不能部署：\n" + availability.DeployReason,
                            MessageType.Warning);
                    if (!availability.ServeReady)
                        EditorGUILayout.HelpBox(
                            "当前不能启动本地服务：\n" + availability.ServeReason,
                            MessageType.Warning);
                }

                if (compact)
                {
                    using (new EditorGUI.DisabledScope(!availability.DeployReady))
                        if (GUILayout.Button("部署到本地目录")) AssetBuildMenu.Deploy();
                    using (new EditorGUI.DisabledScope(!availability.ServeReady))
                        if (GUILayout.Button("启动本地 CDN 服务")) AssetBuildMenu.StartLocalServer();
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!availability.DeployReady))
                            if (GUILayout.Button("部署到本地目录")) AssetBuildMenu.Deploy();
                        using (new EditorGUI.DisabledScope(!availability.ServeReady))
                            if (GUILayout.Button("启动本地 CDN 服务")) AssetBuildMenu.StartLocalServer();
                    }
                }
            }
        }

        /// <summary>
        /// 合并动作层的真实优先级：先要求 Profile，再判断 Unity 状态，最后检查各动作自己的廉价业务条件。
        /// </summary>
        internal static ActionAvailability EvaluateActionAvailability(
            bool hasProfile,
            bool canEditProject,
            string editModeReason,
            bool canRunAnyMode,
            string anyModeReason,
            int enabledPackageCount,
            bool deployDirectoryExists)
        {
            const string missingProfileReason = "请先在上方明确创建并复核构建配置。";
            const string missingPackagesReason =
                "构建配置中没有启用的资源包；请定位配置并至少开启一个普通 AssetBundle 包的“参与构建”。";
            string buildReason = !hasProfile
                ? missingProfileReason
                : !canEditProject ? editModeReason
                : enabledPackageCount <= 0 ? missingPackagesReason
                : string.Empty;
            string deployReason = !hasProfile
                ? missingProfileReason
                : !canRunAnyMode ? anyModeReason
                : enabledPackageCount <= 0 ? missingPackagesReason
                : string.Empty;
            string serveReason = !hasProfile
                ? missingProfileReason
                : !canRunAnyMode ? anyModeReason
                : !deployDirectoryExists
                    ? "部署目录尚不存在；请先完成资源包构建与部署，再启动本地服务。"
                    : string.Empty;
            return new ActionAvailability(
                buildReady: buildReason.Length == 0,
                buildReason: buildReason,
                deployReady: deployReason.Length == 0,
                deployReason: deployReason,
                serveReady: serveReason.Length == 0,
                serveReason: serveReason);
        }

        private static void DrawUtilities(bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("产物与缓存目录", EditorStyles.boldLabel);
                GUILayout.Label("按钮只打开已存在的目录，不会因为查看而创建空目录。", EditorStyles.wordWrappedMiniLabel);
                DrawDirectoryButtons(compact);
                DrawPath("构建输出", AssetBuildLayout.BundlesRoot);
                DrawPath("部署", AssetBuildLayout.DeployRoot);
                DrawPath("下载缓存", AssetBuildLayout.DownloadedRoot);
                DrawPath("内置首包", BundleBuilderHelper.GetStreamingAssetsRoot());
            }
        }

        private static void DrawDirectoryButtons(bool compact)
        {
            if (compact)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("构建输出")) AssetBuildMenu.OpenBuildOutput();
                    if (GUILayout.Button("部署")) AssetBuildMenu.OpenDeploy();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("下载缓存")) AssetBuildMenu.OpenDownloaded();
                    if (GUILayout.Button("内置首包")) AssetBuildMenu.OpenBuiltin();
                }
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("构建输出")) AssetBuildMenu.OpenBuildOutput();
                if (GUILayout.Button("部署")) AssetBuildMenu.OpenDeploy();
                if (GUILayout.Button("下载缓存")) AssetBuildMenu.OpenDownloaded();
                if (GUILayout.Button("内置首包")) AssetBuildMenu.OpenBuiltin();
            }
        }

        private static void DrawPath(string label, string path)
        {
            string state = Directory.Exists(path) ? "✓" : "—";
            GUILayout.Label($"{state} {label}：{path}", EditorStyles.wordWrappedMiniLabel);
        }
    }
}
