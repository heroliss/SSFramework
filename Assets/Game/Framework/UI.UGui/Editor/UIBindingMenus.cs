using System.Text;
using Game.Framework.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// UI 节点绑定的菜单入口：
    /// <list type="bullet">
    ///   <item>Prefab 编辑模式下 <c>GameObject</c> 右键「标记 / 取消标记 UI 绑定节点」——增删根上 <see cref="UIBindingData"/> 的绑定条目（经 <c>Undo</c>，与原生预制体编辑同感：脏标记、撤销、Ctrl+S 才落盘）。</item>
    ///   <item>Project 里 prefab 右键「生成 UI 绑定代码」（按磁盘已存状态生成）。</item>
    ///   <item>顶部“UI 绑定”工作台：解释并定位全工程 Profile 与目录级覆盖。</item>
    /// </list>
    /// 多数日常增删改直接在 Hierarchy 行尾徽标 / 「＋」上完成，菜单留作多选批量的快捷方式。
    /// </summary>
    public static class UIBindingMenus
    {
        // ───────────── Prefab 编辑模式：标记 / 取消标记 ─────────────

        [MenuItem("GameObject/SSFramework/标记为 UI 绑定节点", false, 30)]
        private static void MarkSelected()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                FrameworkEditorFeedback.Warn(
                    "标记 UI 绑定节点未执行",
                    "影响：Prefab 没有变化。\n原因：当前不在 Prefab 编辑模式。\n下一步：双击 prefab 进入编辑，选中子节点后重试。");
                return;
            }

            var root = stage.prefabContentsRoot;
            var rootTf = root.transform;
            UICodeGenProfile.TryResolve(out var profile);

            int group = Undo.GetCurrentGroup();
            var data = UIBindingUtil.GetOrAddData(root);   // 没有就 Undo.AddComponent（可撤销）
            Undo.RecordObject(data, "标记 UI 绑定节点");

            int marked = 0;
            var skipped = new StringBuilder();
            foreach (var go in Selection.gameObjects)
            {
                if (go == root) continue;                                                  // 根上是窗口脚本本身，不绑
                if (!UIBindingUtil.TryGetNodePath(rootTf, go.transform, out string path)) continue; // 不在本 prefab 树
                // 树状边界：落在某个子 View 脚本内部的节点应由那个子 View 自己绑定，父窗口不跨脚本抓孙节点。
                if (UIBindingUtil.IsInsideSubView(rootTf, go.transform, out string owner))
                {
                    skipped.AppendLine($"  · {path}（在子 View「{owner}」内部，应由 {owner} 绑定）");
                    continue;
                }

                var entry = data.Find(path);
                if (entry == null) { entry = new UIBindingEntry { Path = path, Node = go.transform }; data.Entries.Add(entry); }
                var comp = UIBindingUtil.PickDefaultComponent(
                    go, UICodeGenProfile.BuiltinComponentPriorityOrDefault(profile));
                string id = UIBindingUtil.TypeId(comp.GetType());
                if (!entry.ComponentTypes.Contains(id)) entry.ComponentTypes.Add(id);
                marked++;
            }

            if (skipped.Length > 0)
                Debug.LogWarning("[UI 绑定] 以下节点跨了子 View 边界，已跳过（父窗口应引用子 View 本身，由子 View 管自己子树）：\n" + skipped);

            if (marked == 0)
            {
                Undo.RevertAllDownToGroup(group); // 把（可能刚加的空）组件撤回，保持干净
                FrameworkEditorFeedback.Warn("标记 UI 绑定节点未执行",
                    skipped.Length > 0
                        ? "影响：Prefab 没有变化。\n原因：选中节点都在子 View 内部，父 View 不跨边界绑定。\n下一步：标记子 View 节点本身，或进入子 View 维护其内部绑定。"
                        : "影响：Prefab 没有变化。\n原因：没有选中当前 prefab 内可绑定的子节点。\n下一步：选择非根节点后重试。");
                return;
            }

            EditorUtility.SetDirty(data);
            EditorSceneManager.MarkSceneDirty(stage.scene); // 出 * ：原生「未保存」提示，Ctrl+S 才随 prefab 落盘
            Undo.CollapseUndoOperations(group);             // 整批合成一步撤销
            EditorApplication.RepaintHierarchyWindow();
            Debug.Log($"[UI 绑定] 已标记 {marked} 个节点（Ctrl+S 保存到 prefab，Ctrl+Z 撤销）。Hierarchy 行尾显示徽标；根行「⟳ 生成代码」或 Project 右键产出代码。");
        }

        [MenuItem("GameObject/SSFramework/取消 UI 绑定标记", false, 31)]
        private static void UnmarkSelected()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                FrameworkEditorFeedback.Warn(
                    "取消 UI 绑定标记未执行",
                    "影响：Prefab 没有变化。\n原因：当前不在 Prefab 编辑模式。\n下一步：双击 prefab 进入编辑，选择已标记节点后重试。");
                return;
            }

            var root = stage.prefabContentsRoot;
            var data = UIBindingUtil.GetData(root);
            if (data == null)
            {
                FrameworkEditorFeedback.Info("取消 UI 绑定标记", "本 prefab 没有绑定数据，无需修改。");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(data, "取消 UI 绑定标记");

            int removed = 0;
            foreach (var go in Selection.gameObjects)
                if (UIBindingUtil.TryGetNodePath(root.transform, go.transform, out string path) && data.RemovePath(path))
                    removed++;

            if (removed == 0)
            {
                FrameworkEditorFeedback.Info("取消 UI 绑定标记", "选中节点没有绑定标记，Prefab 未修改。");
                return;
            }

            EditorUtility.SetDirty(data);
            EditorSceneManager.MarkSceneDirty(stage.scene);
            Undo.CollapseUndoOperations(group);
            EditorApplication.RepaintHierarchyWindow();
            Debug.Log($"[UI 绑定] 已取消 {removed} 个节点的绑定标记（Ctrl+S 保存，Ctrl+Z 撤销）。记得重新生成代码。");
        }

        // ───────────── Project：对 prefab 生成代码（按磁盘状态） ─────────────

        [MenuItem("Assets/SSFramework/生成 UI 绑定代码", false, 30)]
        private static void GenerateForSelectedPrefab()
        {
            if (!FrameworkEditorOperationGate.EnsureCanStart("UI 绑定代码生成")) return;

            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!UICodeGenProfile.TryResolve(out var profile))
            {
                FrameworkEditorFeedback.Warn(
                    "UI 绑定代码生成未启动",
                    "影响：没有创建配置，也没有生成代码。\n原因：工程里还没有 UICodeGenProfile。\n" +
                    $"下一步：打开“{FrameworkMenuPaths.UIBinding}”，明确创建并填写业务程序集的生成目标后重试。",
                    Selection.activeObject);
                return;
            }
            var (ok, message) = UIBindingCodeGenerator.GenerateFromAsset(path, profile);
            FrameworkEditorFeedback.ReportResult("生成 UI 绑定代码", ok, message, Selection.activeObject);
        }

        [MenuItem("Assets/SSFramework/生成 UI 绑定代码", true)]
        private static bool GenerateForSelectedPrefab_Validate()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".prefab");
        }

    }
}
