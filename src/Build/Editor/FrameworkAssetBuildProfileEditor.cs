using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// <see cref="FrameworkAssetBuildProfile"/> 的自定义 Inspector：说明本 Module 只构建普通 AssetBundle 包，
    /// 默认字段下加一个「同步收集器包列表」按钮 + 用法提示。这是对账的主入口（你正盯着 Packages 列表、最容易发现缺漏的地方）；
    /// 资源构建工作台提供等价入口。
    /// </summary>
    [CustomEditor(typeof(FrameworkAssetBuildProfile))]
    public sealed class FrameworkAssetBuildProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "本配置只驱动普通 AssetBundle 包。使用 PackRawFile 的代码包、视频包等应关闭“参与构建”，" +
                "再交给拥有对应 RawFile 配方的独立构建模块；误把它们启用时，构建会在写产物前明确失败。",
                MessageType.Info);

            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "排除某个包用「参与构建 = 关」，不要删条目（删了会被下面的同步补回）。\n" +
                "误删条目 / 收集器里新增了包：点「同步收集器包列表」——按 YooAsset 收集器补缺、保留你已有的每包设置、孤儿仅警告不自动删。",
                MessageType.Info);

            bool canWrite = FrameworkEditorOperationGate.CanStart(
                requireEditMode: true, out string operationReason);
            if (!canWrite)
                EditorGUILayout.HelpBox(
                    "当前不能同步配置或生成常量：\n" + operationReason,
                    MessageType.Warning);

            using (new EditorGUI.DisabledScope(!canWrite))
            {
                if (GUILayout.Button("同步收集器包列表"))
                {
                    if (!FrameworkEditorOperationGate.EnsureCanStart("同步资源包列表")) return;
                    var profile = (FrameworkAssetBuildProfile)target;
                    string summary = profile.SyncFromCollector();
                    FrameworkEditorFeedback.ReportSummary("同步资源包列表", summary, profile);
                }

                if (GUILayout.Button("生成包名与构建常量代码"))
                {
                    if (!FrameworkEditorOperationGate.EnsureCanStart("生成资源包名与构建常量")) return;
                    var profile = (FrameworkAssetBuildProfile)target;
                    var (ok, message) = AssetPackageConstantsGenerator.Generate(profile);
                    FrameworkEditorFeedback.ReportResult("生成资源包名与构建常量", ok, message, profile);
                }
            }
        }
    }
}
