using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>HybridCLR 设置同步、生成、代码包构建与部署的分步工作台。</summary>
    public sealed class HotUpdateBuildWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.HotUpdateBuild, priority = 21)]
        public static void Open() => GetWindow<HotUpdateBuildWindow>("SSFramework 代码热更新").Show();

        [InitializeOnLoadMethod]
        private static void RegisterTool() => FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
            "hot-update-build", FrameworkToolCategory.BuildAndRelease, 20,
            "代码热更新", "维护热更程序集单一真源，按需同步 HybridCLR、生成桥接与裁剪文件、构建并部署代码包。",
            FrameworkMenuPaths.HotUpdateBuild));

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(320, 440);

        private void OnGUI()
        {
            bool compact = position.width < 500f;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("代码热更新 · HybridCLR", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "推荐顺序：①配置、校验并同步程序集 → ②结构边界变化后按需 Generate All → ③构建并部署代码包。每个动作仍保持独立，不会一键连跑。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool hasProfile = FrameworkHotUpdateProfile.TryResolve(out var profile);
            bool hasAssetProfile = FrameworkAssetBuildProfile.TryResolve(out _);
            DrawProfile(profile, compact);
            DrawSharedVersionDependency(hasProfile, hasAssetProfile);
            DrawStep("① 校验与同步", "校验只读，不会在缺配置时偷偷创建资产；同步会把 Profile 的程序集列表写入 HybridCLRSettings。",
                "校验程序集列表", HotUpdateBuildMenu.ValidateAssemblies,
                "同步热更设置", HotUpdateBuildMenu.SyncSettings, compact, hasProfile,
                primaryRequireEditMode: false, secondaryRequireEditMode: true);
            DrawStep("② 生成桥接与裁剪文件", "HybridCLR Generate All 较慢，并会运行迷你 Player Build；通常只在程序集、泛型实例或 Unity 版本变化后执行。",
                "执行 Generate All", HotUpdateBuildMenu.GenerateBridgeAndLinker,
                null, null, compact, hasProfile,
                primaryRequireEditMode: true, secondaryRequireEditMode: false);
            DrawStep("③ 构建与部署代码包", "构建会 CompileDll 并生成 RawFile 包；部署会替换 Deploy 下的代码包目录。",
                "构建代码包", HotUpdateBuildMenu.BuildCodePackage,
                "部署代码包", HotUpdateBuildMenu.DeployCodePackage, compact, hasProfile,
                primaryRequireEditMode: true, secondaryRequireEditMode: false,
                primaryPrerequisitesReady: hasAssetProfile);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawProfile(FrameworkHotUpdateProfile profile, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("热更配置", EditorStyles.boldLabel);
                if (profile == null)
                {
                    EditorGUILayout.HelpBox(
                        "尚无 HotUpdate Profile。创建时会尝试加入 Framework Core 与 Asset.Yoo 作为默认候选；创建后应按项目程序集边界复核。",
                        MessageType.Warning);
                    if (GUILayout.Button("创建默认热更配置")) HotUpdateBuildMenu.SelectProfile();
                    return;
                }

                GUILayout.Label($"{profile.HotUpdateAssemblyNames.Count} 个热更程序集 · {AssetDatabase.GetAssetPath(profile)}",
                    EditorStyles.wordWrappedMiniLabel);
                if (compact)
                {
                    if (GUILayout.Button("定位并编辑配置")) HotUpdateBuildMenu.SelectProfile();
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("定位并编辑配置", GUILayout.Width(130))) HotUpdateBuildMenu.SelectProfile();
                    }
                }
            }
        }

        private static void DrawSharedVersionDependency(bool hasHotUpdateProfile, bool hasAssetProfile)
        {
            if (!hasHotUpdateProfile || hasAssetProfile) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("代码包版本号前置", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "构建代码包需要资源构建 Profile 提供统一版本号格式；部署已有代码包不受影响。工具不会为补前置条件暗中创建配置。",
                    MessageType.Warning);
                if (GUILayout.Button("打开资源构建工作台")) AssetBuildWindow.Open();
            }
        }

        private static void DrawStep(
            string title,
            string description,
            string primaryLabel,
            System.Action primary,
            string secondaryLabel,
            System.Action secondary,
            bool compact,
            bool hasProfile,
            bool primaryRequireEditMode,
            bool secondaryRequireEditMode,
            bool primaryPrerequisitesReady = true)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                GUILayout.Label(description, EditorStyles.wordWrappedMiniLabel);
                string primaryReason = primaryPrerequisitesReady
                    ? "请先在上方明确创建并复核热更配置。"
                    : "构建代码包还需要资源构建 Profile 提供统一版本号格式；请先用上方跳转补齐配置。";
                bool primaryReady = hasProfile && primaryPrerequisitesReady &&
                                    FrameworkEditorOperationGate.CanStart(primaryRequireEditMode, out primaryReason);
                bool secondaryReady = secondary == null ||
                                      (hasProfile && FrameworkEditorOperationGate.CanStart(secondaryRequireEditMode, out _));
                if (!hasProfile)
                    GUILayout.Label("当前不可执行：请先在上方明确创建并复核热更配置。", EditorStyles.wordWrappedMiniLabel);
                else if (!primaryReady)
                    GUILayout.Label("当前不可执行：" + primaryReason, EditorStyles.wordWrappedMiniLabel);

                if (compact || secondary == null)
                {
                    using (new EditorGUI.DisabledScope(!primaryReady))
                        if (GUILayout.Button(primaryLabel, GUILayout.Height(26))) primary();
                    if (secondary != null)
                    {
                        using (new EditorGUI.DisabledScope(!secondaryReady))
                            if (GUILayout.Button(secondaryLabel, GUILayout.Height(26))) secondary();
                    }
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!primaryReady))
                        if (GUILayout.Button(primaryLabel, GUILayout.Height(26))) primary();
                    using (new EditorGUI.DisabledScope(!secondaryReady))
                        if (GUILayout.Button(secondaryLabel, GUILayout.Height(26))) secondary();
                }
            }
        }
    }
}
