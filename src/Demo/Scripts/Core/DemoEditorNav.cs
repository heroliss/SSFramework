#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// demo 导航小工具：在 Hierarchy / Project 里选中并高亮一个场景节点或工程资产，实现各章「选中 / 定位」按钮
    /// 「点一下 → 跳到对应对象」的导览效果。纯 demo 编辑器便利（不是框架用法），各模块共用，避免每个模块各抄一份。
    /// </summary>
    internal static class DemoEditorNav
    {
        /// <summary>选中并 ping 一个场景 GameObject（Hierarchy 高亮 + Inspector 显示）。<c>null</c> 安全。</summary>
        public static void PingSceneObject(GameObject go)
        {
            if (go == null) return;
            Selection.activeObject = go;
            EditorGUIUtility.PingObject(go);
        }

        /// <summary>按工程资源路径选中并 ping 一个资产（Project 窗口高亮）。找不到则告警。</summary>
        public static void PingAsset(string assetPath)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
            else Debug.LogWarning("[Demo] 没找到资产：" + assetPath);
        }
    }
}
#endif
