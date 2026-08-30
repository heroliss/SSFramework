#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.Editor
{
#pragma warning disable CS0618 // 本文件专门迁移两个旧版组件。
    /// <summary>把旧版 Model + System + Utility 接线原子迁移为单个 AssetUtility。</summary>
    internal static class AssetRuntimeSetupMigration
    {
        private const string MenuPath = "GameObject/SSFramework/资源系统/迁移为 AssetUtility 单组件入口";

        [MenuItem(MenuPath, false, 30)]
        private static void MigrateSelection()
        {
            var candidates = new List<AssetSystemConfigModel>();
            var seen = new HashSet<int>();
            foreach (GameObject selected in Selection.gameObjects)
            {
                if (selected == null) continue;
                foreach (AssetSystemConfigModel config in selected.GetComponentsInChildren<AssetSystemConfigModel>(true))
                    if (config != null && seen.Add(config.GetInstanceID())) candidates.Add(config);
            }

            int migrated = 0;
            var errors = new List<string>();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("迁移资源系统为单组件入口");
            foreach (AssetSystemConfigModel config in candidates)
            {
                if (TryMigrate(config, recordUndo: true, out string error)) migrated++;
                else errors.Add(error);
            }
            Undo.CollapseUndoOperations(undoGroup);

            string details = migrated > 0
                ? $"已迁移 {migrated} 处资源系统接线；配置现在归 AssetUtility.Settings 所有。"
                : "当前选择及其子节点中没有可迁移的 AssetSystemConfigModel。";
            if (errors.Count > 0) details += "\n" + string.Join("\n", errors);
            FrameworkEditorFeedback.Level level = errors.Count > 0
                ? migrated > 0 ? FrameworkEditorFeedback.Level.Warning : FrameworkEditorFeedback.Level.Failure
                : migrated > 0 ? FrameworkEditorFeedback.Level.Success : FrameworkEditorFeedback.Level.Info;
            FrameworkEditorFeedback.Report("迁移资源系统", level, details, Selection.activeObject);
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateSelection() =>
            !Application.isPlaying && Selection.gameObjects.Length > 0;

        internal static bool TryMigrate(
            AssetSystemConfigModel legacyConfig,
            bool recordUndo,
            out string error,
            bool markSceneDirty = true)
        {
            error = null;
            if (Application.isPlaying)
            {
                error = "只能在 Edit Mode 迁移资源系统。";
                return false;
            }
            if (legacyConfig == null)
            {
                error = "迁移目标为空。";
                return false;
            }

            GameObject host = legacyConfig.gameObject;
            AssetUtility utility = host.GetComponent<AssetUtility>();
            if (utility == null)
            {
                error = $"{BuildPath(host)} 上缺少 AssetUtility；为避免把配置写到错误 Context，未自动猜测其它节点。";
                return false;
            }

            AssetRuntimeSettings settings = legacyConfig.ToRuntimeSettings();
            if (recordUndo) Undo.RecordObject(utility, "迁移资源运行配置");
            utility.ReplaceSettingsForEditorMigration(settings);
            EditorUtility.SetDirty(utility);

            AssetInitSystem legacyInitializer = host.GetComponent<AssetInitSystem>();
            if (legacyInitializer != null)
            {
                if (recordUndo) Undo.DestroyObjectImmediate(legacyInitializer);
                else UnityEngine.Object.DestroyImmediate(legacyInitializer);
            }
            if (recordUndo) Undo.DestroyObjectImmediate(legacyConfig);
            else UnityEngine.Object.DestroyImmediate(legacyConfig);

            if (markSceneDirty && host.scene.IsValid()) EditorSceneManager.MarkSceneDirty(host.scene);
            return true;
        }

        private static string BuildPath(GameObject gameObject)
        {
            string path = gameObject.name;
            for (Transform parent = gameObject.transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }

    /// <summary>让旧组件在 Inspector 中直接说明迁移方向，避免 Obsolete 只停留在编译器警告。</summary>
    [CustomEditor(typeof(AssetSystemConfigModel))]
    internal sealed class LegacyAssetSystemConfigInspector : FrameworkContextAwareInspector
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "这是旧版资源配置兼容组件。新结构只保留 AssetUtility，配置与自动初始化都由它负责。",
                MessageType.Warning);
            if (GUILayout.Button("迁移为 AssetUtility 单组件入口"))
            {
                if (!AssetRuntimeSetupMigration.TryMigrate(
                        (AssetSystemConfigModel)target,
                        recordUndo: true,
                        out string error))
                    FrameworkEditorFeedback.Report(
                        "迁移资源系统",
                        FrameworkEditorFeedback.Level.Failure,
                        error,
                        target);
                return;
            }
            base.OnInspectorGUI();
        }
    }
#pragma warning restore CS0618
}
#endif
