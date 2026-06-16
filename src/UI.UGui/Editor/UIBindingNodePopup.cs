using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>
    /// 点 Hierarchy 绑定徽标 / 「＋」弹出的就地编辑面板：改一个节点的字段名、勾选要绑的组件、解绑、生成代码。
    /// 直接改 prefab 根上的 <see cref="UIBindingData"/>（经 <c>Undo</c>、标脏）——不立即写盘，随 Ctrl+S 保存 prefab 才落盘。
    /// 只在 Prefab 编辑模式下弹出。按路径实时取条目（撤销后引用会失效，故不缓存条目本身）。
    /// </summary>
    public sealed class UIBindingNodePopup : PopupWindowContent
    {
        private readonly string _assetPath;
        private readonly GameObject _root;
        private readonly string _path;
        private readonly GameObject _node;

        public UIBindingNodePopup(string assetPath, GameObject root, string path, GameObject node)
        {
            _assetPath = assetPath;
            _root = root;
            _path = path;
            _node = node;
        }

        public override Vector2 GetWindowSize() => new(300, 210);

        public override void OnGUI(Rect rect)
        {
            var data = _root != null ? _root.GetComponent<UIBindingData>() : null;
            var entry = data != null ? data.Find(_path) : null;
            if (data == null || entry == null)
            {
                EditorGUILayout.LabelField("该节点已解绑。", EditorStyles.boldLabel);
                if (GUILayout.Button("关闭")) editorWindow.Close();
                return;
            }

            EditorGUILayout.LabelField(string.IsNullOrEmpty(_path) ? "(根)" : _path, EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string newName = EditorGUILayout.TextField(new GUIContent("字段名", "留空 = 由节点名推导"), entry.FieldName);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(data, "改 UI 绑定字段名");
                entry.FieldName = newName;
                MarkDirty(data);
            }

            EditorGUILayout.LabelField("绑定组件（勾选 = 各生成一个字段）：");
            if (_node != null)
            {
                foreach (var comp in _node.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string id = UIBindingUtil.TypeId(comp.GetType());
                    bool on = entry.ComponentTypes.Contains(id);
                    bool now = EditorGUILayout.ToggleLeft(comp.GetType().Name, on);
                    if (now != on)
                    {
                        Undo.RecordObject(data, "改 UI 绑定组件");
                        if (now) entry.ComponentTypes.Add(id);
                        else entry.ComponentTypes.Remove(id);
                        MarkDirty(data);
                    }
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("解绑"))
            {
                Undo.RecordObject(data, "解绑 UI 节点");
                data.RemovePath(_path);
                MarkDirty(data);
                editorWindow.Close();
            }
            if (GUILayout.Button("生成代码"))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorUtility.DisplayDialog("UI 绑定", "Play 模式下不能生成代码（会触发重编译），先停止运行。", "好");
                else
                    UIBindingCodeGenerator.GenerateAndLog(_assetPath, data, UICodeGenProfile.Resolve());
                editorWindow.Close();
            }
            EditorGUILayout.EndHorizontal();
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
