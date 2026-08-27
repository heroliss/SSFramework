using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// <see cref="FrameworkAssetBuildProfile"/> 的自定义 Inspector：顶部提示代码包由热更管线负责（资源构建恒跳过），
    /// 默认字段下加一个「同步收集器包列表」按钮 + 用法提示。这是对账的主入口（你正盯着 Packages 列表、最容易发现缺漏的地方）；
    /// 资源构建工作台提供等价入口。
    /// </summary>
    [CustomEditor(typeof(FrameworkAssetBuildProfile))]
    public sealed class FrameworkAssetBuildProfileEditor : UnityEditor.Editor
    {
        // 打开 Inspector 时解析一次代码包名（Resolve 是工程级 FindAssets，不放每帧重绘里跑）。
        private string _codePackageName;

        private void OnEnable() =>
            _codePackageName = FrameworkHotUpdateProfile.TryResolve(out var profile)
                ? profile.CodePackageName
                : "CodePackage（尚未创建热更配置）";

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                $"代码包「{_codePackageName}」由“SSFramework/构建与发布/代码热更新”工作台负责，资源构建恒跳过它——" +
                "构建器按包名识别排除，不依赖「参与构建」开关；列表里该条目的「参与构建 / 首包 / 内置 shader」等设置一律无效。",
                MessageType.Info);

            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "排除某个【业务】包用「BuildEnabled = 关」，不要删条目（删了会被下面的同步补回）。\n" +
                "误删条目 / 收集器里新增了包：点「同步收集器包列表」——按 YooAsset 收集器补缺、保留你已有的每包设置、孤儿仅警告不自动删。",
                MessageType.Info);

            if (GUILayout.Button("同步收集器包列表"))
            {
                if (!FrameworkEditorOperationGate.EnsureCanStart("同步资源包列表")) return;
                var profile = (FrameworkAssetBuildProfile)target;
                string summary = profile.SyncFromCollector();
                FrameworkEditorFeedback.ReportSummary("同步资源包列表", summary, profile);
            }

            if (GUILayout.Button("生成包名常量代码"))
            {
                if (!FrameworkEditorOperationGate.EnsureCanStart("生成资源包名常量")) return;
                var profile = (FrameworkAssetBuildProfile)target;
                var (ok, message) = AssetPackageConstantsGenerator.Generate(profile);
                FrameworkEditorFeedback.ReportResult("生成资源包名常量", ok, message, profile);
            }
        }
    }
}
