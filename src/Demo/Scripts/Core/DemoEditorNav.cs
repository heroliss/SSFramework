#if UNITY_EDITOR
using Game.Framework.Context;
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

        /// <summary>
        /// 在指定 Context 的 Hierarchy 子树中查找由它直接拥有的组件；嵌套 Context 下的同类型组件会被排除。
        /// 这让 Inspector 导航与运行时“沿父链取最近 Context”的注册语义保持一致，避免多 Context 场景里选中另一份同类型状态。
        /// </summary>
        public static T FindComponentOwnedBy<T>(MonoGameContextBase context) where T : Component
        {
            if (context == null) return null;

            foreach (var candidate in context.GetComponentsInChildren<T>(true))
            {
                if (candidate.GetComponentInParent<MonoGameContextBase>(true) == context)
                    return candidate;
            }

            return null;
        }

        /// <summary>按工程资源路径选中并 ping 一个资产（Project 窗口高亮）。找不到则告警。</summary>
        public static void PingAsset(string assetPath)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
            else Debug.LogWarning("[Demo] 没找到资产：" + assetPath);
        }

        /// <summary>
        /// 打开一个由 Framework Editor Module 注册的菜单入口。章节只声明目的地，失败反馈集中在这里，
        /// 避免菜单改名或可选 Module 被移除后，按钮静默无反应或各章给出互相矛盾的提示。
        /// </summary>
        /// <returns>菜单存在且已被 Unity 接受执行时返回 <c>true</c>。</returns>
        public static bool OpenMenu(string menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                Debug.LogWarning("[Demo] 无法打开 Editor 工具：菜单路径为空。");
                return false;
            }

            if (EditorApplication.ExecuteMenuItem(menuPath)) return true;

            Debug.LogWarning(
                $"[Demo] 无法打开 Editor 工具：菜单不存在或当前不可用。\n" +
                $"路径：{menuPath}\n" +
                "请确认对应 Framework Editor Module 已安装，或从 SSFramework/工具中心查找新的入口。");
            return false;
        }
    }
}
#endif
