using System.IO;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Fonts.Editor
{
    /// <summary>字体常用字集的配置、生成与后续 TMP 烘焙指引。</summary>
    public sealed class FontCharsetWindow : EditorWindow
    {
        [MenuItem(FrameworkMenuPaths.FontCharset, priority = 44)]
        public static void Open() => GetWindow<FontCharsetWindow>("SSFramework 字体字集").Show();

        [InitializeOnLoadMethod]
        private static void RegisterEditorEntries()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "font-charset", FrameworkToolCategory.CodeGeneration, 50,
                "字体字集", "扫描项目文本生成去重字符文件，再交给 TMP Font Asset Creator 烘焙静态字体图集。",
                FrameworkMenuPaths.FontCharset));
            FrameworkConfigRegistry.Register(new FrameworkConfigDescriptor(
                "font-charset", 70, "字体（常用字集生成）", typeof(FontCharsetProfile), singleton: true,
                "全工程单例；只在工作台明确点击创建；输出字符文件供 TMP Font Asset Creator 烘焙静态字体图集。",
                FrameworkMenuPaths.FontCharset));
        }

        private Vector2 _scroll;

        private void OnEnable() => minSize = new Vector2(320, 380);

        private void OnGUI()
        {
            bool compact = position.width < 460f;
            bool canWrite = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true, out string operationReason);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("字体字集生成", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "字集生成只负责回答“哪些字符需要进静态图集”，不会自动修改 TMP Font Asset。这样可保留字体、图集尺寸、内边距（Padding）与渲染模式（Render Mode）的人工质量决策。",
                MessageType.Info);
            if (!canWrite)
                EditorGUILayout.HelpBox("当前不能创建配置或生成字集：\n" + operationReason, MessageType.Warning);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
                if (!FontCharsetProfile.TryResolve(out var profile))
                {
                    EditorGUILayout.HelpBox(
                        "尚无常用字集配置（Charset Profile）。点击创建会写入一个默认扫描 Assets、包含 ASCII 的项目配置资产。",
                        MessageType.Warning);
                    using (new EditorGUI.DisabledScope(!canWrite))
                        if (GUILayout.Button("创建默认字集配置")) FontCharsetMenu.LocateProfile();
                }
                else
                {
                    GUILayout.Label($"扫描目录：{string.Join(", ", profile.ScanDirs ?? System.Array.Empty<string>())}", EditorStyles.wordWrappedMiniLabel);
                    GUILayout.Label($"文件类型：{string.Join(", ", profile.FilePatterns ?? System.Array.Empty<string>())}", EditorStyles.wordWrappedMiniLabel);
                    GUILayout.Label($"输出：{profile.OutputPath}", EditorStyles.wordWrappedMiniLabel);
                    bool validOutput = FrameworkProjectPath.TryResolveAssetsFile(
                        profile.OutputPath, ".txt", out _, out string outputAbsolutePath, out string outputError);
                    if (!validOutput)
                        EditorGUILayout.HelpBox("输出路径无效：" + outputError, MessageType.Error);
                    if (compact)
                    {
                        if (GUILayout.Button("定位配置")) FontCharsetMenu.LocateProfile();
                        using (new EditorGUI.DisabledScope(!canWrite || !validOutput))
                            if (GUILayout.Button("生成常用字集", GUILayout.Height(28))) FontCharsetMenu.GenerateCharset();
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("定位配置")) FontCharsetMenu.LocateProfile();
                            using (new EditorGUI.DisabledScope(!canWrite || !validOutput))
                                if (GUILayout.Button("生成常用字集", GUILayout.Height(28))) FontCharsetMenu.GenerateCharset();
                        }
                    }

                    bool outputExists = validOutput && File.Exists(outputAbsolutePath);
                    using (new EditorGUI.DisabledScope(!outputExists))
                        if (GUILayout.Button(outputExists ? "定位已生成字集" : "尚未生成字集"))
                            EditorUtility.RevealInFinder(outputAbsolutePath);
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("下一步：烘焙 TMP 静态图集", EditorStyles.boldLabel);
                GUILayout.Label(
                    "打开 Window/TextMeshPro/Font Asset Creator 并选择字体源；在 Character Set（字符集）中选择 Characters from File（从文件读取字符），再引用上面的输出文件。烘焙后仍应在目标分辨率检查缺字、图集占用和 fallback 链。",
                    EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
