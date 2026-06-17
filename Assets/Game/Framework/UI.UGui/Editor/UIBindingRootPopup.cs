using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 点 Hierarchy 里 prefab 根行信息徽标弹出的「窗口绑定总览」：表格列出全部绑定（每条可 <c>◎</c> 定位），
    /// 下方提供生成目标设置 + 生成按钮。根行刻意<b>不</b>放即点即生成的按钮（易误触）——生成藏在本弹窗里、需明确点击。
    /// 表格部分与「子孙聚合」弹窗共用 <see cref="UIBindingListPopup"/>，外观一致。
    /// </summary>
    internal sealed class UIBindingRootPopup : UIBindingListPopup
    {
        private string _assetPath;
        private readonly GameObject _root;
        private readonly bool _editable;
        private bool _resolvedRuntimePath;

        public UIBindingRootPopup(string assetPath, GameObject root, bool editable)
        {
            _assetPath = assetPath;
            _root = root;
            _editable = editable;
        }

        protected override Transform Root => _root != null ? _root.transform : null;
        protected override string Title => $"{(_root != null ? _root.name : "(?)")} · 窗口绑定（{RowCount}）";
        protected override float MinWidth => 340f;
        protected override float MaxListHeight => 320f;          // 下面还有生成面板，列表区比子孙弹窗略矮
        protected override float ExtraHeight => (_editable ? 0f : 36f) + 214f; // 只读提示 + 生成面板（宁多勿少）

        protected override List<UIBindingListView.Row> BuildRows()
        {
            var rows = new List<UIBindingListView.Row>();
            var data = _root != null ? _root.GetComponent<UIBindingData>() : null;
            if (data == null) return rows;
            string rootName = _root.name;
            var profile = UICodeGenProfile.Resolve();
            foreach (var e in data.Entries)
                rows.Add(UIBindingListView.ToRow(e, string.IsNullOrEmpty(e.Path) ? "(根)" : e.Path, rootName, profile)); // 完整相对路径
            return rows;
        }

        protected override void DrawExtra()
        {
            var data = _root != null ? _root.GetComponent<UIBindingData>() : null;
            if (data == null)
            {
                EditorGUILayout.LabelField("本 prefab 没有绑定数据。");
                return;
            }

            if (!_editable)
                EditorGUILayout.HelpBox("只读——双击 prefab 进入编辑模式才能改。", MessageType.None);

            // 运行实例 assetPath 传入为 null（无 prefab 连接）：首次靠窗口类名找回 prefab 路径，让脚本引用不再是 None。
            if (string.IsNullOrEmpty(_assetPath) && !_resolvedRuntimePath)
            {
                _assetPath = UIBindingUtil.ResolveRuntimePrefabPath(_root);
                _resolvedRuntimePath = true;
            }

            EditorGUILayout.Space(4);
            UIBindingGenGUI.Draw(data, _assetPath, _editable);
        }
    }
}
