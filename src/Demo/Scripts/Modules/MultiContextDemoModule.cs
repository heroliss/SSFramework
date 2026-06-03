using Game.Framework.Demo.Core;
using Game.Framework.Model;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·多 Context：作用域树 + 解析回退。用 DemoRoot 下真实嵌套的两级 Context 节点演示——
    /// 子作用域解析不到就回退父级；子级注册了同类型则覆盖。
    /// </summary>
    public sealed class MultiContextDemoModule : DemoModuleBase
    {
        public override string Id => "multi-context";
        public override string Title => "多 Context · 作用域树";
        public override string Category => "核心";
        public override int Order => 45;
        public override string Summary =>
            "GameContext 是一棵作用域树（全局 / 场景 / 局部）。子 Context 解析不到就回退父级；子级注册了同类型则用子级的（覆盖）。本章用场景里真实嵌套的两级 Context 节点演示。";

        public override void Build(DemoModuleHost host)
        {
            var parent = Object.FindFirstObjectByType<DemoScopeParent>();
            var child = Object.FindFirstObjectByType<DemoScopeChild>();
            if (parent == null || child == null)
            {
                host.AddNote("没找到作用域节点——请确认 DemoRoot 下有 DemoScopeParent，且其子节点有 DemoScopeChild。");
                return;
            }

            host.AddSectionTitle("演示：覆盖 + 回退（真实场景节点）");
            host.AddValueDisplay($"子作用域解析 ScopedTag → 「{child.GetModel<ScopedTag>().Text}」");
            host.AddNote("子作用域自己注册了 ScopedTag → 用子级的（覆盖父级）。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoScopeChild.cs", "class DemoScopeChild", "DemoScopeChild"));
            host.AddValueDisplay($"子作用域解析 ParentOnlyTag → 「{child.GetModel<ParentOnlyTag>().Text}」");
            host.AddNote("子作用域没注册 ParentOnlyTag → 逐级回退，命中父级。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoScopeParent.cs", "class DemoScopeParent", "DemoScopeParent"));
            host.AddValueDisplay($"父作用域解析 ScopedTag → 「{parent.GetModel<ScopedTag>().Text}」");
            host.AddNote("回退是单向的（子 → 父）：父级各自独立，看不到子级注册的东西。");

#if UNITY_EDITOR
            host.AddActionRow("选中 父作用域 到 Inspector", () => SelectInInspector(parent));
            host.AddActionRow("选中 子作用域 到 Inspector", () => SelectInInspector(child));
#endif
            host.AddTip("这两个作用域是 DemoRoot 下真实的 MonoGameContextBase 节点（父 → 子嵌套）。点上面按钮到 Hierarchy 看父子层级——"
                + "MonoXxxBase 子节点按 Transform 父链找最近的 Context 注册，子级解析不到就沿父链回退。");

            host.AddSectionTitle("作用域树");
            host.AddConcept("分层", "全局（跨场景：配置 / 音频）→ 场景（本场景）→ 局部（一个面板 / 关卡）各成一层。");
            host.AddConcept("解析顺序", "本层运行时覆盖 → 本层 InstallBindings → 父级递归 → 全局 Main（inheritFromGlobal 时）。");
            host.AddConcept("覆盖 vs 回退", "子层注册同类型 → 用子层（覆盖）；子层没有 → 逐级回退父层。");
            host.AddTip("好处：切场景 / 关卡时整层 Context 一并 Dispose，临时注册随之清掉、不污染全局——这也是不在运行时单独热替换某个层的原因。");
        }

#if UNITY_EDITOR
        // 编辑器便利：选中并高亮场景里的作用域节点，方便去 Hierarchy 看父子层级。非框架用法，纯 demo 导航。
        private static void SelectInInspector(MonoBehaviour target)
        {
            UnityEditor.Selection.activeObject = target.gameObject;
            UnityEditor.EditorGUIUtility.PingObject(target.gameObject);
        }
#endif
    }

    /// <summary>演示用标签 Model：父子作用域都注册（子覆盖父）。</summary>
    public sealed class ScopedTag : IModel
    {
        public readonly string Text;
        public ScopedTag(string text) => Text = text;
    }

    /// <summary>演示用标签 Model：只在父作用域注册（演示子级回退）。</summary>
    public sealed class ParentOnlyTag : IModel
    {
        public readonly string Text;
        public ParentOnlyTag(string text) => Text = text;
    }
}
