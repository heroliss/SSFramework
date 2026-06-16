using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 在 Hierarchy 里把 UI 节点绑定直接做成可视 + 可编辑——<b>不必走右键菜单</b>：
    /// <list type="bullet">
    ///   <item>已绑定节点：行尾绿色 <c>◈ 字段名</c> 徽标，点击弹面板改字段名 / 加删组件 / 解绑。</item>
    ///   <item>未绑定但<b>子孙有绑定</b>的节点：行尾淡色 <c>◈N</c> 聚合标记（tooltip 列出），折叠时也不漏看里面有绑定。</item>
    ///   <item>Prefab 编辑模式下<b>选中</b>一个未绑定且有可绑组件的节点：行尾出现 <c>＋</c>，点一下按默认组件绑上并弹面板细调。</item>
    ///   <item>Prefab 根行：蓝色 <c>◈ N</c> 信息徽标，点开「窗口绑定总览」弹窗（生成代码 / 目标设置藏在里面，避免根行误触）。</item>
    ///   <item>场景里的 prefab 实例节点：映射回 prefab 资产显示<b>只读</b>徽标（点击定位到 prefab）。</item>
    /// </list>
    /// 绑定数据是 prefab 根上的 <see cref="UIBindingData"/> 组件——增删改经 <c>Undo</c>，吃 Unity 预制体编辑的原生「脏标记 / 撤销 / Ctrl+S 保存」。
    /// 实例只读是刻意的：绑定驱动「一个 prefab 一个生成类」，是 prefab 资产级属性，不该按实例 override（会与生成代码脱节；真正的变化轴是 prefab 变体）。
    /// </summary>
    [InitializeOnLoad]
    public static class UIBindingHierarchyDecorator
    {
        private static GUIStyle _badgeStyle;
        private static GUIStyle _addStyle;
        private static GUIStyle _aggStyle;

        static UIBindingHierarchyDecorator()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnItem;
        }

        private static UICodeGenProfile _profile;
        private static UICodeGenProfile Profile => _profile != null ? _profile : (_profile = UICodeGenProfile.Resolve());

        private static void OnItem(int instanceID, Rect rect)
        {
            if (EditorUtility.InstanceIDToObject(instanceID) is not GameObject go) return;

            // 定位该节点归属的 prefab 资产 + 根 + 是否可编辑。
            string assetPath;
            Transform root;
            bool editable;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && go.transform.IsChildOf(stage.prefabContentsRoot.transform))
            {
                assetPath = stage.assetPath;
                root = stage.prefabContentsRoot.transform;
                editable = true;
            }
            else if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                root = instanceRoot != null ? instanceRoot.transform : null;
                editable = false;
            }
            else return;

            if (root == null) return;

            // 数据来源：编辑态读 stage 根上的活组件（含未保存改动）；实例读 prefab 资产根上的组件（只读）。
            var data = editable ? UIBindingUtil.GetData(root.gameObject) : UIBindingUtil.LoadAssetData(assetPath);

            // 根行：信息徽标 → 弹窗总览（生成/设置藏弹窗里，不在根行放即点即生成的按钮）。
            if (go.transform == root)
            {
                if (data != null && data.Entries.Count > 0)
                    DrawRootBadge(rect, assetPath, root.gameObject, data, editable);
                return;
            }

            if (!UIBindingUtil.TryGetNodePath(root, go.transform, out string path)) return;

            var entry = data != null ? data.Find(path) : null;
            if (entry != null)
            {
                // 本节点已绑：画自己的徽标；若它<b>同时</b>有已绑子孙，再在徽标左侧并排画聚合标记（两者同显，不互相覆盖）。
                float badgeLeft = DrawBadge(rect, assetPath, root, path, entry, editable);
                int ownDescendants = CountBoundDescendants(data, path);
                if (ownDescendants > 0) DrawAggregate(rect, badgeLeft, data, path, ownDescendants);
                return;
            }

            // 未绑定节点：先画「子孙有绑定」的聚合标记（折叠也不漏看），再视情况画「＋」（放在聚合标记左侧不重叠）。
            float rightEdge = rect.xMax;
            if (data != null)
            {
                int descendants = CountBoundDescendants(data, path);
                if (descendants > 0) rightEdge = DrawAggregate(rect, rect.xMax, data, path, descendants);
            }

            if (!editable) return;
            if (!IsSelected(instanceID)) return;
            if (UIBindingUtil.IsInsideSubView(root, go.transform, out _)) return;
            var def = UIBindingUtil.PickDefaultComponent(go, Profile.BuiltinComponentPriority);
            if (def is Transform) return; // 没有有意义的可绑组件（纯布局节点）
            DrawAddButton(rect, rightEdge, root.gameObject, go, path, def, stage);
        }

        // 画已绑节点的徽标，返回徽标左边界（供并排的聚合标记避让）。
        private static float DrawBadge(Rect rect, string assetPath, Transform root, string path, UIBindingEntry entry, bool editable)
        {
            string fieldName = !string.IsNullOrEmpty(entry.FieldName)
                ? entry.FieldName
                : UIBindingUtil.DeriveBaseName(path, root.name);
            string text = entry.ComponentTypes.Count > 1 ? $"◈ {fieldName}(+{entry.ComponentTypes.Count - 1})" : $"◈ {fieldName}";

            EnsureStyles();
            var content = new GUIContent(text, BuildTooltip(entry, editable));
            float w = _badgeStyle.CalcSize(content).x + 6;
            var badgeRect = new Rect(rect.xMax - w, rect.y, w, rect.height);

            var prevColor = GUI.color;
            GUI.color = editable ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.55f, 0.6f, 0.7f);

            if (editable)
            {
                if (GUI.Button(badgeRect, content, _badgeStyle))
                {
                    var node = string.IsNullOrEmpty(path) ? root : root.Find(path);
                    PopupWindow.Show(badgeRect, new UIBindingNodePopup(assetPath, root.gameObject, path,
                        node != null ? node.gameObject : null));
                }
            }
            else
            {
                GUI.Label(badgeRect, content, _badgeStyle);
                if (Event.current.type == EventType.MouseDown && badgeRect.Contains(Event.current.mousePosition))
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath));
                    Event.current.Use();
                }
            }

            GUI.color = prevColor;
            return rect.xMax - w;
        }

        // 根行蓝色信息徽标：显示绑定数，点开总览弹窗（含生成 / 目标设置）。
        private static void DrawRootBadge(Rect rect, string assetPath, GameObject rootGo, UIBindingData data, bool editable)
        {
            EnsureStyles();
            var content = new GUIContent($"◈ {data.Entries.Count}",
                editable ? "窗口绑定总览 · 生成代码 / 目标设置（点击）" : "窗口绑定总览（点击；实例只读）");
            float w = _badgeStyle.CalcSize(content).x + 8;
            var badgeRect = new Rect(rect.xMax - w, rect.y, w, rect.height);

            var prevColor = GUI.color;
            GUI.color = new Color(0.5f, 0.78f, 0.96f); // 蓝调，区别于节点绑定的绿
            if (GUI.Button(badgeRect, content, _badgeStyle))
                PopupWindow.Show(badgeRect, new UIBindingRootPopup(assetPath, rootGo, editable));
            GUI.color = prevColor;
        }

        // 「子孙有绑定」的淡色聚合标记（非交互，只提示），从 rightEdge 向左画，返回腾出后的新左边界供「＋」避让。
        private static float DrawAggregate(Rect rect, float rightEdge, UIBindingData data, string path, int count)
        {
            EnsureStyles();
            var content = new GUIContent($"◈{count}", BuildDescendantTooltip(data, path));
            float w = _aggStyle.CalcSize(content).x + 6;
            var r = new Rect(rightEdge - w, rect.y, w, rect.height);

            var prevColor = GUI.color;
            GUI.color = new Color(0.55f, 0.72f, 0.55f, 0.55f); // 比直接绑定淡，一眼区分
            GUI.Label(r, content, _aggStyle);
            GUI.color = prevColor;
            return rightEdge - w;
        }

        // 选中的未绑定节点行尾画「＋」：经 Undo 按默认组件绑上、标脏，并弹面板细调。rightEdge 让它避开聚合标记。
        private static void DrawAddButton(Rect rect, float rightEdge, GameObject rootGo, GameObject node, string path, Component def, PrefabStage stage)
        {
            EnsureStyles();
            var content = new GUIContent("＋", $"绑定为 UI 节点（默认 {def.GetType().Name}），点击后可细调");
            var addRect = new Rect(rightEdge - 18, rect.y, 18, rect.height);

            var prevColor = GUI.color;
            GUI.color = new Color(0.62f, 0.72f, 0.55f);
            if (GUI.Button(addRect, content, _addStyle))
            {
                int group = Undo.GetCurrentGroup();
                var data = UIBindingUtil.GetOrAddData(rootGo);
                Undo.RecordObject(data, "绑定 UI 节点");
                var entry = data.Find(path);
                if (entry == null) { entry = new UIBindingEntry { Path = path }; data.Entries.Add(entry); }
                string id = UIBindingUtil.TypeId(def.GetType());
                if (!entry.ComponentTypes.Contains(id)) entry.ComponentTypes.Add(id);
                EditorUtility.SetDirty(data);
                EditorSceneManager.MarkSceneDirty(stage.scene);
                Undo.CollapseUndoOperations(group);

                PopupWindow.Show(addRect, new UIBindingNodePopup(stage.assetPath, rootGo, path, node));
            }
            GUI.color = prevColor;
        }

        // 某节点（路径 path）下有几条绑定属于其子孙（path/...）——给祖先聚合标记用。
        private static int CountBoundDescendants(UIBindingData data, string path)
        {
            string prefix = path + "/";
            int n = 0;
            foreach (var e in data.Entries)
                if (!string.IsNullOrEmpty(e.Path) && e.Path.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string BuildDescendantTooltip(UIBindingData data, string path)
        {
            string prefix = path + "/";
            var lines = new List<string> { "子孙已绑定：" };
            foreach (var e in data.Entries)
                if (!string.IsNullOrEmpty(e.Path) && e.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string sub = e.Path.Substring(prefix.Length);
                    int slash = sub.LastIndexOf('/');
                    string tail = slash >= 0 ? sub.Substring(slash + 1) : sub;
                    string fieldName = !string.IsNullOrEmpty(e.FieldName) ? e.FieldName : UIBindingUtil.DeriveBaseName(e.Path, data.name);
                    // 字段名就是节点名（默认派生）时不重复显示「→ 名」，避免「ScoreText → ScoreText」这种废话。
                    lines.Add(fieldName == UIBindingUtil.SanitizeIdentifier(tail) ? $"  · {sub}" : $"  · {sub} → {fieldName}");
                }
            return string.Join("\n", lines);
        }

        private static bool IsSelected(int instanceID)
        {
            foreach (int id in Selection.instanceIDs)
                if (id == instanceID) return true;
            return false;
        }

        private static string BuildTooltip(UIBindingEntry entry, bool editable)
        {
            var comps = new List<string>();
            foreach (var id in entry.ComponentTypes)
            {
                var type = UIBindingUtil.ResolveType(id);
                comps.Add(type != null ? type.Name : id);
            }
            return $"UI 绑定：{string.Join(", ", comps)}\n" +
                   $"路径：{(string.IsNullOrEmpty(entry.Path) ? "(根)" : entry.Path)}\n" +
                   (editable ? "点击编辑" : "实例只读——进 prefab 编辑");
        }

        private static void EnsureStyles()
        {
            _badgeStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                fontStyle = FontStyle.Bold,
            };
            _addStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
            };
            _aggStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
            };
        }
    }
}
