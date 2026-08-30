using System.IO;
using System.Linq;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Config.Editor
{
    /// <summary>
    /// 「配置表生成总览」窗口：把工程内所有 <see cref="LubanConfigProfile"/> 集中成卡片——每套列出 luban.conf 源、目标、
    /// 代码 / 数据输出目录、命名空间，并提供「生成这套 / 打开各目录 / 点名定位资产」。多套按数据域或构建目标并存时
    /// 一眼看清各套落点、按套操作，省得到处翻文件夹；顶部批量按钮只提交当前已就绪配置。
    /// </summary>
    public sealed class LubanConfigOverviewWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.Luban, priority = 40)]
        public static void Open() => GetWindow<LubanConfigOverviewWindow>("配置表生成总览").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "luban", FrameworkToolCategory.CodeGeneration, 10,
                "配置表 (Luban)", "管理多套 Luban 输入与输出；代码、数据和 manifest 经暂存校验后按套事务发布。",
                FrameworkMenuPaths.Luban));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "luban", 30, "配置表（Luban 生成）", typeof(LubanConfigProfile), singleton: false,
                "可按数据域或构建目标并存多套；每套显式维护 luban.conf 源、代码与数据输出，框架不猜业务路径。",
                FrameworkMenuPaths.Luban));
            FrameworkGeneratedOutputClaimCatalog.Register(new FrameworkGeneratedOutputClaimSource(
                LubanCodeGenerator.OutputClaimSourceId,
                "配置表（Luban）",
                LubanCodeGenerator.CollectRegisteredOutputClaims));
        }

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(280, 320);

        private void OnGUI()
        {
            bool compact = position.width < 520f;
            bool canWrite = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true, out string operationReason);
            EditorGUILayout.Space(4);
            if (compact)
            {
                EditorGUILayout.LabelField("配置表生成 · 总览", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!canWrite))
                    if (GUILayout.Button("新建配置")) CreateProfile();
                if (GUILayout.Button("刷新")) Repaint();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("配置表生成 · 总览", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(!canWrite))
                        if (GUILayout.Button("新建配置", GUILayout.Width(80))) CreateProfile();
                    if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
                }
            }
            EditorGUILayout.HelpBox(
                "每套配置 = 一个 Luban Profile（luban.conf 源 + 代码 / 数据输出 + 命名空间）。所有输出必须位于 Assets 的独立子目录，" +
                "且不得与其它生成器的写入 / 清理范围重叠。当前运行时固定使用 cs-bin + bin。\n" +
                "CLI 只写暂存区；校验成功后差量发布，失败恢复旧代码与数据。可按数据域或可选内容拆分。",
                MessageType.Info);
            if (!canWrite)
                EditorGUILayout.HelpBox("当前不能新建或生成：\n" + operationReason, MessageType.Warning);

            var profiles = LubanConfigProfile.ResolveAll();
            var (ownershipOk, ownershipMessage) = profiles.Count == 0
                ? (false, string.Empty)
                : LubanCodeGenerator.ValidateOutputOwnership(profiles);
            var prerequisites = profiles.ToDictionary(
                profile => profile,
                LubanCodeGenerator.InspectGenerationPrerequisites);
            var readyProfiles = profiles
                .Where(profile => prerequisites[profile].CanGenerate)
                .ToArray();
            int readyCount = readyProfiles.Length;
            bool canGenerateAny = canWrite && ownershipOk && readyCount > 0;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (compact)
            {
                EditorGUILayout.LabelField(
                    FormatCountSummary(profiles.Count, ownershipOk, readyCount),
                    EditorStyles.miniBoldLabel);
                using (new EditorGUI.DisabledScope(!canGenerateAny))
                    if (GUILayout.Button(FormatBatchButtonLabel(profiles.Count, ownershipOk, readyCount)))
                        LubanBuildMenu.GenerateProfiles(readyProfiles);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        FormatCountSummary(profiles.Count, ownershipOk, readyCount),
                        EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(!canGenerateAny))
                        if (GUILayout.Button(
                                FormatBatchButtonLabel(profiles.Count, ownershipOk, readyCount),
                                GUILayout.MinWidth(90)))
                            LubanBuildMenu.GenerateProfiles(readyProfiles);
                }
            }
            if (profiles.Count == 0)
                EditorGUILayout.LabelField("（无配置——点击“新建配置”后填写 conf 与输出目录）", EditorStyles.wordWrappedMiniLabel);
            else if (!ownershipOk)
                EditorGUILayout.HelpBox("输出目录预检未通过：\n" + ownershipMessage, MessageType.Error);
            else if (readyCount == 0)
                EditorGUILayout.HelpBox(
                    "当前没有可生成的配置；请按卡片提示补齐 CLI、luban.conf 与输出字段。",
                    MessageType.Warning);
            else
                EditorGUILayout.LabelField("✓ " + ownershipMessage, EditorStyles.miniLabel);

            foreach (var profile in profiles)
                DrawCard(
                    profile,
                    prerequisites[profile],
                    canWrite && ownershipOk && prerequisites[profile].CanGenerate,
                    compact);
            EditorGUILayout.EndScrollView();
        }

        internal static string FormatCountSummary(int profileCount, bool ownershipOk, int readyCount)
        {
            if (profileCount <= 0) return "共 0 套";
            return ownershipOk
                ? $"共 {profileCount} 套 · 可生成 {readyCount} 套"
                : $"共 {profileCount} 套 · 输出预检失败，已暂停";
        }

        internal static string FormatBatchButtonLabel(
            int profileCount,
            bool ownershipOk,
            int readyCount)
        {
            if (profileCount <= 0) return "生成全部";
            if (!ownershipOk) return "输出冲突，已暂停";
            if (readyCount <= 0) return "暂无可生成配置";
            return readyCount > 0 && readyCount < profileCount
                ? $"生成可用配置（{readyCount}/{profileCount}）"
                : "生成全部";
        }

        private static void CreateProfile()
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("创建 Luban 配置")) return;
            if (!EditorApplication.ExecuteMenuItem("Assets/Create/SSFramework/配置表生成配置 (Luban Profile)"))
                FrameworkEditorFeedback.Warn(
                    "新建 Luban 配置未启动",
                    "影响：没有创建资产。\n下一步：在 Project 窗口使用 Assets/Create/SSFramework/配置表生成配置。");
        }

        // 一套配置一张卡片：资产名（点击定位选中）+ 源 / 目标 / 输出 / 命名空间 + 响应式操作区。
        private static void DrawCard(
            LubanConfigProfile profile,
            LubanCodeGenerator.GenerationPrerequisiteReport prerequisites,
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
                DrawValue("Luban CLI", profile.LubanToolPath, compact);
                DrawValue("源 (luban.conf)", profile.ConfPath, compact);
                DrawValue(
                    "目标",
                    $"{profile.Target} · {LubanCodeGenerator.CodeTarget} / {LubanCodeGenerator.DataTarget}",
                    compact);
                DrawValue("代码输出", profile.OutputCodeDir, compact);
                DrawValue("数据输出", profile.OutputDataDir, compact);
                DrawValue("命名空间", profile.ManifestNamespace, compact);

                if (!prerequisites.CanGenerate)
                    EditorGUILayout.HelpBox(
                        "当前配置不能生成：\n" + prerequisites.Message,
                        MessageType.Warning);

                if (compact)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!canGenerate))
                            if (GUILayout.Button("生成这套"))
                                LubanBuildMenu.GenerateProfiles(new[] { profile });
                        if (GUILayout.Button("源目录")) Reveal(Path.GetDirectoryName(profile.ConfPath));
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("代码目录")) Reveal(profile.OutputCodeDir);
                        if (GUILayout.Button("数据目录")) Reveal(profile.OutputDataDir);
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!canGenerate))
                            if (GUILayout.Button("生成这套"))
                                LubanBuildMenu.GenerateProfiles(new[] { profile });
                        if (GUILayout.Button("源目录")) Reveal(Path.GetDirectoryName(profile.ConfPath));
                        if (GUILayout.Button("代码目录")) Reveal(profile.OutputCodeDir);
                        if (GUILayout.Button("数据目录")) Reveal(profile.OutputDataDir);
                    }
                }
            }
        }

        private static void DrawValue(string label, string value, bool compact)
        {
            value = string.IsNullOrEmpty(value) ? "（未配置）" : value;
            if (!compact)
            {
                EditorGUILayout.LabelField(label, value);
                return;
            }

            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            GUILayout.Label(value, EditorStyles.wordWrappedMiniLabel);
        }

        // 工程相对路径 → 绝对路径后在资源管理器定位；尚未生成（目录不存在）时只提示、不报错。
        private static void Reveal(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return;
            if (!FrameworkProjectPath.TryResolve(projectRelative, out _, out string full, out string error))
            {
                Debug.LogWarning("[配置表构建] 无法定位：" + error);
                return;
            }
            if (Directory.Exists(full) || File.Exists(full)) EditorUtility.RevealInFinder(full);
            else Debug.LogWarning($"[配置表构建] 目录不存在（可能尚未生成）：{full}");
        }
    }
}
