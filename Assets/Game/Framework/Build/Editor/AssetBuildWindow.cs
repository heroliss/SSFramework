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
        }

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(320, 420);

        private void OnGUI()
        {
            bool compact = position.width < 500f;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("资源构建与本地联调", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "推荐顺序：①构建资源包 → ②部署到统一目录 → ③按需启动本地 CDN。步骤保持独立，正式发布可把部署目录交给 CI 上传，不依赖本地服务。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool hasProfile = FrameworkAssetBuildProfile.TryResolve(out var profile);
            DrawProfile(profile, compact);
            DrawBuild(compact, hasProfile);
            DrawDeployAndServe(compact, hasProfile);
            DrawUtilities(compact);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawProfile(FrameworkAssetBuildProfile profile, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
                if (profile == null)
                {
                    EditorGUILayout.HelpBox(
                        "尚无构建 Profile。创建时会读取 YooAsset Collector 的包列表作为初始值；这是一次明确的项目资产写入。",
                        MessageType.Warning);
                    if (GUILayout.Button("创建默认构建配置")) AssetBuildMenu.SelectProfile();
                    return;
                }

                string path = AssetDatabase.GetAssetPath(profile);
                GUILayout.Label($"{profile.EnabledPackageNames.Count()} 个启用包 · {path}", EditorStyles.wordWrappedMiniLabel);
                if (compact)
                {
                    if (GUILayout.Button("定位配置")) AssetBuildMenu.SelectProfile();
                    if (GUILayout.Button("同步 Collector 包列表")) AssetBuildMenu.SyncProfile();
                    if (GUILayout.Button("生成包名常量")) AssetBuildMenu.GeneratePackageConstants();
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("定位配置")) AssetBuildMenu.SelectProfile();
                        if (GUILayout.Button("同步 Collector 包列表")) AssetBuildMenu.SyncProfile();
                        if (GUILayout.Button("生成包名常量")) AssetBuildMenu.GeneratePackageConstants();
                    }
                }
            }
        }

        private static void DrawBuild(bool compact, bool hasProfile)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("① 构建资源包", EditorStyles.boldLabel);
                GUILayout.Label(
                    "普通构建复用 SBP 增量缓存；全量重建会先清缓存，明显更慢，仅用于缓存损坏或产物异常排查。构建前 Unity 会询问如何处理脏场景。",
                    EditorStyles.wordWrappedMiniLabel);

                string reason = "请先在上方明确创建并复核构建配置。";
                bool ready = hasProfile && FrameworkEditorOperationGate.CanStart(requireEditMode: true, out reason);
                if (!ready) EditorGUILayout.HelpBox(reason, MessageType.Warning);
                using (new EditorGUI.DisabledScope(!ready))
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

        private static void DrawDeployAndServe(bool compact, bool hasProfile)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("② 部署 · ③ 本地服务", EditorStyles.boldLabel);
                GUILayout.Label(
                    "部署会用最新构建产物重建 Deploy 下对应包目录；本地服务只用于开发联调，会启动外部 Python 进程。",
                    EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(!hasProfile))
                {
                    if (compact)
                    {
                        if (GUILayout.Button("部署到本地目录")) AssetBuildMenu.Deploy();
                        if (GUILayout.Button("启动本地 CDN 服务")) AssetBuildMenu.StartLocalServer();
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("部署到本地目录")) AssetBuildMenu.Deploy();
                            if (GUILayout.Button("启动本地 CDN 服务")) AssetBuildMenu.StartLocalServer();
                        }
                    }
                }
            }
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
