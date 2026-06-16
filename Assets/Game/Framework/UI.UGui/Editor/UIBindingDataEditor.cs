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

            UIBindingEntry remove = null;
            using (new EditorGUI.DisabledScope(!editable))
            {
                foreach (var entry in data.Entries)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(string.IsNullOrEmpty(entry.Path) ? "(根)" : entry.Path, EditorStyles.miniBoldLabel);
                    if (editable && GUILayout.Button("解绑", GUILayout.Width(48))) remove = entry;
                    EditorGUILayout.EndHorizontal();

                    EditorGUI.BeginChangeCheck();
                    string fieldName = EditorGUILayout.TextField(
                        new GUIContent("字段名", "留空 = 由节点名推导；多组件时各自加组件名后缀"), entry.FieldName);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(data, "改 UI 绑定字段名");
                        entry.FieldName = fieldName;
                        MarkDirty(data, stage);
                    }

                    var comps = new List<string>();
                    foreach (var id in entry.ComponentTypes)
                    {
                        var type = UIBindingUtil.ResolveType(id);
                        comps.Add(type != null ? type.Name : id + "（未解析）");
                    }
                    EditorGUILayout.LabelField("组件", comps.Count > 0 ? string.Join(", ", comps) : "（无——请在 Hierarchy 徽标里勾选）");

                    EditorGUILayout.EndVertical();
                }
            }

            if (remove != null)
            {
                Undo.RecordObject(data, "解绑 UI 节点");
                data.Entries.Remove(remove);
                MarkDirty(data, stage);
            }

            EditorGUILayout.Space();
            string assetPath = editable ? stage.assetPath : AssetDatabase.GetAssetPath(data);
            if (string.IsNullOrEmpty(assetPath))
                EditorGUILayout.HelpBox("拿不到 prefab 路径（请从 Project 选中 prefab 或进编辑模式）。", MessageType.None);
            else
                UIBindingGenGUI.Draw(data, assetPath, editable);
        }

        private static void MarkDirty(UIBindingData data, PrefabStage stage)
        {
            EditorUtility.SetDirty(data);
            if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
            EditorApplication.RepaintHierarchyWindow();
        }
    }
}
