using UnityEditor;
using UnityEngine;

namespace Game.Framework.Build
{
    /// <summary>
    /// <see cref="FrameworkAssetBuildProfile"/> 的自定义 Inspector：在默认字段下加一个「同步收集器包列表」按钮 +
    /// 一段用法提示。这是对账的主入口（你正盯着 Packages 列表、最容易发现缺漏的地方）；菜单 <c>SSFramework/资源构建/同步收集器包列表</c> 是等价快捷入口。
    /// </summary>
    [CustomEditor(typeof(FrameworkAssetBuildProfile))]
    public sealed class FrameworkAssetBuildProfileEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "排除某个包用「BuildEnabled = 关」，不要删条目（删了会被下面的同步补回）。\n" +
                "误删条目 / 收集器里新增了包：点「同步收集器包列表」——按 YooAsset 收集器补缺、保留你已有的每包设置、孤儿仅警告不自动删。",
                MessageType.Info);

            if (GUILayout.Button("同步收集器包列表"))
            {
                var profile = (FrameworkAssetBuildProfile)target;
                string summary = profile.SyncFromCollector();
                Debug.Log("[资源构建] 同步收集器包列表：\n" + summary);
                EditorUtility.DisplayDialog("同步完成", summary, "好");
            }
        }
    }
}
