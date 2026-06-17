using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 绑定清单的统一表格渲染——「窗口绑定总览」弹窗、「子孙聚合」弹窗、<see cref="UIBindingDataEditor"/> Inspector 三处共用，口径/外观一致。
    /// 一个节点一块（◎ 定位 + 节点路径只显示一次），其上每个组件占一行（字段名 ↔ 组件成对，因为一个组件就是一个字段），
    /// 多组件不再横向溢出；同一节点的多行用斑马底色成块，一眼看出属于同一节点。
    /// 列宽由调用方按 GUILayoutOption 给定（弹窗按 <see cref="Measure"/> 结果定宽、Inspector 给「下限 + 撑满」随面板伸缩），过长截断挂 tooltip。
    /// </summary>
    internal static class UIBindingListView
    {
        /// <summary>一个节点一行（块）。<see cref="Members"/> = 该节点每个组件对应的「字段名 ↔ 组件」对。</summary>
        public sealed class Row
        {
            public string PathLabel; // 显示用路径（总览/Inspector=完整相对路径；子孙=相对子路径）
            public string FullPath;  // 定位用完整路径（相对窗口根）
            public List<(string Field, string Comp)> Members = new();
        }

        public struct Layout { public float Field, Path, Comp, Width; }

        public const float LocateW = 24f; // ◎ 定位列宽（三处共用，对齐表头占位）
        private const float Gap = 6f;      // 列间余量
        private const float Margin = 18f;  // 弹窗左右留白
        private const string EmptyField = "—";    // 无组件时字段名列占位
        private const string EmptyComp = "（无）"; // 无组件时组件列占位（短，窄列也容得下）
        private static readonly Color ZebraColor = new(0.5f, 0.5f, 0.5f, 0.10f);

        public static float RowHeight => EditorGUIUtility.singleLineHeight + 2f;

        /// <summary>内容总高（表头 + 每节点按组件数撑开的行），供弹窗高度自适应（略偏高，宁多勿少避免冒滚动条）。</summary>
        public static float ContentHeight(IReadOnlyList<Row> rows)
        {
            int lines = 1; // 表头
            foreach (var r in rows) lines += Mathf.Max(1, r.Members.Count);
            return RowHeight * lines;
        }

        /// <summary>列宽 = 表头与各行内容取最大、各自夹上限；总宽供弹窗定宽。Inspector 不用本结果（随面板伸缩）。</summary>
        public static Layout Measure(IReadOnlyList<Row> rows)
        {
            var st = EditorStyles.label;
            float f = st.CalcSize(new GUIContent("字段名")).x;
            float p = st.CalcSize(new GUIContent("节点路径")).x;
            float c = st.CalcSize(new GUIContent("组件")).x;
            foreach (var r in rows)
            {
                p = Mathf.Max(p, st.CalcSize(new GUIContent(r.PathLabel)).x);
                if (r.Members.Count == 0) // 无组件行也要让占位文本（—/（无））量进列宽，否则列太窄被截
                {
                    f = Mathf.Max(f, st.CalcSize(new GUIContent(EmptyField)).x);
                    c = Mathf.Max(c, st.CalcSize(new GUIContent(EmptyComp)).x);
                }
                foreach (var m in r.Members)
                {
                    f = Mathf.Max(f, st.CalcSize(new GUIContent(m.Field)).x);
                    c = Mathf.Max(c, st.CalcSize(new GUIContent(m.Comp)).x);
                }
            }
            f = Mathf.Min(f, 200f); p = Mathf.Min(p, 220f); c = Mathf.Min(c, 200f);
            var l = new Layout { Field = f, Path = p, Comp = c };
            l.Width = LocateW + f + p + c + Gap * 3f + Margin;
            return l;
        }

        /// <summary>一条 entry → 表格行：每个组件配一行「字段名 ↔ 组件」（口径与生成器一致，一个组件就是一个字段）。
        /// pathLabel 由调用方决定（总览/Inspector=完整相对路径；子孙=相对子路径）。</summary>
        public static Row ToRow(UIBindingEntry e, string pathLabel, string rootName, UICodeGenProfile profile)
        {
            var names = UIBindingUtil.EffectiveFieldNames(e, rootName, profile);
            var row = new Row { PathLabel = pathLabel, FullPath = e.Path };
            var ids = e.ComponentTypes;
            for (int i = 0; i < ids.Count; i++)
            {
                var t = UIBindingUtil.ResolveType(ids[i]);
                string comp = t != null ? t.Name : ids[i];
                string field = i < names.Count ? names[i] : "?";
                row.Members.Add((field, comp));
            }
            return row;
        }

        /// <summary>弹窗用：表头 + 各节点块，定宽列（窗口已按 <see cref="Measure"/> 定宽）。点 <c>◎</c> 回调收到该节点完整路径。</summary>
        public static void Draw(IReadOnlyList<Row> rows, Layout layout, Action<string> onLocate)
        {
            var fieldCol = new[] { GUILayout.Width(layout.Field) };
            var pathCol = new[] { GUILayout.Width(layout.Path) };
            var compCol = new[] { GUILayout.Width(layout.Comp) };

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(GUIContent.none, GUILayout.Width(LocateW));
            GUILayout.Label("字段名", EditorStyles.miniBoldLabel, fieldCol);
            GUILayout.Label("节点路径", EditorStyles.miniBoldLabel, pathCol);
            GUILayout.Label("组件", EditorStyles.miniBoldLabel, compCol);
            EditorGUILayout.EndHorizontal();

            if (rows.Count == 0) { EditorGUILayout.LabelField("（无）"); return; }

            var dups = DuplicateFields(rows);
            for (int i = 0; i < rows.Count; i++)
                DrawRowBlock(rows[i], i, fieldCol, pathCol, compCol, onLocate, duplicateFields: dups);
        }

        /// <summary>这批行里会撞名的字段名（≥2 行同名）——画 ⚠ 重名提示用。空集 = 无重名。</summary>
        public static HashSet<string> DuplicateFields(IReadOnlyList<Row> rows)
        {
            var counts = new Dictionary<string, int>();
            foreach (var r in rows)
                foreach (var m in r.Members)
                    counts[m.Field] = counts.TryGetValue(m.Field, out int c) ? c + 1 : 1;
            var dups = new HashSet<string>();
            foreach (var kv in counts)
                if (kv.Value > 1) dups.Add(kv.Key);
            return dups;
        }

        /// <summary>
        /// 画一个节点块：斑马底色 + <c>◎</c> 定位 + 字段名(逐组件竖排) + 节点路径(只显示一次) + 组件(逐组件竖排)，可选在右侧追加操作按钮。
        /// 字段名/组件列用「<c>\n</c> 拼成的多行 <see cref="GUILayout.Label"/>」而非竖排子组——与表头同为 Label，列宽口径一致才对得齐
        /// （竖排子组的 ExpandWidth 与 Label 的不一致，会让表头与各行错位）。<b>不</b>给单元格强制行高：让多行 Label 按自身内容自适应高度，
        /// 块高即由它撑出 → 多行块底部不再多出一截空隙（强制成 行数×singleLineHeight 会比实际文本高、留底缝）。
        /// 列宽由调用方按 <see cref="GUILayoutOption"/> 传入（弹窗给定宽、Inspector 给「下限 + 撑满」随面板伸缩）。
        /// <paramref name="trailing"/>（行级操作按钮：字段名开关 / 解绑）直接接在组件列后——靠 ExpandWidth 列把空间吃满、把它们顶到最右，
        /// <b>不</b>另垫 FlexibleSpace（那会让各列塌回下限、组件列紧贴路径列）。
        /// <paramref name="duplicateFields"/> 命中的字段名前缀 <c>⚠</c> 提示重名（生成时会跳过其一）。
        /// </summary>
        public static void DrawRowBlock(Row r, int index,
            GUILayoutOption[] fieldCol, GUILayoutOption[] pathCol, GUILayoutOption[] compCol,
            Action<string> onLocate, Action trailing = null, ISet<string> duplicateFields = null)
        {
            string fieldText, compText;
            if (r.Members.Count == 0) { fieldText = EmptyField; compText = EmptyComp; }
            else
            {
                fieldText = string.Empty; compText = string.Empty;
                for (int i = 0; i < r.Members.Count; i++)
                {
                    if (i > 0) { fieldText += "\n"; compText += "\n"; }
                    string fld = r.Members[i].Field;
                    // 撞名字段加 ⚠ 前缀（与 Hierarchy 徽标口径一致）——生成时这类字段会被跳过其一。
                    fieldText += duplicateFields != null && duplicateFields.Contains(fld) ? "⚠ " + fld : fld;
                    compText += r.Members[i].Comp;
                }
            }

            Rect block = EditorGUILayout.BeginHorizontal();
            // 斑马底色：同一节点的多行共用一块，一眼看出属于同一节点（隔行上色，相邻节点也分得开）。
            if (Event.current.type == EventType.Repaint && (index & 1) == 1)
                EditorGUI.DrawRect(block, ZebraColor);

            // ◎ 定位（一个节点一个）
            if (GUILayout.Button(new GUIContent("◎", "定位该节点（选中并高亮）"), EditorStyles.miniButton, GUILayout.Width(LocateW)))
                onLocate?.Invoke(r.FullPath);

            GUILayout.Label(new GUIContent(fieldText, fieldText), Cell, fieldCol);
            GUILayout.Label(new GUIContent(r.PathLabel, string.IsNullOrEmpty(r.FullPath) ? "(根)" : r.FullPath), Cell, pathCol);
            GUILayout.Label(new GUIContent(compText, compText), Cell, compCol);

            trailing?.Invoke();

            EditorGUILayout.EndHorizontal();
        }

        // 单元格文本样式：竖向居中（单行与 ◎ 按钮对齐；多行块按内容自适应高度、无强制留白，居中即填满）、不换行（过长截断、tooltip 给全文）。
        private static GUIStyle _cell;
        private static GUIStyle Cell => _cell ??= new GUIStyle(EditorStyles.label) { wordWrap = false };

        /// <summary>
        /// 弹窗标题条拖拽把手：在标题条按下拖动即移动整窗。两类绑定弹窗共用。
        /// ① 按下即 <c>hotControl = id</c> 捕获鼠标——光标拖出窗口范围也持续收到 MouseDrag/MouseUp，拖拽不中断；
        /// ② 用 <see cref="GUIUtility.GUIToScreenPoint"/>(鼠标) 的「屏幕坐标」按相对起点位移定位，而非 e.delta（窗口被挪后 delta 相对新位置算 → 抖动偏移）。
        /// 调用方各持一份起点状态（<paramref name="mouseStart"/>/<paramref name="winStart"/>），并在 OnGUI 顶部首先调用（保证 GetControlID 稳定）。
        /// </summary>
        public static void DragHandle(EditorWindow window, float width, ref Vector2 mouseStart, ref Vector2 winStart)
        {
            if (window == null) return;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var strip = new Rect(0f, 0f, width, EditorGUIUtility.singleLineHeight + 2f);
            EditorGUIUtility.AddCursorRect(strip, MouseCursor.Pan);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when strip.Contains(e.mousePosition):
                    GUIUtility.hotControl = id;
                    mouseStart = GUIUtility.GUIToScreenPoint(e.mousePosition);
                    winStart = window.position.position;
                    e.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    var p = window.position;
                    p.position = winStart + (GUIUtility.GUIToScreenPoint(e.mousePosition) - mouseStart);
                    window.position = p;
                    e.Use();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }
        }
    }
}
