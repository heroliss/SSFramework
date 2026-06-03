using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·Container：注册（RegisterValue / RegisterFactory）+ [Inject] + 精确类型键解析。
    /// 解析顺序 / 父子回退留给「多 Context」章，避免重复。
    /// </summary>
    public sealed class ContainerDemoModule : DemoModuleBase
    {
        public override string Id => "container";
        public override string Title => "依赖注入 · Container";
        public override string Category => "核心";
        public override int Order => 40;
        public override string Summary =>
            "依赖注入容器：RegisterValue（给现成实例）/ RegisterFactory（懒构造、缓存为单例）注册，GetXxx 解析；class Command 可 [Inject] 字段。按精确类型键查找。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterFactory(c => new LazyService(), typeof(ILazyService));

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("演示：RegisterFactory 懒构造 + 单例");
            var countLabel = host.AddValueDisplay();
            countLabel.text = "点「使用服务」试试（服务是懒构造：注册了，用到才建）";
            host.AddActionRow("使用服务", () =>
            {
                var n = this.ExecuteCommand(new UseLazyServiceCommand());
                countLabel.text = $"服务已构造 {n} 次——首次用才构造，之后复用同一实例（单例）";
            }, CodeRef.Here("class LazyService", "LazyService"));
            host.AddNote("RegisterFactory 给的是工厂：首次 Resolve 才调用、结果缓存为单例。所以点几次都只构造一次，"
                + "适合“用到才建”的重对象（音频 / 网络 / 配置）。", CodeRef.Here("InstallBindings", "注册代码"));

            host.AddSectionTitle("拿依赖的两种方式：[Inject] vs ctx");
            var injectLabel = host.AddValueDisplay("点下面按钮：class Command 用 [Inject] 拿同一个服务");
            host.AddActionRow("class Command + [Inject]", () =>
            {
                var n = this.ExecuteCommand(new InjectServiceCommand());
                injectLabel.text = $"[Inject] 注入的服务：已构造 {n} 次（与「使用服务」是同一个单例）";
            }, CodeRef.Here("class InjectServiceCommand", "InjectServiceCommand"));
            host.AddNote("class Command 把依赖声明成 [Inject] 字段，CommandSystem 在 Execute 前自动注入；前面「使用服务」是 struct Command，不能 [Inject]（反射只会写到装箱副本），改用 ctx.GetUtility 实时解析——两者拿到的是同一个单例。");
            host.AddNote("能 [Inject] 的：class Command + System / Model / Utility 各层（Mono 在 Awake 注入、纯 C# 经绑定注入）；唯独 struct Command 不行。另外禁止 [Inject] GameContext / IGameContext（拿到完整 Context 会绕过权限接口，框架黑名单报错）。");

            host.AddSectionTitle("注册与注入");
            host.AddConcept("RegisterValue", "直接给现成实例——前面各章注册 Model 用的就是它。");
            host.AddConcept("RegisterFactory", "给工厂，首次 Resolve 才构造、缓存为单例（也可 Eager 在 Build 时立即构造）。");
            host.AddConcept("[Inject] 字段", "class Command / 层 可用 [Inject] 字段拿依赖（执行 / Awake 前注入）；struct Command 不行（反射改的是装箱副本），只能 ctx.GetXxx。");
            host.AddNote("Container 按精确类型键解析、不做继承扫描——注册成什么类型，就用那个类型取。"
                + "解析顺序与父子 Context 回退见「多 Context · 作用域树」一章。");
#if UNITY_EDITOR
            host.AddActionRow("选中 demo 根 Context（DemoApp）", () =>
            {
                var ctx = Object.FindFirstObjectByType<MonoDemoContext>();
                if (ctx != null) { UnityEditor.Selection.activeObject = ctx.gameObject; UnityEditor.EditorGUIUtility.PingObject(ctx.gameObject); }
            });
            host.AddNote("各章纯 C# 的 Model / Service 都注册在这个 Context 的容器里——它们是运行时对象，Inspector 看不到。想 Inspector 可视化就走 Mono 路径（见「Model · 状态与 Inspector」）。");
#endif
        }
    }

    /// <summary>懒构造演示服务：构造时累计计数，用来证明“首次用才建、之后复用同一实例”。</summary>
    public interface ILazyService : IUtility
    {
        int ConstructCount { get; }
    }

    /// <summary>ILazyService 实现：构造一次 _total +1。由 RegisterFactory 懒构造、缓存为单例。</summary>
    public sealed class LazyService : ILazyService
    {
        private static int _total;
        public LazyService() => _total++;   // 首次 Resolve 才会走到这里（懒），之后复用缓存实例
        public int ConstructCount => _total;
    }

    /// <summary>使用服务（struct Command）：不能 [Inject]（装箱），用 ctx.GetUtility 实时解析（首次即构造），返回累计构造次数。</summary>
    public readonly struct UseLazyServiceCommand : ICommand<int>
    {
        public int Execute(ICommandContext ctx) => ctx.GetUtility<ILazyService>().ConstructCount;
    }

    /// <summary>使用服务（class Command）：依赖声明成 [Inject] 字段，CommandSystem 在 Execute 前自动注入；struct 不能这样。</summary>
    public sealed class InjectServiceCommand : ICommand<int>
    {
        [Inject] private ILazyService _service;   // class Command 专属：执行前由框架按字段类型从容器注入
        public int Execute(ICommandContext ctx) => _service.ConstructCount;
    }
}
