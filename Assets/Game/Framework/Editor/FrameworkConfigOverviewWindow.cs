using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 「框架配置总览」hub（菜单 <c>SSFramework/配置总览</c>）：把框架各模块的配置 profile 资产聚合到一页——
    /// 每模块一节，列出找到的资产（点路径定位选中）、标注单份 / 多份语义并做健康检查（单例类找到多份黄条警告），
    /// 并提供跳转到模块专用总览 / 配置入口的按钮。解决「配置资产散落各目录、不知道有哪些 / 在哪」的问题；
    /// 生成 / 构建等操作不在这里做，仍在各模块子菜单与专用总览里。
    /// </summary>
    /// <remarks>
    /// 刻意用「字符串类型名 + <c>FindAssets</c> / <c>Type.GetType</c>」发现资产、菜单路径字符串跳转，
    /// 不编译期引用各模块 editor 程序集：本窗口在通用编辑器程序集（Game.Framework.Editor），反向引用
    /// Build.Editor / Config.Editor / UI.UGui.Editor 会破坏「模块整块可删」的程序集边界。
    /// 模块删除 → 类型不存在 → 对应节自动隐藏。代价：新增配置类型 / 改菜单路径要同步 <see cref="Sections"/> 表。
    /// </remarks>
    public sealed class FrameworkConfigOverviewWindow : EditorWindow
    {
        [MenuItem("SSFramework/配置总览", priority = 0)]
        public static void Open() => GetWindow<FrameworkConfigOverviewWindow>("框架配置总览").Show();

        /// <summary>一节 = 一类配置 profile 的登记信息。</summary>
        private sealed class Section
        {
            public string Title;         // 节标题：模块 + 配置用途
            public string TypeName;      // AssetDatabase.FindAssets("t:...") 用短类型名
            public string QualifiedType; // 「命名空间.类型, 程序集」——判断模块是否还在工程里
            public bool Singleton;       // true = 全工程应恰一份（多份 → 警告）
            public string Note;          // 单/多份语义与创建方式，一句话
            public string JumpMenu;      // 模块专用总览 / 配置菜单路径
            public string JumpLabel;
            public string SubTypeName;   // 附属配置类型（可空，如 UI 目录级覆盖）
            public string SubLabel;
        }

        private static readonly Section[] Sections =
        {
            new()
            {
                Title = "服务注册（安装器生成）",
                TypeName = "ServiceInstallerProfile",
                QualifiedType = "Game.Framework.Editor.ServiceInstallerProfile, Game.Framework.Editor",
                Singleton = false,
                Note = "多份并存（demo / 正式项目各一份）；无自动创建，经 Assets/Create/SSFramework/服务安装器配置 建。",
                JumpMenu = "SSFramework/服务注册/配置总览", JumpLabel = "打开总览",
            },
            new()
            {
                Title = "UI 绑定（代码生成）",
                TypeName = "UICodeGenProfile",
                QualifiedType = "Game.Framework.UI.UGui.Editor.UICodeGenProfile, Game.Framework.UI.UGui.Editor",
                Singleton = true,
                Note = "全工程单例（首次使用自动创建）；目录级差异用 UICodeGenDirConfig 就近覆盖，不建第二份 Profile。",
                JumpMenu = "SSFramework/UI 绑定/配置总览", JumpLabel = "打开总览",
                SubTypeName = "UICodeGenDirConfig", SubLabel = "目录级覆盖",
            },
            new()
            {
                Title = "配置表（Luban 生成）",
                TypeName = "LubanConfigProfile",
                QualifiedType = "Game.Framework.Build.LubanConfigProfile, Game.Framework.Config.Editor",
                Singleton = false,
                Note = "多套并存（demo / 正式游戏各一套，各自 luban.conf 源与输出）；首次生成时自动创建。",
                JumpMenu = "SSFramework/配置表构建/配置总览 (定位 · 打开目录 · 生成)", JumpLabel = "打开总览",
            },
            new()
            {
                Title = "资源构建",
                TypeName = "FrameworkAssetBuildProfile",
                QualifiedType = "Game.Framework.Build.FrameworkAssetBuildProfile, Game.Framework.Build.Editor",
                Singleton = true,
                Note = "全工程单例（首次使用自动创建，按收集器包列表初始化）。",
                JumpMenu = "SSFramework/资源构建/构建配置 (Build Profile)", JumpLabel = "打开配置",
            },
            new()
            {
                Title = "热更构建",
                TypeName = "FrameworkHotUpdateProfile",
                QualifiedType = "Game.Framework.Build.FrameworkHotUpdateProfile, Game.Framework.Build.Editor",
                Singleton = true,
                Note = "全工程单例（首次使用自动创建，默认档位 = 内核 + Asset.Yoo 热更）。",
                JumpMenu = "SSFramework/热更构建/热更配置 (HotUpdate Profile)", JumpLabel = "打开配置",
            },
            new()
            {
                Title = "字体（常用字集生成）",
                TypeName = "FontCharsetProfile",
                QualifiedType = "Game.Framework.Fonts.Editor.FontCharsetProfile, Game.Framework.Fonts.Editor",
                Singleton = true,
                Note = "全工程单例（首次使用自动创建）；产出 charset 文件喂 TMP Font Asset Creator 烘焙主字体。",
                JumpMenu = "SSFramework/字体/常用字集配置 (Charset Profile)", JumpLabel = "打开配置",
            },
        };

        private Vector2 _scroll;

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("框架配置 · 全模块总览", EditorStyles.boldLabel);
                if (GUILayout.Button("刷新", GUILayout.Width(60))) Repaint();
            }
            EditorGUILayout.HelpBox(
                "框架各模块配置 profile 的一页清单：点路径定位选中资产；单例类找到多份会警告。\n" +
                "生成 / 构建等操作在各模块子菜单与专用总览里，这里只回答「有哪些配置、在哪、健康与否」。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var section in Sections)
                DrawSection(section);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawSection(Section section)
        {
            // 模块（含其 editor 程序集）被整块删除时类型不存在——整节隐藏，不显示误导性的「0 份」。
            if (Type.GetType(section.QualifiedType) == null) return;

            var paths = FindPaths(section.TypeName);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{section.Title} — {paths.Count} 份", EditorStyles.boldLabel);
                    if (GUILayout.Button(section.JumpLabel, GUILayout.Width(90)) &&
                        !EditorApplication.ExecuteMenuItem(section.JumpMenu))
                        Debug.LogWarning($"[配置总览] 菜单不存在（改名了？请同步节表）：{section.JumpMenu}");
                }
                EditorGUILayout.LabelField(section.Note, EditorStyles.miniLabel);

                if (section.Singleton && paths.Count > 1)
                    EditorGUILayout.HelpBox("找到多份，仅第一份生效——请删到只剩一份。", MessageType.Warning);

                if (paths.Count == 0)
                    EditorGUILayout.LabelField("（未找到）", EditorStyles.miniLabel);
                foreach (string path in paths)
                    DrawAssetRow(path);

                if (section.SubTypeName != null)
                {
                    var subPaths = FindPaths(section.SubTypeName);
                    EditorGUILayout.LabelField($"{section.SubLabel}（{subPaths.Count}）", EditorStyles.miniBoldLabel);
                    foreach (string path in subPaths)
                        DrawAssetRow(path);
                }
            }
            EditorGUILayout.Space(4);
        }

        private static List<string> FindPaths(string typeName) =>
            AssetDatabase.FindAssets("t:" + typeName)
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        private static void DrawAssetRow(string path)
        {
            if (GUILayout.Button(new GUIContent(path, "点击定位并选中"), EditorStyles.objectField))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
        }
    }
}
