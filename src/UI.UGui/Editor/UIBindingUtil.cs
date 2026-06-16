using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Framework.UI.UGui.Editor
{
    /// <summary>UI 节点绑定工具的辅助：类型 id 互转、节点路径、字段名清洗、默认组件挑选、自定义脚本判定，以及根上 <see cref="UIBindingData"/> 的取/加。</summary>
    public static class UIBindingUtil
    {
        // ───────────── 绑定数据组件（prefab 根上）存取 ─────────────

        /// <summary>取根上的绑定数据组件（无则 null）。</summary>
        public static UIBindingData GetData(GameObject root) => root != null ? root.GetComponent<UIBindingData>() : null;

        /// <summary>取根上的绑定数据组件，没有就经 <c>Undo</c> 加一个（可撤销、并把 Stage 标脏）。</summary>
        public static UIBindingData GetOrAddData(GameObject root)
        {
            var data = root.GetComponent<UIBindingData>();
            if (data == null) data = Undo.AddComponent<UIBindingData>(root);
            return data;
        }

        /// <summary>读 prefab 资产根上的绑定数据组件——给场景实例的只读徽标用（按资产路径加载，Unity 内部缓存）。</summary>
        public static UIBindingData LoadAssetData(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            return go != null ? go.GetComponent<UIBindingData>() : null;
        }

        // ───────────── 生成目标解析（本 prefab 覆盖 > Profile 默认）。生成器与 GUI 共用，避免口径漂移。 ─────────────

        public static string ResolveNamespace(UIBindingData data, UICodeGenProfile profile)
            => string.IsNullOrWhiteSpace(data.NamespaceOverride) ? profile.NamespaceRoot : data.NamespaceOverride.Trim();

        public static string ResolveOutputDir(UIBindingData data, UICodeGenProfile profile)
            => string.IsNullOrWhiteSpace(data.OutputDirOverride) ? profile.OutputCodeDir : data.OutputDirOverride.Trim().TrimEnd('/', '\\');

        public static string ResolveGeneratedDir(UIBindingData data, UICodeGenProfile profile)
            => string.IsNullOrWhiteSpace(data.GeneratedDirOverride) ? profile.GeneratedCodeDir : data.GeneratedDirOverride.Trim().TrimEnd('/', '\\');

        /// <summary>组件类型 → 稳定可解析的 id（<c>FullName, AssemblyName</c>，不含版本/区域/公钥）。</summary>
        public static string TypeId(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;

        /// <summary>id → 类型（编辑器域里类型未加载则返回 null）。</summary>
        public static Type ResolveType(string id) => string.IsNullOrEmpty(id) ? null : Type.GetType(id);

        /// <summary>生成代码里用的类型引用——全限定 + <c>global::</c> 前缀，彻底规避命名空间冲突（含 <c>Game.Framework.System</c> 陷阱）。</summary>
        public static string CSharpTypeName(Type t) => "global::" + t.FullName.Replace('+', '.');

        /// <summary>是否用户自定义脚本（非 Unity/TMPro 内置）——标记节点时这类组件优先级最高。</summary>
        public static bool IsCustomScript(Type t)
        {
            string ns = t.Namespace ?? string.Empty;
            return !(ns == "UnityEngine" || ns.StartsWith("UnityEngine.")
                  || ns == "Unity" || ns.StartsWith("Unity.")
                  || ns == "TMPro" || ns.StartsWith("TMPro.")
                  || ns.StartsWith("UnityEditor"));
        }

        /// <summary>
        /// 标记节点时挑默认组件：① 第一个自定义脚本（最可能是要引用的子 View / 自定义控件）；
        /// ② 否则按内置优先级列表（按 <c>FullName</c> 或简单名匹配）；③ 仍没有则兜底节点自身的 Transform。
        /// </summary>
        public static Component PickDefaultComponent(GameObject node, IReadOnlyList<string> builtinPriority)
        {
            var comps = node.GetComponents<Component>().Where(c => c != null).ToList();

            var custom = comps.FirstOrDefault(c => IsCustomScript(c.GetType()));
            if (custom != null) return custom;

            if (builtinPriority != null)
                foreach (string name in builtinPriority)
                {
                    var hit = comps.FirstOrDefault(c => c.GetType().FullName == name || c.GetType().Name == name);
                    if (hit != null) return hit;
                }

            return node.transform;
        }

        /// <summary>计算 <paramref name="node"/> 相对 <paramref name="root"/> 的路径（root 自身 = 空串）；不在 root 子树下返回 false。</summary>
        public static bool TryGetNodePath(Transform root, Transform node, out string path)
        {
            path = null;
            if (node == root) { path = string.Empty; return true; }

            var segments = new List<string>();
            var t = node;
            while (t != null && t != root)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            if (t != root) return false; // 不是 root 的后代

            segments.Reverse();
            path = string.Join("/", segments);
            return true;
        }

        /// <summary>把任意名字清洗成合法 C# 标识符（非法字符换 <c>_</c>，数字开头补前缀 <c>_</c>）。</summary>
        public static string SanitizeIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "_";
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>字段基名：取路径最后一段（root 自身取 root 名），清成合法标识符。生成器与 Hierarchy 装饰器共用。</summary>
        public static string DeriveBaseName(string path, string rootName)
        {
            if (string.IsNullOrEmpty(path)) return SanitizeIdentifier(rootName);
            int slash = path.LastIndexOf('/');
            return SanitizeIdentifier(slash >= 0 ? path.Substring(slash + 1) : path);
        }

        /// <summary>类型链上是否有名为 <c>MonoViewBase</c> 的基类（框架 View 基类）——名称判定，避开对运行时程序集的硬引用。</summary>
        public static bool IsViewScript(Type t)
        {
            for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                if (b.Name == "MonoViewBase") return true;
            return false;
        }

        /// <summary>
        /// <paramref name="node"/> 是否落在某个「子 View 脚本」内部——即 root 与 node 之间存在带 <c>MonoViewBase</c> 的中间祖先。
        /// 用于树状边界校验：父窗口不该跨进子 View 子树抓孙节点，应改为引用那个子 View 本身。
        /// </summary>
        public static bool IsInsideSubView(Transform root, Transform node, out string ownerName)
        {
            ownerName = null;
            var t = node.parent;
            while (t != null && t != root)
            {
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && IsViewScript(c.GetType())) { ownerName = c.GetType().Name; return true; }
                t = t.parent;
            }
            return false;
        }
    }
}
