using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Network.Proto.Editor
{
    /// <summary>
    /// 「Protobuf 生成总览」窗口：把工程内所有 <see cref="ProtoConfigProfile"/> 集中成卡片——每套列出
    /// .proto 源目录（含文件数）、protoc 可用性、代码输出目录，并提供「生成这套 / 打开各目录 / 点名定位资产」。
    /// 多套并存（正式协议 + 框架测试等）时一眼看清各套落点、按套操作；顶部「生成全部」等同菜单「生成全部」，
    /// 「新建 Profile…」引导创建（本配置无自动创建——默认路径无从捏造）。
    /// </summary>
    public sealed class ProtoConfigOverviewWindow : EditorWindow
    {
        public static void Open() => GetWindow<ProtoConfigOverviewWindow>("Protobuf 生成总览").Show();

        private Vector2 _scroll;

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Protobuf 生成 · 总览", EditorStyles.boldLabel);
                if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
            }
            EditorGUILayout.HelpBox(
                "每套配置 = 一个 Proto Profile（各自的 .proto 源目录 + 生成 C# 输出目录），互不干扰。\n" +
                "生成消息类型经 GoogleProtobufNetworkSerializer（框架模块 Game.Framework.Network.Proto）接进网络接缝：" +
                "构造处 RegisterFile(生成的 XxxReflection.Descriptor) 即整文件注册。",
                MessageType.Info);

            var profiles = ProtoConfigProfile.ResolveAll();
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"共 {profiles.Count} 套", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("新建 Profile…", GUILayout.Width(100)))
                    CreateProfile();
                using (new EditorGUI.DisabledScope(playing || profiles.Count == 0))
                    if (GUILayout.Button("生成全部", GUILayout.Width(90)))
                        ProtoBuildMenu.GenerateProfiles(profiles);
            }
            if (playing)
                EditorGUILayout.LabelField("（运行中——停止后可生成）", EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (profiles.Count == 0)
                EditorGUILayout.HelpBox("还没有 Proto profile——点右上「新建 Profile…」创建，然后在 Inspector 填 .proto 源目录与输出目录。", MessageType.Warning);
            foreach (var profile in profiles)
                DrawCard(profile, playing);
            EditorGUILayout.EndScrollView();
        }

        // 一套配置一张卡片：资产名（点击定位选中）+ 源 / protoc / 输出 + 一排操作按钮。
        private static void DrawCard(ProtoConfigProfile profile, bool playing)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string path = AssetDatabase.GetAssetPath(profile);
                if (GUILayout.Button(new GUIContent(Path.GetFileName(path), path + "\n点击定位并选中"), EditorStyles.objectField))
                {
                    EditorGUIUtility.PingObject(profile);
                    Selection.activeObject = profile;
                }

                string protoDirFull = string.IsNullOrEmpty(profile.ProtoDir) ? null : Path.GetFullPath(profile.ProtoDir);
                int protoCount = protoDirFull != null && Directory.Exists(protoDirFull)
                    ? Directory.GetFiles(protoDirFull, "*.proto", SearchOption.AllDirectories).Length
                    : 0;
                EditorGUILayout.LabelField("源 (.proto)", string.IsNullOrEmpty(profile.ProtoDir)
                    ? "（未配置）"
                    : $"{profile.ProtoDir}（{protoCount} 个 .proto）");

                string protoc = ProtoCodeGenerator.ResolveProtocPath(
                    Path.GetDirectoryName(Application.dataPath), profile.ProtocDir);
                EditorGUILayout.LabelField("protoc", File.Exists(protoc) ? profile.ProtocDir : $"✗ 缺失：{protoc}");

                EditorGUILayout.LabelField("代码输出", string.IsNullOrEmpty(profile.OutputCodeDir) ? "（未配置）" : profile.OutputCodeDir);
                if (!string.IsNullOrEmpty(profile.ExtraArgs))
                    EditorGUILayout.LabelField("附加参数", profile.ExtraArgs);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(playing))
                        if (GUILayout.Button("生成这套"))
                            ProtoBuildMenu.GenerateProfiles(new[] { profile });
                    if (GUILayout.Button("源目录")) Reveal(profile.ProtoDir);
                    if (GUILayout.Button("输出目录")) Reveal(profile.OutputCodeDir);
                }
            }
        }

        private static void CreateProfile()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "新建 Proto Profile", "ProtoConfigProfile", "asset",
                "选择保存位置（推荐放协议所属模块的 Editor 目录下）");
            if (string.IsNullOrEmpty(path)) return;
            var profile = CreateInstance<ProtoConfigProfile>();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(profile);
            Selection.activeObject = profile;
        }

        // 工程相对路径 → 绝对路径后在资源管理器定位；尚未生成（目录不存在）时只提示、不报错。
        private static void Reveal(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return;
            string full = Path.GetFullPath(projectRelative);
            if (Directory.Exists(full) || File.Exists(full)) EditorUtility.RevealInFinder(full);
            else Debug.LogWarning($"[Protobuf 生成] 目录不存在（可能尚未生成）：{full}");
        }
    }
}
