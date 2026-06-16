using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 点 Hierarchy 里 prefab 根行信息徽标弹出的「窗口绑定总览」：列出全部绑定，并提供生成目标设置 + 生成按钮。
    /// 根行刻意<b>不</b>放即点即生成的按钮（易误触）——生成藏在本弹窗里、需明确点击。
    /// </summary>
    public sealed class UIBindingRootPopup : PopupWindowContent
    {
        private readonly string _assetPath;
        private readonly GameObject _root;
        private readonly bool _editable;
        private Vector2 _scroll;

        public UIBindingRootPopup(string assetPath, GameObject root, bool editable)
        {
            _assetPath = assetPath;
            _root = root;
            _editable = editable;
        }

        public override Vector2 GetWindowSize() => new(340, 360);

        public override void OnGUI(Rect rect)
        {
            var data = _root != null ? _root.GetComponent<UIBindingData>() : null;
            if (data == null)
            {
                EditorGUILayout.LabelField("本 prefab 没有绑定数据。", EditorStyles.boldLabel);
                if (GUILayout.Button("关闭")) editorWindow.Close();
                return;
            }

            EditorGUILayout.LabelField($"{_root.name} · 窗口绑定（{data.Entries.Count}）", EditorStyles.boldLabel);
            if (!_editable)
                EditorGUILayout.HelpBox("只读——双击 prefab 进入编辑模式才能改。", MessageType.None);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(110));
            foreach (var entry in data.Entries)
            {
                string fieldName = !string.IsNullOrEmpty(entry.FieldName)
                    ? entry.FieldName
                    : UIBindingUtil.DeriveBaseName(entry.Path, _root.name);
                var comps = new System.Collections.Generic.List<string>();
                foreach (var id in entry.ComponentTypes)
                {
                    var t = UIBindingUtil.ResolveType(id);
                    comps.Add(t != null ? t.Name : id);
                }
                EditorGUILayout.LabelField(new GUIContent($"◈ {fieldName}", entry.Path),
                    new GUIContent($"{(string.IsNullOrEmpty(entry.Path) ? "(根)" : entry.Path)}  ·  {string.Join(", ", comps)}"));
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            UIBindingGenGUI.Draw(data, _assetPath, _editable);
        }
    }
}
