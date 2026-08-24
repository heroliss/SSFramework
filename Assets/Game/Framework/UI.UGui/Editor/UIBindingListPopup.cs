using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 绑定清单弹窗的公共骨架——「窗口绑定总览」与「子孙聚合」两弹窗共用：标题 + 自适应大小 + 滚动表格 + 行内 <c>◎</c> 定位。
    /// 子类只提供「窗口根 / 标题 / 怎么取行」，需要时再在列表下方追加内容（如总览弹窗的生成面板）。
    /// 仍是无标题栏、失焦自动关的 <see cref="PopupWindowContent"/>（与不可拖前一致）；额外把标题行做成拖拽把手——
    /// 经 <see cref="PopupWindowContent.editorWindow"/> 直接挪宿主窗口位置，于是「外观不变、点别处自动关」之外多了可拖动。
    /// </summary>
    internal abstract class UIBindingListPopup : PopupWindowContent
    {
        private List<UIBindingListView.Row> _rows;
        private UIBindingListView.Layout _layout;
        private float _titleWidth;
        private float _measuredBodyHeight = -1f; // OnGUI 实测滚动内容总高；>0 后接管首帧估算
        private Vector2 _scroll;
        private Vector2 _dragMouseStart; // 拖拽起点的鼠标屏幕坐标
        private Vector2 _dragWinStart;   // 拖拽起点的窗口左上角

        /// <summary>解析定位用的活窗口根（编辑 Stage 根 / 场景实例根 / 运行实例根）。</summary>
        protected abstract Transform Root { get; }
        protected abstract string Title { get; }
        protected abstract List<UIBindingListView.Row> BuildRows();

        /// <summary>已构建的行数（标题里显示数量、子类算标题用）。</summary>
        protected int RowCount => _rows != null ? _rows.Count : 0;

        protected virtual void DrawExtra() { }       // 列表下方追加内容（默认无）
        protected virtual float ExtraHeight => 0f;   // 上面那块的高度（参与窗口自适应）
        protected virtual float MaxListHeight => 560f;
        protected virtual float MinWidth => 320f;

        // 行/布局/标题宽只在首次（GetWindowSize 或 DrawGUI 先到者）算一次；只读内容，不必每帧重建。
        private void Ensure()
        {
            if (_rows != null) return;
            _rows = BuildRows() ?? new List<UIBindingListView.Row>();
            _layout = UIBindingListView.Measure(_rows);
            _titleWidth = EditorStyles.boldLabel.CalcSize(new GUIContent(Title)).x + 16f;
        }

        private float EstimatedBodyHeight()
            => Mathf.Min(UIBindingListView.ContentHeight(_rows) + 8f, MaxListHeight) + ExtraHeight;

        private float HeaderHeight => EditorGUIUtility.singleLineHeight + 8f;

        private float BodyViewportHeight()
        {
            float desired = _measuredBodyHeight > 0f ? _measuredBodyHeight : EstimatedBodyHeight();
            return UIBindingPopupLayout.CalculateBodyViewportHeight(
                desired,
                UIBindingPopupLayout.ResolveMaxWindowHeight(editorWindow),
                HeaderHeight);
        }

        /// <summary>
        /// 弹窗尺寸（宽按内容夹在 <see cref="MinWidth"/>..700）。高度首帧用估算，OnGUI 实测后接管；
        /// 超过当前显示器工作区时只压缩滚动视口，列表和附加操作仍全部可达。
        /// </summary>
        public override Vector2 GetWindowSize()
        {
            Ensure();
            float w = Mathf.Clamp(Mathf.Max(_layout.Width, _titleWidth), MinWidth, 700f);
            float h = HeaderHeight + BodyViewportHeight();
            return new(w, h);
        }

        public override void OnGUI(Rect rect)
        {
            Ensure();
            // 标题行作拖拽把手，挪整个弹窗（与节点绑定弹窗共用同一套拖拽，须在最前调用以稳定 GetControlID）。
            UIBindingListView.DragHandle(editorWindow, rect.width, ref _dragMouseStart, ref _dragWinStart);
            EditorGUILayout.LabelField(Title, EditorStyles.boldLabel);

            // 表格与附加面板共用一个纵向滚动区。这样低工作区下不只“列表能滚”，底部生成设置和按钮也能到达。
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(BodyViewportHeight()));
            if (rect.width + 1f < _layout.Width)
                UIBindingListView.DrawCompact(_rows, Locate);
            else
                UIBindingListView.Draw(_rows, _layout, Locate);
            DrawExtra();

            float measuredBodyHeight = -1f;
            if (Event.current.type == EventType.Repaint)
                measuredBodyHeight = GUILayoutUtility.GetLastRect().yMax + 4f;
            EditorGUILayout.EndScrollView();

            // 真实内容高度只影响视口是否需要滚动，并始终受工作区上限约束；>1px 阈值避免布局亚像素抖动。
            if (Event.current.type == EventType.Repaint && editorWindow != null)
            {
                if (measuredBodyHeight > 1f && Mathf.Abs(measuredBodyHeight - _measuredBodyHeight) > 1f)
                {
                    _measuredBodyHeight = measuredBodyHeight;
                    // PopupWindowContent 的宿主会再次调用 GetWindowSize 并贴合内容；不要在这里锁 min/max，
                    // 否则弹窗被工作区临时夹窄后，拖到更宽显示器也无法恢复期望宽度。
                    editorWindow.Repaint();
                }
            }
        }

        // 按窗口根 + 完整路径定位活节点（编辑/实例/运行时都适用）。
        protected void Locate(string fullPath)
        {
            var root = Root;
            if (root == null) return;
            var node = string.IsNullOrEmpty(fullPath) ? root : root.Find(fullPath);
            if (node == null) return;
            Selection.activeGameObject = node.gameObject;
            EditorGUIUtility.PingObject(node.gameObject);
        }
    }
}
