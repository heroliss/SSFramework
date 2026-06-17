using System.Text;
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
    ///   <item><c>SSFramework/UI 绑定/配置</c>：定位生成配置资产。</item>
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
                EditorUtility.DisplayDialog("UI 绑定", "请在 Prefab 编辑模式下选中节点再标记（双击 prefab 进入编辑）。", "好");
                return;
            }

            var root = stage.prefabContentsRoot;
            var rootTf = root.transform;
            var profile = UICodeGenProfile.Resolve();

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
                var comp = UIBindingUtil.PickDefaultComponent(go, profile.BuiltinComponentPriority);
                string id = UIBindingUtil.TypeId(comp.GetType());
                if (!entry.ComponentTypes.Contains(id)) entry.ComponentTypes.Add(id);
                marked++;
            }

            if (skipped.Length > 0)
                Debug.LogWarning("[UI 绑定] 以下节点跨了子 View 边界，已跳过（父窗口应引用子 View 本身，由子 View 管自己子树）：\n" + skipped);

            if (marked == 0)
            {
                Undo.RevertAllDownToGroup(group); // 把（可能刚加的空）组件撤回，保持干净
                EditorUtility.DisplayDialog("UI 绑定",
                    skipped.Length > 0
                        ? "选中的节点都在子 View 内部，已跳过（见 Console）。父窗口应直接标记那个子 View 节点。"
                        : "没有可标记的节点——请选中当前 prefab 里的子节点（不要选根）。",
                    "好");
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
                EditorUtility.DisplayDialog("UI 绑定", "请在 Prefab 编辑模式下选中节点再取消标记。", "好");
                return;
            }

            var root = stage.prefabContentsRoot;
            var data = UIBindingUtil.GetData(root);
            if (data == null)
            {
                EditorUtility.DisplayDialog("UI 绑定", "本 prefab 还没有任何绑定。", "好");
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
                EditorUtility.DisplayDialog("UI 绑定", "选中的节点没有绑定标记。", "好");
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
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("UI 绑定", "Play 模式下不能生成代码（会触发重编译打断运行），请先停止运行。", "好");
                return;
            }

            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var (ok, message) = UIBindingCodeGenerator.GenerateFromAsset(path, UICodeGenProfile.Resolve());
            Debug.Log("[UI 绑定] " + message);
            EditorUtility.DisplayDialog(ok ? "UI 绑定生成完成" : "UI 绑定生成失败", message, "好");
        }

        [MenuItem("Assets/SSFramework/生成 UI 绑定代码", true)]
        private static bool GenerateForSelectedPrefab_Validate()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".prefab");
        }

        // ───────────── 顶部菜单 ─────────────

        [MenuItem("SSFramework/UI 绑定/配置 (UI CodeGen Profile)", priority = 20)]
        private static void PingProfile()
        {
            var profile = UICodeGenProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }
    }
}
