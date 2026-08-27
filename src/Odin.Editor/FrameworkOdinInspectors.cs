using System;
using System.Collections.Generic;
using Game.Framework.Context;
using Game.Framework.Editor;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;
using Game.Framework.View;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Odin.Editor
{
    /// <summary>
    /// 把 Odin 的完整业务字段绘制与 Framework 运行时诊断组合起来。该 Adapter 只属于 Editor，删除后由
    /// Game.Framework.Editor 的原生 fallback Inspector 接管，不改变组件序列化布局或运行时行为。
    /// </summary>
    [CanEditMultipleObjects]
    public sealed class FrameworkOdinInspector : OdinEditor, IFrameworkMonoDiagnosticsOwner
    {
        public override void OnInspectorGUI()
        {
            if (targets.Length == 1)
                FrameworkMonoDiagnosticsGUI.DrawRuntimeDiagnostics(target);
            else
                EditorGUILayout.HelpBox(
                    "多选时仅绘制共有配置字段；Context、服务与模块诊断请单独选择一个组件查看。",
                    MessageType.Info);
            base.OnInspectorGUI();
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying || base.RequiresConstantRepaint();
    }

    /// <summary>
    /// Odin 会为每个具体业务类型生成精确匹配的 Editor，因此只在 Framework 基类上声明 CustomEditor 会被覆盖。
    /// 这里使用 Odin 官方的临时 CustomEditor 注册表，把“当前由 Framework fallback 接管、且 Odin 设置允许”的组件改由
    /// <see cref="FrameworkOdinInspector"/> 绘制。注册表随 Domain Reload 重建，不写 Odin 配置资产；删除本
    /// Assembly 后也不会留下失效的 Editor 类型记录。
    /// </summary>
    [InitializeOnLoad]
    internal static class FrameworkOdinEditorRegistration
    {
        private static bool _registerScheduled;

        static FrameworkOdinEditorRegistration()
        {
            ScheduleRegister();
        }

        private static void RegisterDelayed()
        {
            _registerScheduled = false;
            RegisterNow();
        }

        internal static void ScheduleRegister()
        {
            if (_registerScheduled) return;
            _registerScheduled = true;
            EditorApplication.delayCall += RegisterDelayed;
        }

        internal static void RegisterWithFeedback()
        {
            int count = RegisterNow();
            FrameworkEditorFeedback.ReportSummary(
                "Odin Inspector 适配已重新应用",
                $"当前由适配层接管 {count} 个 Framework 组件类型。此映射只存在于 Editor 内存，不写 Odin 配置或项目资产。");
        }

        /// <summary>
        /// 重新应用无持久化的 Editor 映射。只接管 Odin 自己判定会绘制的具体类型，显式选择 Unity Inspector
        /// 或使用其它业务 Editor 的类型不会被强制覆盖。
        /// </summary>
        internal static int RegisterNow()
        {
            InspectorConfig config = InspectorConfig.Instance;
            if (!CustomEditorUtility.IsValid || config == null) return 0;

            var candidates = new HashSet<Type>();
            AddCandidates<MonoGameContextBase>(candidates);
            AddCandidates<MonoModelBase>(candidates);
            AddCandidates<MonoSystemBase>(candidates);
            AddCandidates<MonoUtilityBase>(candidates);
            AddCandidates<MonoViewBase>(candidates);

            int registered = 0;
            foreach (Type type in candidates)
            {
                if (!IsFrameworkMonoType(type)) continue;
                Type currentEditor = InspectorTypeDrawingConfigDrawer.GetActualDrawingEditorForType(type);
                Type nativeEditor = GetNativeFrameworkFallback(type);
                bool adapterOwnsType = currentEditor == typeof(FrameworkOdinInspector);
                bool mayTakeOwnership = adapterOwnsType || currentEditor == nativeEditor || currentEditor == typeof(OdinEditor);
                if (!mayTakeOwnership) continue;

                if (config.EnableOdinInInspector && IsOdinEnabledForType(config, type))
                {
                    CustomEditorUtility.SetCustomEditor(
                        type, typeof(FrameworkOdinInspector), isFallbackEditor: false, isEditorForChildClasses: false);
                    registered++;
                }
                else if (adapterOwnsType)
                {
                    // Odin 设置变为禁用/排除时主动归还原生 Inspector，避免旧的内存映射一直残留到
                    // 下一次 Domain Reload。只撤回本 Adapter 当前持有的类型，不碰其它业务 Editor。
                    CustomEditorUtility.SetCustomEditor(
                        type, nativeEditor, isFallbackEditor: false, isEditorForChildClasses: false);
                }
            }
            return registered;
        }

        /// <summary>按 Odin 自己的逐类型覆盖和程序集分类判断当前类型是否应启用 Odin Inspector。</summary>
        internal static bool IsOdinEnabledForType(Type type)
        {
            InspectorConfig config = InspectorConfig.Instance;
            return config != null && config.EnableOdinInInspector && IsOdinEnabledForType(config, type);
        }

        private static bool IsOdinEnabledForType(InspectorConfig config, Type type)
        {
            InspectorTypeDrawingConfig drawing = config.DrawingConfig;
            if (drawing.HasEntryForType(type))
            {
                Type configuredEditor = drawing.GetEditorType(type);
                // Odin 的标准逐类型选择写入 OdinEditor。若项目配置了其它 OdinEditor 子类，它已经是一项
                // 有意的业务 Editor 选择，Adapter 不应把它替换掉。
                return configuredEditor == typeof(OdinEditor);
            }

            AssemblyTypeFlags assemblyType = AssemblyUtilities.GetAssemblyTypeFlag(type.Assembly);
            InspectorDefaultEditors category = InspectorDefaultEditors.None;
            if ((assemblyType & AssemblyTypeFlags.UserTypes) != 0)
                category |= InspectorDefaultEditors.UserTypes;
            if ((assemblyType & AssemblyTypeFlags.PluginTypes) != 0)
                category |= InspectorDefaultEditors.PluginTypes;
            if ((assemblyType & AssemblyTypeFlags.UnityTypes) != 0)
                category |= InspectorDefaultEditors.UnityTypes;
            if ((assemblyType & AssemblyTypeFlags.OtherTypes) != 0)
                category |= InspectorDefaultEditors.OtherTypes;
            return category != InspectorDefaultEditors.None &&
                   (config.DefaultEditorBehaviour & category) != 0;
        }

        private static Type GetNativeFrameworkFallback(Type inspectedType)
        {
            if (typeof(MonoGameContextBase).IsAssignableFrom(inspectedType))
                return typeof(MonoGameContextInspector);
            if (typeof(MonoModelBase).IsAssignableFrom(inspectedType))
                return typeof(MonoModelInspector);
            if (typeof(MonoSystemBase).IsAssignableFrom(inspectedType))
                return typeof(MonoSystemInspector);
            if (typeof(MonoUtilityBase).IsAssignableFrom(inspectedType))
                return typeof(MonoUtilityInspector);
            return typeof(MonoViewInspector);
        }

        private static void AddCandidates<T>(ISet<Type> candidates) where T : UnityEngine.Object
        {
            foreach (Type type in TypeCache.GetTypesDerivedFrom<T>())
                candidates.Add(type);
        }

        private static bool IsFrameworkMonoType(Type type)
        {
            if (type == null || type.IsAbstract || type.IsGenericTypeDefinition) return false;
            return typeof(MonoGameContextBase).IsAssignableFrom(type) ||
                   typeof(MonoModelBase).IsAssignableFrom(type) ||
                   typeof(MonoSystemBase).IsAssignableFrom(type) ||
                   typeof(MonoUtilityBase).IsAssignableFrom(type) ||
                   typeof(MonoViewBase).IsAssignableFrom(type);
        }
    }

    /// <summary>
    /// Odin 更新 Editor Types 时会重建临时 CustomEditor 表。只监听其 InspectorConfig 资产的保存/导入，
    /// 在本轮 Editor 回调结束后重应用 Adapter；不因普通项目资产变化反复抢占其它动态 Editor 工具。
    /// </summary>
    internal sealed class FrameworkOdinSettingsSaveWatcher : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            FrameworkOdinSettingsWatcher.ScheduleIfInspectorConfig(paths);
            return paths;
        }
    }

    internal sealed class FrameworkOdinSettingsImportWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            FrameworkOdinSettingsWatcher.ScheduleIfInspectorConfig(importedAssets);
        }
    }

    internal static class FrameworkOdinSettingsWatcher
    {
        internal static void ScheduleIfInspectorConfig(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            string configPath = AssetDatabase.GetAssetPath(InspectorConfig.Instance);
            if (string.IsNullOrEmpty(configPath)) return;
            foreach (string path in paths)
            {
                if (!string.Equals(path, configPath, StringComparison.Ordinal)) continue;
                FrameworkOdinEditorRegistration.ScheduleRegister();
                return;
            }
        }
    }
}
