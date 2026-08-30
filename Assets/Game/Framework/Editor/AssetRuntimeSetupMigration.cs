#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Framework.Context;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.Editor
{
#pragma warning disable CS0618 // 本文件专门迁移两个旧版组件。
    /// <summary>先预检旧版 Model + System + Utility 接线，再迁移为单个 AssetUtility。</summary>
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
            if (EditorUtility.IsPersistent(legacyConfig))
            {
                error = $"{host.name} 是 Project 中的 Prefab 资产，迁移器不会直接修改持久化资产内容；" +
                        "请先双击 Prefab 进入 Prefab Mode，再执行迁移。";
                return false;
            }

            AssetUtility utility = host.GetComponent<AssetUtility>();
            if (utility == null)
            {
                error = $"{BuildPath(host)} 上缺少 AssetUtility；为避免把配置写到错误 Context，未自动猜测其它节点。";
                return false;
            }

            MonoGameContextBase configContext = legacyConfig.ResolveContextHostForEditor();
            MonoGameContextBase utilityContext = utility.ResolveContextHostForEditor();
            if (configContext != utilityContext)
            {
                error = $"{BuildPath(host)} 上的 AssetSystemConfigModel 与 AssetUtility 指向不同 Context；" +
                        "为避免把配置迁入错误作用域，未做任何修改。";
                return false;
            }
            if (configContext != null && configContext.gameObject.scene != host.scene)
            {
                error = $"{BuildPath(host)} 指向了其它 Scene 中的 Context；" +
                        "迁移器不会跨 Scene 删除旧组件，未做任何修改。";
                return false;
            }
            if (configContext != null &&
                (HasScopedComponentOutsideScene<AssetSystemConfigModel>(
                     host,
                     configContext,
                     config => config.ResolveContextHostForEditor()) ||
                 HasScopedComponentOutsideScene<AssetInitSystem>(
                     host,
                     configContext,
                     initializer => initializer.ResolveContextHostForEditor()) ||
                 HasScopedComponentOutsideScene<AssetUtility>(
                     host,
                     configContext,
                     candidate => candidate.ResolveContextHostForEditor())))
            {
                error = $"Context“{BuildPath(configContext.gameObject)}”的旧资源组件分布在多个 Scene；" +
                        "迁移器不会跨 Scene 修改对象，未做任何修改。请分别整理场景接线后再迁移。";
                return false;
            }
            if (configContext == null &&
                (HasUnscopedPeerOutsideHost<AssetSystemConfigModel>(
                     host,
                     config => config.ResolveContextHostForEditor()) ||
                 HasUnscopedPeerOutsideHost<AssetInitSystem>(
                     host,
                     initializer => initializer.ResolveContextHostForEditor()) ||
                 HasUnscopedPeerOutsideHost<AssetUtility>(
                     host,
                     candidate => candidate.ResolveContextHostForEditor())))
            {
                error = $"{BuildPath(host)} 没有明确的 Context 归属，且当前已加载 Scene 中还有其它无宿主的旧资源组件；" +
                        "无法判断它们运行时是否共同回退到 GameContext.Main，未做任何修改。" +
                        "请关闭无关 Scene、把同一套组件放到一个节点，或显式指定 Context。";
                return false;
            }

            List<AssetSystemConfigModel> legacyConfigs = FindComponentsInScope<AssetSystemConfigModel>(
                host,
                configContext,
                config => config.ResolveContextHostForEditor());
            if (legacyConfigs.Count != 1 || legacyConfigs[0] != legacyConfig)
            {
                string scope = configContext != null
                    ? $"Context“{BuildPath(configContext.gameObject)}”"
                    : $"节点“{BuildPath(host)}”";
                error = $"{scope} 中检测到 {legacyConfigs.Count} 个 AssetSystemConfigModel；" +
                        "无法唯一判断旧初始化器的归属，未做任何修改。请先保留一套旧资源配置再迁移。";
                return false;
            }

            List<AssetUtility> utilities = FindComponentsInScope<AssetUtility>(
                host,
                configContext,
                candidate => candidate.ResolveContextHostForEditor());
            if (utilities.Count != 1 || utilities[0] != utility)
            {
                string scope = configContext != null
                    ? $"Context“{BuildPath(configContext.gameObject)}”"
                    : $"节点“{BuildPath(host)}”";
                error = $"{scope} 中检测到 {utilities.Count} 个 AssetUtility；" +
                        "单入口迁移要求作用域内恰好保留同节点这一份 Utility，未做任何修改。";
                return false;
            }

            List<AssetInitSystem> legacyInitializers = FindComponentsInScope<AssetInitSystem>(
                host,
                configContext,
                initializer => initializer.ResolveContextHostForEditor());
            foreach (AssetInitSystem coLocated in host.GetComponents<AssetInitSystem>())
            {
                if (legacyInitializers.Contains(coLocated)) continue;
                error = $"{BuildPath(host)} 上的 AssetInitSystem 指向不同 Context；" +
                        "为避免留下或删除错误作用域的旧组件，未做任何修改。";
                return false;
            }

            AssetRuntimeSettings settings = legacyConfig.ToRuntimeSettings();
            if (recordUndo) Undo.RecordObject(utility, "迁移资源运行配置");
            utility.ReplaceSettingsForEditorMigration(settings);
            EditorUtility.SetDirty(utility);

            foreach (AssetInitSystem legacyInitializer in legacyInitializers)
            {
                if (recordUndo) Undo.DestroyObjectImmediate(legacyInitializer);
                else UnityEngine.Object.DestroyImmediate(legacyInitializer);
            }
            if (recordUndo) Undo.DestroyObjectImmediate(legacyConfig);
            else UnityEngine.Object.DestroyImmediate(legacyConfig);

            if (markSceneDirty && host.scene.IsValid()) EditorSceneManager.MarkSceneDirty(host.scene);
            return true;
        }

        private static List<T> FindComponentsInScope<T>(
            GameObject host,
            MonoGameContextBase contextHost,
            Func<T, MonoGameContextBase> resolveContext) where T : Component
        {
            var result = new List<T>();
            if (!host.scene.IsValid())
            {
                AddMatching(host.GetComponents<T>());
                return result;
            }

            foreach (GameObject root in host.scene.GetRootGameObjects())
                AddMatching(root.GetComponentsInChildren<T>(includeInactive: true));
            return result;

            void AddMatching(IEnumerable<T> candidates)
            {
                foreach (T candidate in candidates)
                {
                    if (candidate == null) continue;
                    MonoGameContextBase candidateContext = resolveContext(candidate);
                    bool sameScope = contextHost != null
                        ? candidateContext == contextHost
                        : candidate.gameObject == host && candidateContext == null;
                    if (sameScope) result.Add(candidate);
                }
            }
        }

        private static bool HasUnscopedPeerOutsideHost<T>(
            GameObject host,
            Func<T, MonoGameContextBase> resolveContext) where T : Component
        {
            foreach (T candidate in EnumerateLoadedStageComponents<T>(host))
                if (candidate != null && candidate.gameObject != host && resolveContext(candidate) == null)
                    return true;
            return false;
        }

        private static bool HasScopedComponentOutsideScene<T>(
            GameObject host,
            MonoGameContextBase contextHost,
            Func<T, MonoGameContextBase> resolveContext) where T : Component
        {
            foreach (T candidate in EnumerateLoadedStageComponents<T>(host))
                if (candidate.gameObject.scene != host.scene && resolveContext(candidate) == contextHost)
                    return true;
            return false;
        }

        private static IEnumerable<T> EnumerateLoadedStageComponents<T>(GameObject host) where T : Component
        {
            var hostPrefabStage = PrefabStageUtility.GetPrefabStage(host);
            foreach (T candidate in Resources.FindObjectsOfTypeAll<T>())
            {
                if (candidate == null || EditorUtility.IsPersistent(candidate)) continue;
                if (!candidate.gameObject.scene.IsValid()) continue;
                var candidatePrefabStage = PrefabStageUtility.GetPrefabStage(candidate.gameObject);
                bool sameStage = hostPrefabStage != null
                    ? candidatePrefabStage == hostPrefabStage
                    : candidatePrefabStage == null &&
                      !EditorSceneManager.IsPreviewScene(candidate.gameObject.scene);
                if (!sameStage) continue;
                yield return candidate;
            }
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
