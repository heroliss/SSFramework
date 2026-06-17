using UnityEditor;
using UnityEditor.SceneManagement;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// Prefab 编辑模式下让 <see cref="UIBindingData"/> 与活节点树<b>实时一致</b>：改名/移动已绑节点 → 同步其相对路径；删除已绑节点 → 清理该条绑定。
    /// </summary>
    /// <remarks>
    /// 绑定的身份真源是每条 <see cref="UIBindingEntry.Node"/>（节点直接引用），而非路径字符串。本类只在编辑变更后调
    /// <see cref="UIBindingUtil.ReconcilePaths"/> 把清单按引用对齐：引用还在 → 重算路径（改名/移动）；引用被 Unity 置空且路径也解析不到 → 清理（删除）。
    /// 引用按 prefab 内 fileID 序列化、<b>跨域重载由 Unity 正确重链</b>，不像 instanceId 会过期——因此没有「锚过期把所有条目误判成已删」的隐患。
    /// 改名（引用仍在）与删除（引用为空）天然区分，绝不互相误伤。
    /// 改写/清理只 <c>SetDirty</c>、<b>不</b>进 Undo 栈：改名撤销会再次触发对齐把路径跟回；删除清理若进 Undo 会与「撤销删节点」打架，故不进——
    /// 代价是删节点后该绑定的组件/字段名元数据丢失，撤销删除把节点找回后需重绑一次（一次点击）。
    /// </remarks>
    [InitializeOnLoad]
    internal static class UIBindingRenameSync
    {
        static UIBindingRenameSync()
        {
            ObjectChangeEvents.changesPublished += OnChanges;
            PrefabStage.prefabStageOpened += Sync; // 开 prefab 即迁移历史条目（补引用）+ 对齐
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) Sync(stage);        // 域重载后已有 prefab 打开：尽早对齐一次
        }

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) Sync(stage);
        }

        private static void Sync(PrefabStage stage)
        {
            var root = stage.prefabContentsRoot.transform;
            var data = root.GetComponent<UIBindingData>();
            if (data == null || data.Entries.Count == 0) return;

            if (UIBindingUtil.ReconcilePaths(data, root))
            {
                EditorUtility.SetDirty(data);
                EditorSceneManager.MarkSceneDirty(stage.scene);
                EditorApplication.RepaintHierarchyWindow();
            }
        }
    }
}
