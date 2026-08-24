using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// <see cref="UIBindingData"/> 的 Inspector：把绑定清单可读地列出（组件 id → 类型短名），并就地改字段名 / 解绑 / 生成代码。
    /// 仅在 Prefab 编辑模式下可改（实例 / 资产视图只读，与 Hierarchy 徽标一致）——绑定是 prefab 资产级属性。
    /// </summary>
    [CustomEditor(typeof(UIBindingData))]
    public sealed class UIBindingDataEditor : UnityEditor.Editor
    {
        // 哪些条目展开了「字段名」编辑框（按节点路径记，路径在一份清单内唯一）。自定义字段名不常用，默认收起、点按钮才展开。
        private readonly HashSet<string> _expandedFieldName = new();

        private const float ToggleW = 50f;  // 字段名开关列宽
        private const float UnbindW = 40f;  // 解绑列宽
        private const float CompactBreakpoint = 360f;

        // 三列宽度按当前 Inspector 面板宽实算成「定宽」（而非 ExpandWidth）：ExpandWidth 会让每列先吃本行内容、再分余量，
        // 各行路径长短不一就列宽不一、对不齐；定宽则所有行（含表头）严格同宽 → 列对齐，过长截断挂 tooltip。随面板宽每帧重算，仍自适应。
        private static GUILayoutOption[] FixedCol(float w) => new[] { GUILayout.Width(w) };

        public override void OnInspectorGUI()
        {
            var data = (UIBindingData)target;
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            bool editable = stage != null && stage.IsPartOfPrefabContents(data.gameObject);

            EditorGUILayout.HelpBox(
                "窗口节点绑定清单（编辑期元数据，运行时不读）。" +
                (editable ? "在此或 Hierarchy 行尾徽标编辑，Ctrl+S 随 prefab 保存。" : "只读——双击 prefab 进入编辑模式才能改。"),
                MessageType.Info);

            if (data.Entries.Count == 0)
                EditorGUILayout.LabelField("（暂无绑定。Prefab 编辑模式下选子节点右键标记 / Hierarchy 点「＋」。）");

            var profile = UICodeGenProfile.Resolve();
            // 字段重名提示：算出会撞名的字段名集合，画行时给命中的字段名加 ⚠（与 Hierarchy 徽标口径一致）。
            var duplicates = UIBindingUtil.DuplicateFieldNames(data.Entries, data.name, profile);
            bool compact = UseCompactLayout(EditorGUIUtility.currentViewWidth);

            // 按面板宽实算三列定宽：扣掉定位 + 右侧两按钮，再留出面板内边距 + 竖滚动条 + 右缘空隙（否则解绑按钮会贴边/被滚动条压住显示不全），
            // 剩余给中间三列，字段名优先（≤160），其余按 ~52/48 分给路径/组件。
            const float panelPad = 22f;  // 面板左右内边距
            const float scrollbar = 14f; // 竖滚动条（长清单常驻）
            const float rightGap = 6f;   // 解绑按钮与右缘的空隙
            GUILayoutOption[] nameCol = null;
            GUILayoutOption[] pathCol = null;
            GUILayoutOption[] compCol = null;
            if (!compact)
            {
                float avail = Mathf.Max(150f, EditorGUIUtility.currentViewWidth - panelPad - scrollbar - rightGap
                                              - UIBindingListView.LocateW - ToggleW - UnbindW);
                float fieldW = Mathf.Min(160f, avail * 0.34f);
                float restW = avail - fieldW;
                float pathW = restW * 0.52f;
                float compW = restW - pathW;
                nameCol = FixedCol(fieldW);
                pathCol = FixedCol(pathW);
                compCol = FixedCol(compW);
            }

            // 表头：与下面各列同宽（含右侧两个按钮位的占位），列对齐成表格。列结构与两个绑定弹窗一致（◎ + 字段名 + 节点路径 + 组件）。
            if (!compact && data.Entries.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(GUIContent.none, GUILayout.Width(UIBindingListView.LocateW));
                GUILayout.Label("字段名", EditorStyles.miniBoldLabel, nameCol);
                GUILayout.Label("节点路径", EditorStyles.miniBoldLabel, pathCol);
                GUILayout.Label("组件", EditorStyles.miniBoldLabel, compCol);
                GUILayout.Label(GUIContent.none, GUILayout.Width(ToggleW));
                GUILayout.Label(GUIContent.none, GUILayout.Width(UnbindW));
                EditorGUILayout.EndHorizontal();
            }

            UIBindingEntry remove = null;
            for (int i = 0; i < data.Entries.Count; i++)
            {
                var entry = data.Entries[i];
                // 与两个绑定弹窗共用 DrawRowBlock：◎ 定位 + 字段名/组件逐行成对竖排 + 节点路径只显示一次 + 同节点斑马成块。
                // 字段名开关 / 解绑这类 Inspector 专属行级操作经 trailing 追加到块最右侧（定宽常在，窗口变窄只压中间三列）。
                var row = UIBindingListView.ToRow(entry, string.IsNullOrEmpty(entry.Path) ? "(根)" : entry.Path, data.name, profile);
                System.Action trailing = () =>
                {
                    // 字段名开关：已自定义用蓝调底色提示（替代易和「未保存」混淆的 *），只读时禁用。
                    using (new EditorGUI.DisabledScope(!editable))
                    {
                        bool expanded = _expandedFieldName.Contains(entry.Path);
                        bool customized = !string.IsNullOrEmpty(entry.FieldName);
                        var prevBg = GUI.backgroundColor;
                        if (customized) GUI.backgroundColor = new Color(0.5f, 0.78f, 0.96f);
                        var nameBtn = new GUIContent("字段名",
                            customized ? "已自定义字段名（点开可改 / 清空，留空=自动推导）" : "自定义字段名（留空 = 由节点名推导）");
                        bool now = GUILayout.Toggle(expanded, nameBtn, EditorStyles.miniButton, GUILayout.Width(ToggleW));
                        GUI.backgroundColor = prevBg;
                        if (now != expanded) { if (expanded) _expandedFieldName.Remove(entry.Path); else _expandedFieldName.Add(entry.Path); }
                    }

                    // 解绑：用 miniButton（与字段名开关同高、与 ◎ 顶部对齐），定宽常在。
                    using (new EditorGUI.DisabledScope(!editable))
                        if (GUILayout.Button("解绑", EditorStyles.miniButton, GUILayout.Width(UnbindW))) remove = entry;
                };

                if (compact)
                {
                    UIBindingListView.DrawCompactRowBlock(row,
                        _ => LocateNode(data, entry.Path), trailing, duplicates);
                }
                else
                {
                    UIBindingListView.DrawRowBlock(row, i, nameCol, pathCol, compCol,
                        _ => LocateNode(data, entry.Path), trailing, duplicates);
                }

                // 展开时显示自定义字段名输入框（收起时不占空间）。
                if (_expandedFieldName.Contains(entry.Path))
                    using (new EditorGUI.DisabledScope(!editable))
                    {
                        EditorGUI.BeginChangeCheck();
                        string fn = EditorGUILayout.TextField(
                            new GUIContent("　自定义字段名", "留空 = 由节点名推导；非法字符即时清成合法标识符；多组件时各自加组件名后缀"), entry.FieldName);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(data, "改 UI 绑定字段名");
                            entry.FieldName = SanitizeFieldNameInput(fn);
                            MarkDirty(data, stage);
                        }
                    }
            }

            if (remove != null)
            {
                Undo.RecordObject(data, "解绑 UI 节点");
                data.Entries.Remove(remove);
                _expandedFieldName.Remove(remove.Path);
                MarkDirty(data, stage);
            }

            EditorGUILayout.Space();
            string assetPath = editable ? stage.assetPath : AssetDatabase.GetAssetPath(data);
            if (string.IsNullOrEmpty(assetPath))
                EditorGUILayout.HelpBox("拿不到 prefab 路径（请从 Project 选中 prefab 或进编辑模式）。", MessageType.None);
            else
                UIBindingGenGUI.Draw(data, assetPath, editable);
        }

        // 字段名输入清洗：空 / 纯空白保持空（= 自动推导）；非空即时清成合法 C# 标识符（非法字符换 _，数字开头补 _）。
        internal static string SanitizeFieldNameInput(string raw)
            => string.IsNullOrWhiteSpace(raw) ? string.Empty : UIBindingUtil.SanitizeIdentifier(raw);

        /// <summary>窄 Inspector 使用卡片，保证定位、字段名和解绑操作不被固定表格列裁掉。</summary>
        internal static bool UseCompactLayout(float viewWidth) => viewWidth < CompactBreakpoint;

        // 定位：以绑定数据所在节点为根按路径找节点，选中并 ping。data 根在编辑模式=Stage 根、场景实例=实例根、运行时=运行实例根，
        // 三种语境都指向当前活节点，所以定位不随只读禁用（运行时也能点）。
        private static void LocateNode(UIBindingData data, string path)
        {
            var root = data.transform;
            var node = string.IsNullOrEmpty(path) ? root : root.Find(path);
            if (node == null) return;
            Selection.activeGameObject = node.gameObject;
            EditorGUIUtility.PingObject(node.gameObject);
        }

        private static void MarkDirty(UIBindingData data, PrefabStage stage)
        {
            EditorUtility.SetDirty(data);
            if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
            EditorApplication.RepaintHierarchyWindow();
        }
    }
}
