using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.SceneManagement;
using UnityEngine.UIElements;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// Scene 视图里的「绑定改后自动生成代码」勾选框——Prefab 编辑模式下保存（Ctrl+S）时，
    /// 若根上有 <see cref="UIBindingData"/> 就自动重新生成节点代码。状态存 <c>EditorPrefs</c>（全工程持久）。
    /// </summary>
    /// <remarks>
    /// Unity 未公开 Prefab 模式那条 Auto Save 工具条的注入点，故以官方支持的 SceneView <see cref="Overlay"/> 承载（可拖动 / 停靠的浮动小面板），
    /// 语义与「Auto Save 旁边的勾选」一致、位置是浮层而非紧贴 Auto Save。
    /// </remarks>
    [Overlay(typeof(SceneView), OverlayId, "UI 绑定", defaultDisplay = true)]
    public sealed class UIBindingAutoGenOverlay : Overlay
    {
        public const string OverlayId = "ssframework-ui-binding-autogen";
        internal const string AutoGeneratePreferenceKey = "SSFramework.UIBinding.AutoGenerate";

        /// <summary>是否「保存 prefab 时自动重新生成绑定代码」。</summary>
        public static bool AutoGenerate
        {
            get => EditorPrefs.GetBool(AutoGeneratePreferenceKey, false);
            set => EditorPrefs.SetBool(AutoGeneratePreferenceKey, value);
        }

        public override VisualElement CreatePanelContent()
        {
            var toggle = new Toggle("保存时自动生成绑定代码")
            {
                name = "ui-binding-autogen-toggle",
                value = AutoGenerate,
                tooltip = "勾上后：保存 prefab（Ctrl+S）时，若根上有 UIBindingData，自动重新生成节点绑定代码（会触发一次重编译）。",
            };
            toggle.style.minWidth = 0f;
            toggle.style.flexShrink = 1f;
            toggle.labelElement.style.minWidth = 0f;
            toggle.labelElement.style.flexShrink = 1f;
            toggle.labelElement.style.whiteSpace = WhiteSpace.Normal;
            toggle.RegisterValueChangedCallback(evt => AutoGenerate = evt.newValue);

            var root = new VisualElement
            {
                name = "ui-binding-autogen-root",
                style =
                {
                    minWidth = 0f,
                    flexShrink = 1f,
                },
            };
            root.Add(toggle);
            return root;
        }
    }

    /// <summary>把「保存即自动生成」接到 <see cref="PrefabStage.prefabSaved"/> 上（与 Overlay 的开关联动）。</summary>
    [InitializeOnLoad]
    internal static class UIBindingAutoGenHook
    {
        static UIBindingAutoGenHook()
        {
            PrefabStage.prefabSaved += OnPrefabSaved;
        }

        private static void OnPrefabSaved(UnityEngine.GameObject root)
        {
            if (!UIBindingAutoGenOverlay.AutoGenerate || root == null) return;
            var data = root.GetComponent<UIBindingData>();
            if (data == null || data.Entries.Count == 0) return;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string assetPath = stage != null ? stage.assetPath : null;
            if (string.IsNullOrEmpty(assetPath)) return;

            // 延迟到保存流程结束再生成——生成会写 .cs + Refresh（触发重编译），避开与保存重入。
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || root == null) return;
                var d = root.GetComponent<UIBindingData>();
                if (d != null) UIBindingCodeGenerator.GenerateAndLog(assetPath, d, UICodeGenProfile.Resolve());
            };
        }
    }
}
