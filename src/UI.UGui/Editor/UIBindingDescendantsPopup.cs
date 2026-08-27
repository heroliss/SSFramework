using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 点 Hierarchy 行尾「子孙绑定聚合标记」(<c>◈N</c>) 弹出的只读清单：列出某节点子树里全部已绑定的子孙
    /// （相对子路径 + 生成字段名 + 组件），每条一键定位。折叠父节点也能一眼看清里面绑了什么。表格外观与「绑定总览」弹窗一致。
    /// </summary>
    internal sealed class UIBindingDescendantsPopup : UIBindingListPopup
    {
        private readonly UIBindingData _data;
        private readonly string _ancestorPath; // 被点击节点路径（其子孙路径都以 "ancestorPath/" 打头）
        private readonly Transform _root;       // 活窗口根，按它解析定位

        public UIBindingDescendantsPopup(UIBindingData data, string ancestorPath, Transform root)
        {
            _data = data;
            _ancestorPath = ancestorPath ?? string.Empty;
            _root = root;
        }

        protected override Transform Root => _root;
        protected override string Title => $"{LeafOf(_ancestorPath)} · 子孙绑定（{RowCount}）";
        protected override float MaxListHeight => 560f;

        protected override List<UIBindingListView.Row> BuildRows()
        {
            var rows = new List<UIBindingListView.Row>();
            if (_data == null) return rows;
            string prefix = _ancestorPath + "/";
            string rootName = _root != null ? _root.name : string.Empty;
            UICodeGenProfile.TryResolve(out var profile);
            foreach (var e in _data.Entries)
                if (!string.IsNullOrEmpty(e.Path) && e.Path.StartsWith(prefix, StringComparison.Ordinal))
                    rows.Add(UIBindingListView.ToRow(e, e.Path.Substring(prefix.Length), rootName, profile)); // 相对子路径
            return rows;
        }

        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "(根)";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }
    }
}
