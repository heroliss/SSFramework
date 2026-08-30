using System.IO;
using System.Collections.Generic;
using System.Linq;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Network.Proto.Editor
{
    /// <summary>
    /// 「Protobuf 生成总览」窗口：把工程内所有 <see cref="ProtoConfigProfile"/> 集中成卡片——每套列出
    /// .proto 源目录（含文件数）、protoc 可用性、代码输出目录，并提供「生成这套 / 打开各目录 / 点名定位资产」。
    /// 多套并存（正式协议 + 框架测试等）时一眼看清各套落点、按套操作；顶部批量按钮只提交当前已就绪配置，
    /// 「新建 Protobuf 配置…」引导创建（本配置无自动创建——默认路径无从捏造）。
    /// </summary>
    public sealed class ProtoConfigOverviewWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.Protobuf, priority = 41)]
        public static void Open() => GetWindow<ProtoConfigOverviewWindow>("Protobuf 生成总览").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "protobuf", FrameworkToolCategory.CodeGeneration, 20,
                "Protobuf", "管理多套 .proto 源与 C# 输出，检查 protoc 可用性并执行差量生成。",
                FrameworkMenuPaths.Protobuf));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "protobuf", 40, "网络协议（protoc 生成）", typeof(ProtoConfigProfile), singleton: false,
                "可按协议域并存多套；每套显式指定 .proto 源目录与独占的 C# 输出目录。",
                FrameworkMenuPaths.Protobuf));
            FrameworkGeneratedOutputClaimCatalog.Register(new FrameworkGeneratedOutputClaimSource(
                ProtoCodeGenerator.OutputClaimSourceId,
                "网络协议（Protobuf）",
                ProtoCodeGenerator.CollectRegisteredOutputClaims));
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
                EditorGUILayout.LabelField("Protobuf 生成 · 总览", EditorStyles.boldLabel);
                if (GUILayout.Button(new GUIContent("重新扫描", "重新发现 Profile 并读取当前 .proto 目录")))
                    RefreshInputPreview();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Protobuf 生成 · 总览", EditorStyles.boldLabel);
                    if (GUILayout.Button(
                            new GUIContent("重新扫描", "重新发现 Profile 并读取当前 .proto 目录"),
                            GUILayout.Width(82)))
                        RefreshInputPreview();
                }
            }
            EditorGUILayout.HelpBox(
                "每套 Protobuf 配置包含一个 .proto 源目录和一个独占的 C# 输出目录。输出必须位于 Assets 的独立子目录；" +
                "生成器会递归清理其中本次未产出的 *.g.cs，因此不同配置不能共用或嵌套目录。\n" +
                "卡片复用当前工程 revision 的输入快照；点“重新扫描”可立即核对磁盘，真正生成前仍会独立重验。\n" +
                "生成消息类型经 GoogleProtobufNetworkSerializer（框架模块 Game.Framework.Network.Proto）接进网络接缝：" +
                "构造处 RegisterFile(生成的 XxxReflection.Descriptor) 即整文件注册。",
                MessageType.Info);
            if (!canWrite)
                EditorGUILayout.HelpBox("当前不能新建或生成：\n" + operationReason, MessageType.Warning);

            var profiles = ProtoConfigProfile.ResolveAll();
            var (ownershipOk, ownershipMessage) = profiles.Count == 0
                ? (false, string.Empty)
                : ProtoCodeGenerator.ValidateOutputOwnership(profiles);
            var prerequisites = new Dictionary<ProtoConfigProfile, ProtoCodeGenerator.GenerationPrerequisiteReport>();
            var previewAvailable = new Dictionary<ProtoConfigProfile, bool>();
            foreach (ProtoConfigProfile profile in profiles)
            {
                bool available = ProtoCodeGenerator.TryGetGenerationPrerequisitePreview(
                    profile, out ProtoCodeGenerator.GenerationPrerequisiteReport report);
                previewAvailable.Add(profile, available);
                prerequisites.Add(profile, available
                    ? report
                    : new ProtoCodeGenerator.GenerationPrerequisiteReport(
                        false, "输入快照尚未采集或已失效；点击顶部“重新扫描”。"));
            }
            var readyProfiles = profiles
                .Where(profile => previewAvailable[profile] && prerequisites[profile].CanGenerate)
                .ToArray();
            int readyCount = readyProfiles.Length;
            int pendingPreviewCount = previewAvailable.Count(pair => !pair.Value);
            bool canGenerateAny = canWrite && ownershipOk && readyCount > 0;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (compact)
            {
                EditorGUILayout.LabelField(
                    FormatCountSummary(profiles.Count, ownershipOk, readyCount),
                    EditorStyles.miniBoldLabel);
                using (new EditorGUI.DisabledScope(!canWrite))
                    if (GUILayout.Button("新建 Protobuf 配置…"))
                        CreateProfile();
                using (new EditorGUI.DisabledScope(!canGenerateAny))
                    if (GUILayout.Button(FormatBatchButtonLabel(profiles.Count, ownershipOk, readyCount)))
                        ProtoBuildMenu.GenerateProfiles(readyProfiles);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        FormatCountSummary(profiles.Count, ownershipOk, readyCount),
                        EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(!canWrite))
                        if (GUILayout.Button("新建 Protobuf 配置…", GUILayout.Width(150)))
                            CreateProfile();
                    using (new EditorGUI.DisabledScope(!canGenerateAny))
                        if (GUILayout.Button(
                            FormatBatchButtonLabel(profiles.Count, ownershipOk, readyCount),
                            GUILayout.MinWidth(90)))
                            ProtoBuildMenu.GenerateProfiles(readyProfiles);
                }
            }
            if (profiles.Count == 0)
                EditorGUILayout.HelpBox("还没有 Protobuf 配置——点右上“新建 Protobuf 配置…”创建，然后在 Inspector 填写 .proto 源目录与输出目录。", MessageType.Warning);
            else if (!ownershipOk)
                EditorGUILayout.HelpBox("输出目录预检未通过：\n" + ownershipMessage, MessageType.Error);
            else if (pendingPreviewCount > 0)
                EditorGUILayout.HelpBox(
                    $"有 {pendingPreviewCount} 套输入快照尚未采集或已失效；点击顶部“重新扫描”后才会递归读取 .proto 目录。",
                    MessageType.Info);
            else if (readyCount == 0)
                EditorGUILayout.HelpBox(
                    "当前没有可生成的配置；请按卡片提示补齐 protoc、源文件与输出目录。",
                    MessageType.Warning);
            else
                EditorGUILayout.LabelField("✓ " + ownershipMessage, EditorStyles.miniLabel);
            foreach (var profile in profiles)
                DrawCard(
                    profile,
                    prerequisites[profile],
                    previewAvailable[profile],
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

        // 一套配置一张卡片：资产名（点击定位选中）+ 源 / protoc / 输出 + 响应式操作区。
        private static void DrawCard(
            ProtoConfigProfile profile,
            ProtoCodeGenerator.GenerationPrerequisiteReport prerequisites,
            bool previewAvailable,
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

                DrawValue("源 (.proto)", string.IsNullOrEmpty(profile.ProtoDir)
                    ? "（未配置）"
                    : prerequisites.ProtoFileCount > 0
                        ? $"{profile.ProtoDir}（{prerequisites.ProtoFileCount} 个 .proto）"
                        : profile.ProtoDir, compact);

                DrawValue("protoc", profile.ProtocDir, compact);

                DrawValue("代码输出", string.IsNullOrEmpty(profile.OutputCodeDir) ? "（未配置）" : profile.OutputCodeDir, compact);
                if (!string.IsNullOrEmpty(profile.ExtraArgs))
                    DrawValue("附加参数", profile.ExtraArgs, compact);

                if (!previewAvailable)
                    EditorGUILayout.HelpBox(prerequisites.Message, MessageType.Info);
                else if (!prerequisites.CanGenerate)
                    EditorGUILayout.HelpBox(
                        "当前配置不能生成：\n" + prerequisites.Message,
                        MessageType.Warning);

                if (compact)
                {
                    using (new EditorGUI.DisabledScope(!canGenerate))
                        if (GUILayout.Button("生成这套"))
                            ProtoBuildMenu.GenerateProfiles(new[] { profile });
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("源目录")) Reveal(profile.ProtoDir);
                        if (GUILayout.Button("输出目录")) Reveal(profile.OutputCodeDir);
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!canGenerate))
                            if (GUILayout.Button("生成这套"))
                                ProtoBuildMenu.GenerateProfiles(new[] { profile });
                        if (GUILayout.Button("源目录")) Reveal(profile.ProtoDir);
                        if (GUILayout.Button("输出目录")) Reveal(profile.OutputCodeDir);
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

        private static void CreateProfile()
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("创建 Protobuf 配置")) return;
            string path = EditorUtility.SaveFilePanelInProject(
                "新建 Protobuf 配置", "ProtoConfigProfile", "asset",
                "选择保存位置（推荐放协议所属模块的 Editor 目录下）");
            if (string.IsNullOrEmpty(path)) return;
            var profile = CreateInstance<ProtoConfigProfile>();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(profile);
            Selection.activeObject = profile;
        }

        private static void RefreshInputPreview()
        {
            FrameworkEditorProfileCatalog.Refresh(new[] { typeof(ProtoConfigProfile) });
            FrameworkGeneratedOutputClaimCatalog.Invalidate();
            ProtoCodeGenerator.RefreshGenerationPrerequisitePreviews(ProtoConfigProfile.ResolveAll());
        }

        // 工程相对路径 → 绝对路径后在资源管理器定位；尚未生成（目录不存在）时只提示、不报错。
        private static void Reveal(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return;
            if (!FrameworkProjectPath.TryResolve(projectRelative, out _, out string full, out string error))
            {
                Debug.LogWarning("[Protobuf 生成] 无法定位：" + error);
                return;
            }
            if (Directory.Exists(full) || File.Exists(full)) EditorUtility.RevealInFinder(full);
            else Debug.LogWarning($"[Protobuf 生成] 目录不存在（可能尚未生成）：{full}");
        }
    }
}
