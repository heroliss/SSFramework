using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 点 Hierarchy 绑定徽标 / 「＋」弹出的就地编辑面板：改一个节点的字段名、勾选要绑的组件、解绑。
    /// 直接改 prefab 根上的 <see cref="UIBindingData"/>（经 <c>Undo</c>、标脏）——不立即写盘，随 Ctrl+S 保存 prefab 才落盘。
    /// Prefab 编辑模式下可编辑；运行实例点徽标也弹出本面板，但 <c>editable=false</c> 只读（字段名/组件读出来看，隐藏改写按钮）。
    /// 只编辑单个节点的绑定，不放「生成代码」——生成是整窗操作，归根行徽标弹窗 / 菜单。
    /// 按路径实时取条目（撤销后引用会失效，故不缓存条目本身）。
    /// </summary>
    public sealed class UIBindingNodePopup : PopupWindowContent
    {
        private readonly GameObject _root;
        private readonly string _path;
        private readonly GameObject _node;
        private readonly bool _editable;
        private Vector2 _dragMouseStart; // 标题条拖拽起点（屏幕坐标）
        private Vector2 _dragWinStart;   // 标题条拖拽起点（窗口左上角）

        public UIBindingNodePopup(GameObject root, string path, GameObject node, bool editable = true)
        {
            _root = root;
            _path = path;
            _node = node;
            _editable = editable;
        }

        private const float CheckboxW = 24f; // 勾选框（含与组件名的间隔）占位宽——渲染与测宽共用，保证测得的宽 ≥ 实际渲染宽

        /// <summary>
        /// 窗口尺寸按内容自适应：宽取「标题路径 / 各组件行(组件名 + 右侧字段名) / 组件标签」里最宽的，夹在 <c>[300, 640]</c>；
        /// 高按实际行数算。否则深层级节点路径、长字段名会溢出固定窗口，且字段名右对齐时一会儿留缝一会儿超框。
        /// </summary>
        public override Vector2 GetWindowSize()
        {
            var data = _root != null ? _root.GetComponent<UIBindingData>() : null;
            var entry = data != null ? data.Find(_path) : null;
            string rootName = _root != null ? _root.name : string.Empty;
            var profile = UICodeGenProfile.Resolve();

            const float pad = 14f, rightGap = 6f, minW = 300f, maxW = 640f;
            string title = string.IsNullOrEmpty(_path) ? "(根)" : _path;
            float w = EditorStyles.boldLabel.CalcSize(new GUIContent(title)).x;

            float line = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            float h;
            if (!_editable)
            {
                int rows = 0;
                if (entry != null)
                {
                    int typeCount = entry.ComponentTypes.Count;
                    foreach (var id in entry.ComponentTypes)
                    {
                        var t = UIBindingUtil.ResolveType(id);
                        string comp = t != null ? t.Name : id;
                        string fn = t != null ? UIBindingUtil.EffectiveFieldName(entry, t, typeCount, rootName, profile) : "—";
                        w = Mathf.Max(w, EditorStyles.label.CalcSize(new GUIContent("　" + comp + "  →  " + fn)).x);
                        rows++;
                    }
                }
                h = (3 + Mathf.Max(1, rows)) * line + 30f; // 标题 + 表头 + N 行(至少 1) + 只读提示
            }
            else
            {
                w = Mathf.Max(w, EditorStyles.label.CalcSize(new GUIContent("绑定组件（勾选 = 各生成一个字段）：")).x);
                int compCount = 0;
                if (entry != null && _node != null)
                {
                    int typeCount = entry.ComponentTypes.Count;
                    w = Mathf.Max(w, ToggleRowWidth(entry, typeof(GameObject), "GameObject", rootName, profile, typeCount));
                    foreach (var comp in _node.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        compCount++;
                        w = Mathf.Max(w, ToggleRowWidth(entry, comp.GetType(), comp.GetType().Name, rootName, profile, typeCount));
                    }
                }
                h = (5 + compCount) * line + 34f; // 标题 + 字段名 + 组件标签 + (GameObject + N 组件) + 解绑
            }

            return new Vector2(Mathf.Clamp(w + pad + rightGap, minW, maxW), h);
        }

        // 一行组件勾选项的渲染宽：勾选框+组件名 + 最小间隔 + 右侧字段名（勾上时）。与 ToggleType 的渲染口径一致，确保窗口够宽、字段名能右对齐。
        private static float ToggleRowWidth(UIBindingEntry entry, System.Type type, string compName, string rootName, UICodeGenProfile profile, int typeCount)
        {
            float toggleW = CheckboxW + EditorStyles.label.CalcSize(new GUIContent(compName)).x;
            float fieldW = 0f;
            if (entry.ComponentTypes.Contains(UIBindingUtil.TypeId(type)))
                fieldW = FieldHint.CalcSize(new GUIContent("→ " + UIBindingUtil.EffectiveFieldName(entry, type, typeCount, rootName, profile))).x;
            return toggleW + 16f + fieldW; // 16 = 组件名与字段名之间的最小间隔
        }

        public override void OnGUI(Rect rect)
        {
            // 标题行作拖拽把手，挪整个弹窗（与绑定清单弹窗共用同一套拖拽，须在最前调用以稳定 GetControlID）。
            UIBindingListView.DragHandle(editorWindow, rect.width, ref _dragMouseStart, ref _dragWinStart);

            var data = _root != null ? _root.GetComponent<UIBindingData>() : null;
            var entry = data != null ? data.Find(_path) : null;
            if (data == null || entry == null)
            {
                EditorGUILayout.LabelField("该节点已解绑。", EditorStyles.boldLabel);
                if (GUILayout.Button("关闭")) editorWindow.Close();
                return;
            }

            // 标题=节点路径：窗口已按内容自适应宽度，路径通常整串显示；只有超深路径（超过宽度上限）才前端省略，
            // 保留更要紧的尾部（叶子节点名 + 近父），整串进 tooltip 悬浮可看。
            string titleText = string.IsNullOrEmpty(_path) ? "(根)" : _path;
            EditorGUILayout.LabelField(
                new GUIContent(FitPathFront(titleText, EditorStyles.boldLabel, rect.width - 12f), titleText),
                EditorStyles.boldLabel);

            if (!_editable) { DrawReadonly(entry); return; }

            EditorGUI.BeginChangeCheck();
            string newName = EditorGUILayout.TextField(new GUIContent("字段名", "留空 = 由节点名推导；非法字符即时清成合法标识符"), entry.FieldName);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(data, "改 UI 绑定字段名");
                entry.FieldName = UIBindingDataEditor.SanitizeFieldNameInput(newName);
                MarkDirty(data);
            }

            EditorGUILayout.LabelField("绑定组件（勾选 = 各生成一个字段）：");
            if (_node != null)
            {
                string rootName = _root != null ? _root.name : string.Empty;
                var profile = UICodeGenProfile.Resolve();
                var dups = UIBindingUtil.DuplicateFieldNames(data.Entries, rootName, profile);
                int typeCount = entry.ComponentTypes.Count;

                // GameObject 不是 Component（不在 GetComponents 里），单列一个勾选项——绑节点本身用于 SetActive / 销毁 / 换父等。
                ToggleType(data, entry, typeof(GameObject),
                    new GUIContent("GameObject", "绑定该节点的 GameObject 本身（生成 GameObject 字段，用 SetActive / 销毁 / 换父等）"),
                    rootName, profile, typeCount, dups);

                foreach (var comp in _node.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    ToggleType(data, entry, comp.GetType(), new GUIContent(comp.GetType().Name),
                        rootName, profile, typeCount, dups);
                }
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("解绑"))
            {
                Undo.RecordObject(data, "解绑 UI 节点");
                data.RemovePath(_path);
                MarkDirty(data);
                editorWindow.Close();
            }
        }

        // 只读视图（运行实例点徽标）：复用编辑模式的「逐组件竖排」呈现——每个绑定组件单独一行「组件 → 字段名」，
        // 不再把字段名/组件挤成两条逗号长串（组件一多就溢出窗口）。不放任何改写控件。
        private void DrawReadonly(UIBindingEntry entry)
        {
            string rootName = _root != null ? _root.name : string.Empty;
            var profile = UICodeGenProfile.Resolve();

            EditorGUILayout.LabelField("绑定组件 → 字段名：");
            if (entry.ComponentTypes.Count == 0)
                EditorGUILayout.LabelField("　（未选组件）", EditorStyles.miniLabel);
            else
            {
                int typeCount = entry.ComponentTypes.Count;
                foreach (var id in entry.ComponentTypes)
                {
                    var t = UIBindingUtil.ResolveType(id);
                    string comp = t != null ? t.Name : id;
                    string fn = t != null ? UIBindingUtil.EffectiveFieldName(entry, t, typeCount, rootName, profile) : "—";
                    EditorGUILayout.LabelField("　" + comp + "  →  " + fn);
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("只读——停止运行、进 prefab 编辑模式才能改。", EditorStyles.miniLabel);
        }

        // 一个「绑定某类型 = 各生成一个字段」勾选项：左侧勾选框 + 组件名，按类型 id 在条目里增删（GameObject / 各组件共用）；
        // 勾上的项右侧显示它将生成的字段名（含命名模板/别名/多组件后缀/自定义名，撞名加 ⚠ 橙色）——所见即所得。
        private static void ToggleType(UIBindingData data, UIBindingEntry entry, System.Type type, GUIContent label,
            string rootName, UICodeGenProfile profile, int typeCount, HashSet<string> dups)
        {
            string id = UIBindingUtil.TypeId(type);
            bool on = entry.ComponentTypes.Contains(id);

            EditorGUILayout.BeginHorizontal();
            // 勾选项定宽（勾选框 + 组件名），余下空间留给 FlexibleSpace，把右侧字段名稳定顶到最右——比 ExpandWidth(false) 在窄窗里更可控。
            bool now = EditorGUILayout.ToggleLeft(label, on, GUILayout.Width(CheckboxW + EditorStyles.label.CalcSize(label).x));
            if (on)
            {
                string fn = UIBindingUtil.EffectiveFieldName(entry, type, typeCount, rootName, profile);
                bool dup = dups != null && dups.Contains(fn);
                GUILayout.FlexibleSpace();
                var prev = GUI.color;
                if (dup) GUI.color = new Color(0.95f, 0.74f, 0.33f);
                GUILayout.Label(new GUIContent((dup ? "⚠ " : "→ ") + fn,
                    dup ? $"字段名「{fn}」与其它节点重复——生成时会跳过其一" : "勾上后将生成的字段名"), FieldHint);
                GUILayout.Space(6f); // 与窗口右缘留一点缝隙，别贴边
                GUI.color = prev;
            }
            EditorGUILayout.EndHorizontal();

            if (now == on) return;
            Undo.RecordObject(data, "改 UI 绑定组件");
            if (now) entry.ComponentTypes.Add(id);
            else entry.ComponentTypes.Remove(id);
            MarkDirty(data);
        }

        // 右侧字段名提示样式：右对齐 miniLabel（与勾选项同高、贴右排）。
        private static GUIStyle _fieldHint;
        private static GUIStyle FieldHint => _fieldHint ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

        // 路径前端省略：放得下就原样返回；放不下则从前往后丢整段、保留尽量多的尾部（前缀「…/」），末段自己都放不下时对末段做字符级前端省略。
        // 路径的关键信息在尾部（叶子节点 + 近父），故砍前不砍后；只在超过窗口宽度上限的超深路径才触发，普通路径靠窗口自适应整串显示。
        private static string FitPathFront(string path, GUIStyle style, float maxWidth)
        {
            if (style.CalcSize(new GUIContent(path)).x <= maxWidth) return path;
            var segs = path.Split('/');
            string acc = segs[segs.Length - 1];
            if (style.CalcSize(new GUIContent("…/" + acc)).x > maxWidth)
            {
                for (int i = 1; i < acc.Length; i++)
                {
                    string c = "…" + acc.Substring(i);
                    if (style.CalcSize(new GUIContent(c)).x <= maxWidth) return c;
                }
                return "…";
            }
            for (int i = segs.Length - 2; i >= 0; i--)
            {
                string candidate = segs[i] + "/" + acc;
                if (style.CalcSize(new GUIContent("…/" + candidate)).x > maxWidth) break;
                acc = candidate;
            }
            return "…/" + acc;
        }

        // 改完标脏：出 * / 参与撤销 / 随 Ctrl+S 落盘，并刷新 Hierarchy 徽标。
        private static void MarkDirty(UIBindingData data)
        {
            EditorUtility.SetDirty(data);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
            EditorApplication.RepaintHierarchyWindow();
        }
    }
}
