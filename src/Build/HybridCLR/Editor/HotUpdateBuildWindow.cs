using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>HybridCLR 设置同步、生成、代码包构建与部署的分步工作台。</summary>
    public sealed class HotUpdateBuildWindow : EditorWindow
    {
        /// <summary>一步中主、次操作的可用态与应就近显示的原因；不读取 Unity 静态状态。</summary>
        internal readonly struct StepAvailability
        {
            internal bool PrimaryReady { get; }
            internal string PrimaryReason { get; }
            internal bool SecondaryReady { get; }
            internal string SecondaryReason { get; }
            internal bool ShowSecondaryReason { get; }

            internal StepAvailability(
                bool primaryReady,
                string primaryReason,
                bool secondaryReady,
                string secondaryReason,
                bool showSecondaryReason)
            {
                PrimaryReady = primaryReady;
                PrimaryReason = primaryReason;
                SecondaryReady = secondaryReady;
                SecondaryReason = secondaryReason;
                ShowSecondaryReason = showSecondaryReason;
            }
        }

        [MenuItem(FrameworkMenuPaths.HotUpdateBuild, priority = 21)]
        public static void Open() => GetWindow<HotUpdateBuildWindow>("SSFramework 代码热更新").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "hot-update-build", FrameworkToolCategory.BuildAndRelease, 20,
                "代码热更新", "维护热更程序集单一真源，按需同步 HybridCLR、生成桥接与裁剪文件、构建并部署代码包。",
                FrameworkMenuPaths.HotUpdateBuild));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "hot-update-build", 60, "热更构建", typeof(FrameworkHotUpdateProfile), singleton: true,
                "全工程单例；只在工作台明确点击创建，默认候选为内核与 Asset.Yoo，创建后需按项目边界复核。",
                FrameworkMenuPaths.HotUpdateBuild));
        }

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
            bool canCreateProfile = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true, out string createReason);
            DrawProfile(profile, compact, canCreateProfile, createReason);
            DrawSharedVersionDependency(hasProfile, hasAssetProfile);
            DrawStep("① 校验与同步", "校验只读，不会在缺配置时偷偷创建资产；同步会把 Profile 的程序集列表写入 HybridCLRSettings。",
                "校验程序集列表", HotUpdateBuildMenu.ValidateAssemblies,
                "同步热更设置", HotUpdateBuildMenu.SyncSettings, compact, hasProfile,
                primaryRequireEditMode: null, secondaryRequireEditMode: true);
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

        private static void DrawProfile(
            FrameworkHotUpdateProfile profile,
            bool compact,
            bool canCreateProfile,
            string operationReason)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("热更配置", EditorStyles.boldLabel);
                if (profile == null)
                {
                    EditorGUILayout.HelpBox(
                        "尚无 HotUpdate Profile。创建时会尝试加入 Framework Core 与 Asset.Yoo 作为默认候选；创建后应按项目程序集边界复核。",
                        MessageType.Warning);
                    if (!canCreateProfile)
                        EditorGUILayout.HelpBox("当前不能创建配置：\n" + operationReason, MessageType.Warning);
                    using (new EditorGUI.DisabledScope(!canCreateProfile))
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
            bool? primaryRequireEditMode,
            bool secondaryRequireEditMode,
            bool primaryPrerequisitesReady = true)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                GUILayout.Label(description, EditorStyles.wordWrappedMiniLabel);
                const string missingProfileReason = "请先在上方明确创建并复核热更配置。";
                const string missingAssetProfileReason =
                    "构建代码包还需要资源构建 Profile 提供统一版本号格式；请先用上方跳转补齐配置。";
                string primaryGateReason = string.Empty;
                bool primaryGateReady = !hasProfile || !primaryPrerequisitesReady ||
                                        !primaryRequireEditMode.HasValue ||
                                        FrameworkEditorOperationGate.CanStart(
                                            primaryRequireEditMode.Value, out primaryGateReason);
                string secondaryGateReason = string.Empty;
                bool secondaryGateReady = !hasProfile || secondary == null ||
                                          FrameworkEditorOperationGate.CanStart(
                                              secondaryRequireEditMode, out secondaryGateReason);
                StepAvailability availability = EvaluateStepAvailability(
                    hasProfile,
                    primaryPrerequisitesReady,
                    missingProfileReason,
                    missingAssetProfileReason,
                    primaryGateReady,
                    primaryGateReason,
                    secondary != null,
                    secondaryGateReady,
                    secondaryGateReason);
                if (!availability.PrimaryReady)
                    GUILayout.Label(
                        "当前不可执行：" + availability.PrimaryReason,
                        EditorStyles.wordWrappedMiniLabel);
                if (availability.ShowSecondaryReason)
                {
                    GUILayout.Label(
                        secondaryLabel + " 当前不可执行：" + availability.SecondaryReason,
                        EditorStyles.wordWrappedMiniLabel);
                }

                if (compact || secondary == null)
                {
                    using (new EditorGUI.DisabledScope(!availability.PrimaryReady))
                        if (GUILayout.Button(primaryLabel, GUILayout.Height(26))) primary();
                    if (secondary != null)
                    {
                        using (new EditorGUI.DisabledScope(!availability.SecondaryReady))
                            if (GUILayout.Button(secondaryLabel, GUILayout.Height(26))) secondary();
                    }
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!availability.PrimaryReady))
                        if (GUILayout.Button(primaryLabel, GUILayout.Height(26))) primary();
                    using (new EditorGUI.DisabledScope(!availability.SecondaryReady))
                        if (GUILayout.Button(secondaryLabel, GUILayout.Height(26))) secondary();
                }
            }
        }

        /// <summary>
        /// 合并业务前置条件与已经求值的 Gate 结果。业务缺失优先于 Unity 忙碌；共享 Profile 缺失只解释一次。
        /// </summary>
        internal static StepAvailability EvaluateStepAvailability(
            bool hasProfile,
            bool primaryPrerequisitesReady,
            string missingProfileReason,
            string primaryPrerequisiteReason,
            bool primaryGateReady,
            string primaryGateReason,
            bool hasSecondary,
            bool secondaryGateReady,
            string secondaryGateReason)
        {
            if (!hasProfile)
            {
                return new StepAvailability(
                    primaryReady: false,
                    primaryReason: missingProfileReason,
                    secondaryReady: !hasSecondary,
                    secondaryReason: string.Empty,
                    showSecondaryReason: false);
            }

            bool primaryReady = primaryPrerequisitesReady && primaryGateReady;
            string primaryReason = !primaryPrerequisitesReady
                ? primaryPrerequisiteReason
                : primaryGateReady ? string.Empty : primaryGateReason;
            bool secondaryReady = !hasSecondary || secondaryGateReady;
            string secondaryReason = secondaryReady ? string.Empty : secondaryGateReason;
            bool showSecondaryReason = hasSecondary && !secondaryReady &&
                                       (primaryReady || !string.Equals(
                                           primaryReason,
                                           secondaryReason,
                                           System.StringComparison.Ordinal));
            return new StepAvailability(
                primaryReady,
                primaryReason,
                secondaryReady,
                secondaryReason,
                showSecondaryReason);
        }
    }
}
