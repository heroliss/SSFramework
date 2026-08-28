using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「服务安装器配置总览」窗口：把工程内所有 <see cref="ServiceInstallerProfile"/> 集中成卡片——每份列出
    /// 各条目的「扫描目录 → 输出安装器 / 命名空间」，并提供「生成这份 / 点名定位资产 / 定位生成的 .g.cs」。
    /// 多份按子项目、环境或功能域并存时，可一眼看清各份落点并按份操作；顶部「生成全部」是统一人工入口。
    /// </summary>
    public sealed class ServiceInstallerOverviewWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.ServiceInstaller, priority = 42)]
        public static void Open() => GetWindow<ServiceInstallerOverviewWindow>("服务安装器总览").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "service-installer", FrameworkToolCategory.CodeGeneration, 30,
                "服务安装器", "按功能域扫描纯 C# 服务并生成显式安装器；装入哪个 Context 仍由业务代码决定。",
                FrameworkMenuPaths.ServiceInstaller));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "service-installer", 10, "服务注册（安装器生成）", typeof(ServiceInstallerProfile), singleton: false,
                "可按子项目、环境或功能域并存多份；无自动创建，由工作台或 Assets/Create 显式建立。",
                FrameworkMenuPaths.ServiceInstaller));
        }

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(280, 320);

        private void OnGUI()
        {
            bool compact = position.width < 420f;
            bool canWrite = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true, out string operationReason);
            EditorGUILayout.Space(4);
            if (compact)
            {
                EditorGUILayout.LabelField("服务安装器 · 配置总览", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!canWrite))
                    if (GUILayout.Button("新建配置")) CreateProfile();
                if (GUILayout.Button("刷新")) Repaint();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("服务安装器 · 配置总览", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(!canWrite))
                        if (GUILayout.Button("新建配置", GUILayout.Width(80))) CreateProfile();
                    if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
                }
            }
            EditorGUILayout.HelpBox(
                "每份 profile 若干条目：扫描目录下的纯 C# 服务类 → 生成一个静态安装器 (.g.cs)；" +
                "装进哪个 Context 由业务在该 Context 的 InstallBindings 里手写一行调用决定。\n" +
                "多份 profile 可按子项目、环境或功能域拆分；每个条目必须独占一个位于 Assets 内的 .cs 输出文件。",
                MessageType.Info);
            if (!canWrite)
                EditorGUILayout.HelpBox("当前不能新建或生成：\n" + operationReason, MessageType.Warning);

            var profiles = ServiceInstallerProfile.ResolveAll();
            var (ownershipOk, ownershipMessage) = profiles.Count == 0
                ? (false, string.Empty)
                : ServiceInstallerGenerator.ValidateOutputOwnership(profiles);
            var prerequisites = profiles.ToDictionary(
                profile => profile,
                ServiceInstallerGenerator.InspectGenerationPrerequisites);
            int readyCount = prerequisites.Count(pair => pair.Value.CanGenerate);
            string countSummary = FormatCountSummary(profiles.Count, ownershipOk, readyCount);
            bool canGenerateAny = ServiceInstallerGenerator.CanStartGenerationAction(
                canWrite, ownershipOk, readyCount);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (compact)
            {
                EditorGUILayout.LabelField(countSummary, EditorStyles.miniBoldLabel);
                using (new EditorGUI.DisabledScope(!canGenerateAny))
                    if (GUILayout.Button("生成全部"))
                        ServiceInstallerMenu.GenerateProfiles(profiles);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(countSummary, EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(!canGenerateAny))
                        if (GUILayout.Button("生成全部", GUILayout.Width(90)))
                            ServiceInstallerMenu.GenerateProfiles(profiles);
                }
            }
            if (profiles.Count == 0)
                EditorGUILayout.HelpBox("还没有服务安装器配置——点击顶部“新建配置”，再填写扫描目录、唯一输出文件与命名空间。", MessageType.Warning);
            else if (!ownershipOk)
                EditorGUILayout.HelpBox("输出文件预检未通过：\n" + ownershipMessage, MessageType.Error);
            else if (readyCount == 0)
                EditorGUILayout.HelpBox(
                    "当前没有具备生成前置条件的配置；请按卡片提示补齐条目。",
                    MessageType.Warning);
            else
                EditorGUILayout.LabelField("✓ " + ownershipMessage, EditorStyles.miniLabel);

            foreach (var profile in profiles)
            {
                ServiceInstallerGenerator.GenerationPrerequisiteReport profilePrerequisites =
                    prerequisites[profile];
                bool canGenerateProfile = ServiceInstallerGenerator.CanStartGenerationAction(
                    canWrite, ownershipOk, profilePrerequisites.ReadyEntryCount);
                DrawCard(profile, profilePrerequisites, canGenerateProfile, compact);
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>零配置是引导态，不应被描述成输出预检失败；有配置后才呈现所有权或就绪数量。</summary>
        internal static string FormatCountSummary(int profileCount, bool ownershipOk, int readyCount)
        {
            if (profileCount <= 0) return "共 0 份";
            return ownershipOk
                ? $"共 {profileCount} 份 · 可生成 {readyCount} 份"
                : $"共 {profileCount} 份 · 输出预检失败，已暂停";
        }

        private static void CreateProfile()
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("创建服务安装器配置")) return;
            if (!EditorApplication.ExecuteMenuItem("Assets/Create/SSFramework/服务安装器配置 (Service Installer Profile)"))
                FrameworkEditorFeedback.Warn(
                    "新建服务安装器配置未启动",
                    "影响：没有创建资产。\n下一步：在 Project 窗口使用 Assets/Create/SSFramework/服务安装器配置。");
        }

        // 一份 profile 一张卡片：资产名（点击定位选中）+ 逐条目「扫描目录 → 输出 / 命名空间」+ 生成按钮。
        private static void DrawCard(
            ServiceInstallerProfile profile,
            ServiceInstallerGenerator.GenerationPrerequisiteReport prerequisites,
            bool canGenerate,
            bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string path = AssetDatabase.GetAssetPath(profile);
                var assetRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                if (GUI.Button(assetRect, new GUIContent(Path.GetFileName(path), path + "\n点击定位并选中"), EditorStyles.objectField))
                {
                    EditorGUIUtility.PingObject(profile);
                    Selection.activeObject = profile;
                }

                if (profile.Installers != null)
                    for (int i = 0; i < profile.Installers.Count; i++)
                    {
                        ServiceInstallerProfile.InstallerEntry entry = profile.Installers[i];
                        if (entry == null)
                            EditorGUILayout.LabelField(
                                $"条目 {i + 1}：（空配置，无法生成）",
                                EditorStyles.wordWrappedMiniLabel);
                        else
                            DrawEntry(entry, compact);
                    }

                if (!prerequisites.CanGenerate)
                    EditorGUILayout.HelpBox(
                        "生成前置条件未满足：\n" + prerequisites.Message,
                        MessageType.Warning);
                else if (prerequisites.HasInvalidEntries)
                    EditorGUILayout.HelpBox(
                        "部分条目尚未就绪：\n" + prerequisites.Message,
                        MessageType.Warning);

                string buttonLabel = prerequisites.HasInvalidEntries && prerequisites.CanGenerate
                    ? $"生成可用条目（{prerequisites.ReadyEntryCount}/{prerequisites.TotalEntryCount}）"
                    : "生成这份";
                using (new EditorGUI.DisabledScope(!canGenerate))
                    if (GUILayout.Button(buttonLabel))
                        ServiceInstallerMenu.GenerateProfiles(new[] { profile });
            }
        }

        private static void DrawEntry(ServiceInstallerProfile.InstallerEntry entry, bool compact)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var folders = entry.ScanFolders == null
                    ? new List<string>()
                    : entry.ScanFolders.Where(f => f != null).Select(AssetDatabase.GetAssetPath).ToList();
                if (folders.Count == 0)
                    EditorGUILayout.LabelField("扫描目录", "（未配置）", EditorStyles.miniLabel);
                else
                    for (int i = 0; i < folders.Count; i++)
                        DrawValue(i == 0 ? "扫描目录" : null, folders[i], compact);

                var generated = AssetDatabase.LoadAssetAtPath<MonoScript>(entry.OutputPath);
                if (compact)
                {
                    DrawValue("输出", entry.OutputPath, compact: true);
                    // 生成过才有资产可定位；没生成过按钮不出现（而非置灰——避免误解为「点了会生成」）。
                    if (generated != null && GUILayout.Button("定位 .g.cs"))
                        EditorGUIUtility.PingObject(generated);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("输出", entry.OutputPath);
                        if (generated != null && GUILayout.Button("定位 .g.cs", GUILayout.Width(80)))
                            EditorGUIUtility.PingObject(generated);
                    }
                }
                DrawValue("命名空间", entry.Namespace, compact);
            }
        }

        private static void DrawValue(string label, string value, bool compact)
        {
            value = string.IsNullOrEmpty(value) ? "（未配置）" : value;
            if (!compact)
            {
                EditorGUILayout.LabelField(label ?? " ", value);
                return;
            }

            if (!string.IsNullOrEmpty(label))
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            GUILayout.Label(value, EditorStyles.wordWrappedMiniLabel);
        }
    }
}
