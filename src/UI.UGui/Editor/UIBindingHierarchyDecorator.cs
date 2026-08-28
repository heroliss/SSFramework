using System;
using System.Collections.Generic;
using System.Reflection;
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
    ///   <item>Play 模式下的运行实例：从活 <see cref="UIBindingData"/> 读绑定显示<b>只读</b>徽标；设了绑定却没在代码里真正绑成的节点（漂移）画<b>红色</b>警示，点击定位到该活节点。</item>
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

        // Hierarchy 每帧重绘必须只读：缺配置时使用 Profile 定义的展示默认值，绝不能因为看了一眼层级就创建项目资产。
        private static UICodeGenProfile Profile
        {
            get
            {
                UICodeGenProfile.TryResolve(out var profile);
                return profile;
            }
        }

        private static void OnItem(int instanceID, Rect rect)
        {
            // Hierarchy 回调在 Unity 6000.3 仍传 int；这里利用公开的 int → EntityId 隐式转换，
            // 进入对象域后统一使用强类型 EntityId，避免继续依赖已废弃的 Instance ID API。
            if (EditorUtility.EntityIdToObject(instanceID) is not GameObject go) return;

            // 定位该节点归属的 prefab 资产 + 根 + 是否可编辑 / 是否运行实例。
            string assetPath;
            Transform root;
            bool editable;
            bool runtime = false;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && go.transform.IsChildOf(stage.prefabContentsRoot.transform))
            {
                assetPath = stage.assetPath;
                root = stage.prefabContentsRoot.transform;
                editable = true;
            }
            else if (!Application.isPlaying && PrefabUtility.IsPartOfPrefabInstance(go))
            {
                assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                root = instanceRoot != null ? instanceRoot.transform : null;
                editable = false;
            }
            else if (Application.isPlaying)
            {
                // 运行实例已无 prefab 连接，但根上仍带着 UIBindingData（除非构建期剥离）。
                // 从自身向上找最近的带绑定数据的祖先当窗口根，读活组件（只读 + 漂移检查）。
                root = FindRuntimeRoot(go.transform);
                if (root == null) return;
                assetPath = null;
                editable = false;
                runtime = true;
            }
            else return;

            if (root == null) return;

            // 数据来源：编辑态 / 运行实例读根上的活组件；编辑期 prefab 实例读 prefab 资产根上的组件（只读）。
            var data = (editable || runtime) ? UIBindingUtil.GetData(root.gameObject) : UIBindingUtil.LoadAssetData(assetPath);

            // 根行：信息徽标 → 弹窗总览（生成/设置藏弹窗里，不在根行放即点即生成的按钮）。
            if (go.transform == root)
            {
                if (data != null && data.Entries.Count > 0)
                    DrawRootBadge(rect, assetPath, root.gameObject, data, editable, runtime);
                return;
            }

            if (!UIBindingUtil.TryGetNodePath(root, go.transform, out string path)) return;

            var entry = data != null ? data.Find(path) : null;
            if (entry != null)
            {
                // 本节点已绑：画自己的徽标；若它<b>同时</b>有已绑子孙，再在徽标左侧并排画聚合标记（两者同显，不互相覆盖）。
                bool drift = runtime && IsRuntimeDrift(root, entry);
                float badgeLeft = DrawBadge(rect, assetPath, data, root, path, entry, editable, runtime, drift);
                int ownDescendants = CountBoundDescendants(data, path);
                if (ownDescendants > 0) DrawAggregate(rect, badgeLeft, data, root, path, ownDescendants, runtime);
                return;
            }

            // 未绑定节点：先画「子孙有绑定」的聚合标记（折叠也不漏看），再视情况画「＋」（放在聚合标记左侧不重叠）。
            float rightEdge = rect.xMax;
            if (data != null)
            {
                int descendants = CountBoundDescendants(data, path);
                if (descendants > 0) rightEdge = DrawAggregate(rect, rect.xMax, data, root, path, descendants, runtime);
            }

            if (!editable) return;
            if (!IsSelected(go.GetEntityId())) return;
            if (UIBindingUtil.IsInsideSubView(root, go.transform, out _)) return;
            // 纯布局/空节点（无脚本无内置控件）也允许绑：默认取其 RectTransform/Transform（PickDefaultComponent 的兜底），
            // 用于布局引用 / 显隐容器等；「＋」只在选中节点时出现，噪音很小。GameObject 不自动选，需在面板里手动勾。
            var def = UIBindingUtil.PickDefaultComponent(go, UICodeGenProfile.BuiltinComponentPriorityOrDefault(Profile));
            DrawAddButton(rect, rightEdge, root.gameObject, go, path, def, stage);
        }

        // 画已绑节点的徽标，返回徽标左边界（供并排的聚合标记避让）。
        // runtime=true 为 Play 模式只读徽标：漂移（设了绑定却没在代码里真正绑成）画红色警示、点击定位活节点。
        private static float DrawBadge(Rect rect, string assetPath, UIBindingData data, Transform root, string path, UIBindingEntry entry, bool editable, bool runtime, bool drift)
        {
            EnsureStyles();
            // 字段名与「绑定清单 / 生成代码」同一口径（EffectiveFieldNames：含命名模板/别名/多组件后缀），否则徽标显「Add」、生成却是「AddButton」错位。
            bool noComp = entry.ComponentTypes == null || entry.ComponentTypes.Count == 0;
            var names = UIBindingUtil.EffectiveFieldNames(entry, root.name, Profile);
            // 字段重名：本条目任一字段名与窗口内其它节点撞名（生成时会跳过其一）。仅编辑/实例只读视图提示，运行时交给漂移检查。
            bool dup = !noComp && !runtime && CollidesWithDuplicate(data, root.name, names);

            string text, warnTip = null;
            if (noComp)
            {
                // 未勾任何组件：不会生成字段——与正常绑定显式区分（标「未选组件」，保留已填的自定义字段名供参考）。
                string fn = !string.IsNullOrEmpty(entry.FieldName) ? UIBindingUtil.SanitizeIdentifier(entry.FieldName) : null;
                text = fn != null ? $"◈ {fn}（未选组件）" : "◈（未选组件）";
                warnTip = "未勾选任何组件——不会生成字段。勾选一个组件，或解绑该节点。";
            }
            else
            {
                string fieldName = names.Count > 0 ? names[0] : UIBindingUtil.DeriveBaseName(path, root.name);
                string mark = dup ? "◈⚠ " : "◈ ";
                text = names.Count > 1 ? $"{mark}{fieldName}(+{names.Count - 1})" : $"{mark}{fieldName}";
                if (dup) warnTip = $"字段名「{fieldName}」与其它节点重复——生成时会跳过其一。给其中一个改名。";
            }

            string tooltip = runtime && drift
                ? BuildDriftTooltip(entry)
                : (warnTip != null ? "⚠ " + warnTip + "\n" + BuildTooltip(entry, editable) : BuildTooltip(entry, editable));
            var content = new GUIContent(text, tooltip);
            float w = _badgeStyle.CalcSize(content).x + 6;
            var badgeRect = new Rect(rect.xMax - w, rect.y, w, rect.height);

            var prevColor = GUI.color;
            if (runtime)
                GUI.color = drift ? new Color(0.95f, 0.4f, 0.4f) : new Color(0.55f, 0.85f, 0.55f);
            else if (noComp || dup)
                GUI.color = new Color(0.95f, 0.74f, 0.33f); // 橙：绑定不完整（未选组件）或字段名冲突
            else
                GUI.color = editable ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.55f, 0.6f, 0.7f);

            if (editable)
            {
                if (GUI.Button(badgeRect, content, _badgeStyle))
                {
                    var node = string.IsNullOrEmpty(path) ? root : root.Find(path);
                    PopupWindow.Show(badgeRect, new UIBindingNodePopup(root.gameObject, path,
                        node != null ? node.gameObject : null));
                }
            }
            else if (runtime)
            {
                // 运行时点击：弹出与编辑模式同一面板，但只读、隐藏改写按钮（既然能点到徽标，就不必再选中节点）。
                // 注：Play 模式下 Hierarchy 的 IMGUI tooltip 不弹（Unity 持续重绘时吞掉 hover tooltip，停运才恢复）——属编辑器限制，点开面板看详情即可。
                if (GUI.Button(badgeRect, content, _badgeStyle))
                {
                    var node = string.IsNullOrEmpty(path) ? root : root.Find(path);
                    PopupWindow.Show(badgeRect, new UIBindingNodePopup(root.gameObject, path,
                        node != null ? node.gameObject : null, editable: false));
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

        // 根行信息徽标：显示绑定数，点开总览弹窗（含生成 / 目标设置）。窗口内任意节点有警告（未选组件 / 字段名重复）时整枚标黄。
        private static void DrawRootBadge(Rect rect, string assetPath, GameObject rootGo, UIBindingData data, bool editable, bool runtime)
        {
            EnsureStyles();
            bool warn = !runtime && HasWarning(data, rootGo.name, null);
            string tip = (editable ? "窗口绑定总览 · 生成代码 / 目标设置（点击）" : "窗口绑定总览（点击；实例只读）")
                       + (warn ? "\n⚠ 有未选组件 / 字段名重复的节点，点开总览查看" : "");
            var content = new GUIContent($"◈ {data.Entries.Count}", tip);
            float w = _badgeStyle.CalcSize(content).x + 8;
            var badgeRect = new Rect(rect.xMax - w, rect.y, w, rect.height);

            var prevColor = GUI.color;
            GUI.color = warn ? new Color(0.95f, 0.74f, 0.33f) : new Color(0.5f, 0.78f, 0.96f); // 警告橙 / 信息蓝（区别于节点绑定的绿）
            if (GUI.Button(badgeRect, content, _badgeStyle))
                PopupWindow.Show(badgeRect, new UIBindingRootPopup(assetPath, rootGo, editable));
            GUI.color = prevColor;
        }

        // 「子孙有绑定」的淡色聚合标记：从 rightEdge 向左画，返回腾出后的新左边界供「＋」避让。
        // 可点击——弹出子孙绑定只读清单（每条可定位），折叠父节点也能展开看里面绑了什么。
        // 子孙中有警告节点（未选组件 / 字段名重复）时本标记标黄——折叠父节点也能一眼看出里面有需要处理的绑定。
        private static float DrawAggregate(Rect rect, float rightEdge, UIBindingData data, Transform root, string path, int count, bool runtime)
        {
            EnsureStyles();
            bool warn = !runtime && HasWarning(data, root.name, path);
            string tip = BuildDescendantTooltip(data, path)
                       + (warn ? "\n⚠ 子孙中有未选组件 / 字段名重复的节点" : "")
                       + "\n（点击展开子孙绑定清单）";
            var content = new GUIContent($"◈{count}", tip);
            float w = _aggStyle.CalcSize(content).x + 6;
            var r = new Rect(rightEdge - w, rect.y, w, rect.height);

            var prevColor = GUI.color;
            GUI.color = warn ? new Color(0.95f, 0.74f, 0.33f, 0.9f) : new Color(0.55f, 0.72f, 0.55f, 0.55f); // 警告橙 / 普通淡绿（比直接绑定淡）
            if (GUI.Button(r, content, _aggStyle))
                PopupWindow.Show(r, new UIBindingDescendantsPopup(data, path, root));
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
                if (entry == null) { entry = new UIBindingEntry { Path = path, Node = node.transform }; data.Entries.Add(entry); }
                string id = UIBindingUtil.TypeId(def.GetType());
                if (!entry.ComponentTypes.Contains(id)) entry.ComponentTypes.Add(id);
                EditorUtility.SetDirty(data);
                EditorSceneManager.MarkSceneDirty(stage.scene);
                Undo.CollapseUndoOperations(group);

                PopupWindow.Show(addRect, new UIBindingNodePopup(rootGo, path, node));
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
                    var effective = UIBindingUtil.EffectiveFieldNames(e, data.name, Profile);
                    string fieldName = effective.Count > 0 ? effective[0]
                        : (!string.IsNullOrEmpty(e.FieldName) ? e.FieldName : UIBindingUtil.DeriveBaseName(e.Path, data.name));
                    // 字段名就是节点名（默认派生）时不重复显示「→ 名」，避免「ScoreText → ScoreText」这种废话。
                    lines.Add(fieldName == UIBindingUtil.SanitizeIdentifier(tail) ? $"  · {sub}" : $"  · {sub} → {fieldName}");
                }
            return string.Join("\n", lines);
        }

        internal static bool IsSelected(EntityId entityId)
        {
            foreach (EntityId selectedEntityId in Selection.entityIds)
                if (selectedEntityId == entityId) return true;
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

        // 字段重名提示用：窗口内会撞名的字段名集合，按「绑定数据签名」缓存——Hierarchy 逐行每帧回调，撞名集对所有行相同，
        // 只在绑定内容（路径/字段名/组件）变化时重算（签名只做整型哈希、无分配；重算才真正建字符串集）。
        private static GameObject _dupRoot;
        private static int _dupSig;
        private static HashSet<string> _dupSet;

        private static bool CollidesWithDuplicate(UIBindingData data, string rootName, List<string> names)
        {
            if (data == null) return false;
            var dups = DuplicateSet(data, rootName);
            if (dups.Count == 0) return false;
            foreach (var n in names) if (dups.Contains(n)) return true;
            return false;
        }

        // 某节点子孙（descendantOf/...）中、或整窗（descendantOf == null）是否存在「未选组件 / 字段名重复」的警告条目。
        // 给父/根的数字徽标做「子孙有问题→标黄」用：折叠也能一眼看出里面有需要处理的绑定。无警告时只做 Count 判断、开销很小。
        private static bool HasWarning(UIBindingData data, string rootName, string descendantOf)
        {
            if (data == null) return false;
            string prefix = descendantOf != null ? descendantOf + "/" : null;
            var dups = DuplicateSet(data, rootName);
            foreach (var e in data.Entries)
            {
                if (prefix != null && (string.IsNullOrEmpty(e.Path) || !e.Path.StartsWith(prefix, StringComparison.Ordinal))) continue;
                if (e.ComponentTypes == null || e.ComponentTypes.Count == 0) return true; // 未选组件
                if (dups.Count > 0)
                    foreach (var n in UIBindingUtil.EffectiveFieldNames(e, rootName, Profile))
                        if (dups.Contains(n)) return true; // 字段名重复
            }
            return false;
        }

        private static HashSet<string> DuplicateSet(UIBindingData data, string rootName)
        {
            int sig = BindingSignature(data);
            if (_dupRoot == data.gameObject && _dupSig == sig && _dupSet != null) return _dupSet;
            _dupRoot = data.gameObject;
            _dupSig = sig;
            _dupSet = UIBindingUtil.DuplicateFieldNames(data.Entries, rootName, Profile);
            return _dupSet;
        }

        // 绑定内容的轻量签名（路径/字段名/组件 id 的哈希组合）：变了就让撞名集失效重算，避免逐帧重建字符串集。
        private static int BindingSignature(UIBindingData data)
        {
            int sig = 17;
            foreach (var e in data.Entries)
            {
                sig = sig * 31 + (e.Path != null ? e.Path.GetHashCode() : 0);
                sig = sig * 31 + (e.FieldName != null ? e.FieldName.GetHashCode() : 0);
                var ct = e.ComponentTypes;
                sig = sig * 31 + (ct != null ? ct.Count : 0);
                if (ct != null)
                    foreach (var id in ct) sig = sig * 31 + (id != null ? id.GetHashCode() : 0);
            }
            return sig;
        }

        // ───────────── 运行时（Play 模式）支持 ─────────────

        // 从节点向上找最近的带 UIBindingData 的祖先当窗口根（运行实例已无 prefab 连接，靠活组件定位）。
        private static Transform FindRuntimeRoot(Transform t)
        {
            for (var c = t; c != null; c = c.parent)
                if (c.GetComponent<UIBindingData>() != null) return c;
            return null;
        }

        // 运行时漂移：该条绑定的任一目标组件是否都没被窗口字段引用到（设了绑定却没在代码里真正绑成）。
        private static bool IsRuntimeDrift(Transform root, UIBindingEntry entry)
        {
            var node = string.IsNullOrEmpty(entry.Path) ? root : root.Find(entry.Path);
            if (node == null) return true; // 节点都不在，必然没绑成
            var bound = GetRuntimeBoundObjects(root.gameObject);
            foreach (var id in entry.ComponentTypes)
            {
                var type = UIBindingUtil.ResolveType(id);
                if (type == null) continue;
                // GameObject 绑的是节点本身（字段类型 GameObject）；其余按组件引用判定。
                UnityEngine.Object target = type == typeof(GameObject) ? node.gameObject : node.GetComponent(type);
                if (target != null && bound.Contains(target)) return false; // 至少一个目标被字段引用 → 已绑成
            }
            return true;
        }

        // 收集窗口实例所有字段实际引用到的对象（组件 + GameObject）——按<b>引用</b>判定「绑成」，与生成字段名无关，最稳。
        // 按根缓存：Hierarchy 逐行回调，同一根的多行复用一次反射结果，换根才重算。
        private static GameObject _boundCacheRoot;
        private static HashSet<UnityEngine.Object> _boundCache;
        private static HashSet<UnityEngine.Object> GetRuntimeBoundObjects(GameObject rootGo)
        {
            if (_boundCacheRoot == rootGo && _boundCache != null) return _boundCache;
            var set = new HashSet<UnityEngine.Object>();
            foreach (var comp in rootGo.GetComponents<Component>())
            {
                if (comp == null || !UIBindingUtil.IsWindowScript(comp.GetType())) continue;
                // 沿类型链逐级取字段：继承的 protected 字段（如变体基类的节点字段）GetFields 默认不返回，须 DeclaredOnly 逐级收。
                for (var t = comp.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                        if ((typeof(Component).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(GameObject))
                            && f.GetValue(comp) is UnityEngine.Object v && v != null)
                            set.Add(v);
            }
            _boundCacheRoot = rootGo;
            _boundCache = set;
            return set;
        }

        private static string BuildDriftTooltip(UIBindingEntry entry)
        {
            var comps = new List<string>();
            foreach (var id in entry.ComponentTypes)
            {
                var type = UIBindingUtil.ResolveType(id);
                comps.Add(type != null ? type.Name : id);
            }
            return $"⚠ 绑定漂移：设了绑定但代码没真正绑成（{string.Join(", ", comps)}）。\n" +
                   $"路径：{(string.IsNullOrEmpty(entry.Path) ? "(根)" : entry.Path)}\n" +
                   "多半是改了绑定后没重新生成代码——进 prefab 编辑模式，点根行徽标弹窗里的「生成代码」重新生成。";
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
