#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Framework.Audio;
using Game.Framework.Context;
using Game.Framework.Diagnostics;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.Pool;
using Game.Framework.Storage;
using Game.Framework.Systems;
using Game.Framework.Utility;
using Game.Framework.View;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 可选 Runtime Module 向通用 Inspector 追加诊断的 Editor-only 接缝。贡献方只依赖通用 Editor，Core/Odin
    /// Inspector 统一调用这里；通用 Editor 不反向引用具体 Module。
    /// </summary>
    public static class FrameworkInspectorDiagnostics
    {
        private static readonly Dictionary<Type, Action<UnityEngine.Object>> Contributors = new();

        /// <summary>注册或替换指定组件类型的诊断绘制器。Domain Reload 会自然清空并由模块重新注册。</summary>
        public static void Register<T>(Action<T> drawer) where T : UnityEngine.Object
        {
            if (drawer == null) throw new ArgumentNullException(nameof(drawer));
            Contributors[typeof(T)] = inspected => drawer((T)inspected);
        }

        /// <summary>注销指定类型的诊断绘制器，供禁用可选 Editor Module 时显式清理。</summary>
        public static void Unregister<T>() where T : UnityEngine.Object => Contributors.Remove(typeof(T));

        internal static bool HasRegistrationFor(Type inspectedType) =>
            inspectedType != null && Contributors.ContainsKey(inspectedType);

        internal static void DrawRegistered(UnityEngine.Object inspected)
        {
            if (inspected == null) return;
            var matches = new List<KeyValuePair<Type, Action<UnityEngine.Object>>>();
            foreach (var contributor in Contributors)
                if (contributor.Key.IsInstanceOfType(inspected))
                    matches.Add(contributor);
            matches.Sort((left, right) => string.Compare(
                left.Key.FullName, right.Key.FullName, StringComparison.Ordinal));

            foreach (var contributor in matches)
            {
                try
                {
                    contributor.Value(inspected);
                }
                catch (Exception ex)
                {
                    EditorGUILayout.HelpBox(
                        $"{contributor.Key.Name} 诊断绘制失败：{ex.Message}", MessageType.Warning);
                }
            }
        }
    }

    /// <summary>
    /// Framework Mono 组件的无所有权诊断 GUI。普通字段由当前 Editor（Unity 默认、Odin 或业务 Editor）绘制；
    /// 本类只负责可叠加的运行时信息。
    /// </summary>
    internal static class FrameworkMonoDiagnosticsGUI
    {
        internal static void DrawRuntimeDiagnostics(UnityEngine.Object inspected)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("框架诊断", EditorStyles.boldLabel);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("进入 Play 后可在这里查看实际解析到的 Context 和服务状态。", MessageType.Info);
            }
            else if (inspected is MonoGameContextBase contextHost)
            {
                DrawContextHost(contextHost);
            }
            else if (inspected is IHasGameContext holder)
            {
                DrawResolvedContext(holder.Context);
                DrawLayerContracts(inspected);
                DrawServiceDetails(inspected);
            }

            FrameworkInspectorDiagnostics.DrawRegistered(inspected);

            if (GUILayout.Button("打开完整框架诊断"))
                FrameworkDiagnosticsWindow.Open();
        }

        private static void DrawContextHost(MonoGameContextBase host)
        {
            MonoContextDiagnosticSnapshot snapshot = host.DiagnosticSnapshot;
            EditorGUILayout.LabelField("初始化状态", snapshot.State.ToString());
            DrawContextValue("解析到的父级", snapshot.ResolvedParent);

            if (snapshot.Failure != null)
                EditorGUILayout.HelpBox($"{snapshot.Failure.GetType().Name}: {snapshot.Failure.Message}", MessageType.Error);

            if (snapshot.Context == null) return;
            var names = new List<string>();
            foreach (Type contract in snapshot.Context.Container.LocalRegistrations)
                names.Add(contract.Name);
            names.Sort(StringComparer.Ordinal);
            DrawLines("本地注册", names, "（无）");
        }

        private static void DrawResolvedContext(IGameContext context)
        {
            DrawContextValue("解析到的 Context", context);
            if (context == null)
                EditorGUILayout.HelpBox("尚未解析到 Context；请检查 Target Context 或 Transform 父级。", MessageType.Warning);
        }

        private static void DrawContextValue(string label, IGameContext context)
        {
            if (context is UnityEngine.Object unityObject)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(label, unityObject, typeof(UnityEngine.Object), true);
                return;
            }
            EditorGUILayout.LabelField(label, context == null ? "（无）" : context.GetType().Name);
        }

        private static void DrawLayerContracts(UnityEngine.Object inspected)
        {
            Type marker = inspected switch
            {
                MonoModelBase => typeof(IModel),
                MonoSystemBase => typeof(ISystem),
                MonoUtilityBase => typeof(IUtility),
                _ => null,
            };
            if (marker == null) return;

            Type concrete = inspected.GetType();
            var names = new List<string> { concrete.Name };
            foreach (Type candidate in concrete.GetInterfaces())
            {
                if (candidate != marker && marker.IsAssignableFrom(candidate))
                    names.Add(candidate.Name);
            }
            names.Sort(StringComparer.Ordinal);
            DrawLines("注册契约", names, "（仅具体类型）");
        }

        private static void DrawServiceDetails(UnityEngine.Object inspected)
        {
            switch (inspected)
            {
                case AssetUtility assets:
                    DrawLines("资源状态", assets.EditorDiagnostics, "（尚无资源包状态）");
                    break;
                case MonoAudioUtility audio:
                    DrawLines("音频状态", audio.EditorDiagnostics, "（尚无活动音频）");
                    break;
                case MonoPoolUtility pool:
                    DrawLines("对象池", pool.Impl.GetPoolDiagnostics(), "（尚无对象池）");
                    break;
                case MonoStorageUtility storage:
                    EditorGUILayout.LabelField("存储根目录", storage.EditorStorageRoot, EditorStyles.wordWrappedMiniLabel);
                    break;
            }
        }

        private static void DrawLines(string title, IEnumerable<string> lines, string emptyText)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            bool any = false;
            if (lines != null)
            {
                foreach (string line in lines)
                {
                    any = true;
                    EditorGUILayout.LabelField($"• {line}", EditorStyles.wordWrappedMiniLabel);
                }
            }
            if (!any) EditorGUILayout.LabelField(emptyText, EditorStyles.wordWrappedMiniLabel);
        }
    }

    /// <summary>标记已经自行绘制 Framework 诊断的 Editor，避免 Header hook 重复追加。</summary>
    public interface IFrameworkMonoDiagnosticsOwner { }

    /// <summary>无 Odin 时使用的原生保底 Inspector；可选 Odin Adapter 以非 fallback Editor 覆盖它。</summary>
    public abstract class FrameworkContextAwareInspector : UnityEditor.Editor, IFrameworkMonoDiagnosticsOwner
    {
        public override void OnInspectorGUI()
        {
            FrameworkMonoDiagnosticsGUI.DrawRuntimeDiagnostics(target);
            DrawDefaultInspector();
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }

    /// <summary>
    /// 场景接线字段在 Awake 后只保留快照语义。字段级 Drawer 不依赖当前 Editor 所有权，因此原生 Inspector、
    /// Odin Inspector 与业务自定义 Inspector 都能复用同一条 PlayMode 禁改规则。
    /// </summary>
    [CustomPropertyDrawer(typeof(LockInPlayModeAttribute))]
    internal sealed class LockInPlayModeDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUI.PropertyField(position, property, label, true);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => EditorGUI.GetPropertyHeight(property, label, true);
    }

    /// <summary>
    /// 在当前 Editor 所有权之外叠加框架诊断：Unity 默认与遵循默认 Header 流程的业务 Inspector 走此入口；
    /// Odin 生成的具体 Inspector 不保证触发该回调，故由可选 Odin Adapter 直接绘制。
    /// </summary>
    [InitializeOnLoad]
    internal static class FrameworkMonoInspectorHeader
    {
        static FrameworkMonoInspectorHeader()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += Draw;
        }

        private static void Draw(UnityEditor.Editor editor)
        {
            if (editor == null || editor is IFrameworkMonoDiagnosticsOwner || editor.targets.Length != 1) return;
            UnityEngine.Object inspected = editor.target;
            if (inspected is not MonoGameContextBase && inspected is not IHasGameContext) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                FrameworkMonoDiagnosticsGUI.DrawRuntimeDiagnostics(inspected);
        }
    }

    [CustomEditor(typeof(MonoGameContextBase), true, isFallback = true)]
    public sealed class MonoGameContextInspector : FrameworkContextAwareInspector { }

    [CustomEditor(typeof(MonoModelBase), true, isFallback = true)]
    public sealed class MonoModelInspector : FrameworkContextAwareInspector { }

    [CustomEditor(typeof(MonoSystemBase), true, isFallback = true)]
    public sealed class MonoSystemInspector : FrameworkContextAwareInspector { }

    [CustomEditor(typeof(MonoUtilityBase), true, isFallback = true)]
    public sealed class MonoUtilityInspector : FrameworkContextAwareInspector { }

    [CustomEditor(typeof(MonoViewBase), true, isFallback = true)]
    public sealed class MonoViewInspector : FrameworkContextAwareInspector { }

    [CustomEditor(typeof(FrameworkSelfCheck))]
    public sealed class FrameworkSelfCheckInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var check = (FrameworkSelfCheck)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("自检结果", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("框架内核程序集", check.KernelAssembly);
            EditorGUILayout.LabelField("运行环境", check.Environment);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("进入 Play 后自动执行；也可点击下方按钮重新运行。", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(check.AllOk ? "全部通过" : "尚未全部通过", check.AllOk ? MessageType.Info : MessageType.Warning);
                foreach (string result in check.Results)
                    EditorGUILayout.LabelField(result, EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
                if (GUILayout.Button("重新自检")) check.Run();
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
#endif
