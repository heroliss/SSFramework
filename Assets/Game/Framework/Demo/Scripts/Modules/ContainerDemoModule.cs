using System;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·Container：层感知值注册 / 工厂 / owned 注册 + [Inject] + 精确类型键解析。
    /// 解析顺序 / 父子回退留给「多 Context」章，避免重复。
    /// </summary>
    public sealed class ContainerDemoModule : DemoModuleBase
    {
        public override string Id => "container";
        public override string Title => "依赖注入 · Container";
        public override string Category => "核心";
        public override int Order => 45;   // 排在「多 Context」后：先看作用域树的使用观感，再进注册/注入机制细节
        public override string Summary =>
            "依赖注入容器：普通分层对象自动推导契约，非分层对象显式列契约；工厂可懒构造并缓存为单例，IDisposable 产物用 OwnedFactory 把所有权交给 Context。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterOwnedFactory(c => new LazyService(), typeof(ILazyService));

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("注册进容器、按类型解析、自动注入依赖");
            host.AddNote("容器管「谁提供 / 谁需要」某个类型：注册现成实例或工厂 → 按**精确类型键**解析 → 消费方经 `[Inject]` 字段或 `GetXxx` 拿到。本章还区分两件常被混在一起的事：**怎么构造**（Value / Factory）与**谁负责释放**（Owned）。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：OwnedFactory 懒构造 + 单例复用 + 随 Context 释放");
            var countLabel = host.AddValueDisplay();
            countLabel.text = "点「使用服务」试试（服务是懒构造：注册了，用到才建）";
            host.AddActionRow("使用服务", () =>
            {
                var n = this.ExecuteCommand(new UseLazyServiceCommand());
                countLabel.text = $"服务已构造 {n} 次——首次用才构造，之后复用同一实例（单例）";
            }, CodeRef.Here("class LazyService", "LazyService"));
            host.AddSubNote("这里用 `RegisterOwnedFactory`：首次 Resolve 才调用、结果缓存为单例；因为产物实现 `IDisposable`，根 Context 结束时还会自动 Dispose。普通 `RegisterFactory` 只管构造与缓存，**不接管产物所有权**。",
                CodeRef.Here("InstallBindings", "注册代码"));

            // ── 拿依赖 ──
            host.AddSectionTitle("拿依赖的几种方式：[Inject] / ctx / this.GetXxx");
            var injectLabel = host.AddValueDisplay("点下面按钮：class Command 用 [Inject] 拿同一个服务");
            host.AddActionRow("class Command + [Inject]", () =>
            {
                var n = this.ExecuteCommand(new InjectServiceCommand());
                injectLabel.text = $"[Inject] 注入的服务：已构造 {n} 次（与「使用服务」是同一个单例）";
            }, CodeRef.Here("class InjectServiceCommand", "InjectServiceCommand"));
            host.AddNote("class Command 把依赖声明成 `[Inject]` 字段，默认命令分发器 `CommandSystem` 在 Execute 前自动注入；上面「使用服务」是 struct Command，不能 `[Inject]`（反射只写到装箱副本），改用 `ctx.GetUtility` 实时解析——两者拿到同一个单例。");
            host.AddConcept("[Inject] 字段", "class Command 与三层（Model / System / Utility）可用：Mono 在 `Awake`、纯 C# 在绑定时注入一次、快照到字段。struct Command 不能用。");
            host.AddConcept("this.GetXxx / ctx.GetXxx", "层里也能 `this.GetModel/System/Utility<T>()` 实时解析；struct Command 只能 `ctx.GetXxx`。View 没有 GetModel/GetSystem 权限，只能经 Command 间接拿。");
            host.AddSubNote("权限对 `[Inject]` 一视同仁：注入目标按宿主层的权限闸门校验（与 `this.GetXxx` 同源）——View 注 Model/System、Model 注 System 等越权在注入期 `LogError` 拦下，不是绕权限的后门。Command 例外（经 `ctx` 有完整层访问权）；`GameContext`/`IGameContext` 始终禁注（万能门会绕过权限接口）。");

            host.AddSectionTitle("注册与注入");
            host.AddConcept("RegisterModel/System/Utility", "普通分层对象的默认入口：直接给现成实例，自动登记具体类型与该层的 Interface；实例生命周期仍由外部持有。");
            host.AddConcept("RegisterValue", "低层精确接线：只登记显式列出的 contract，留给 `ICommandSystem` 等非分层基础设施、选择性暴露和生成代码。");
            host.AddConcept("RegisterFactory", "给工厂，首次 Resolve 才构造、缓存为单例（也可 Eager）；容器不负责 Dispose 产物，适合普通对象或所有权明确在外部的对象。");
            host.AddConcept("RegisterOwnedModel/System/Utility", "普通分层对象的 owned 入口：自动推导契约，并把 `IDisposable` 实例交给 Context 逆序释放（如 `PoolUtility`）。");
            host.AddConcept("RegisterOwned", "低层 owned 接线：生命周期同样交给 Context，但 contract 必须显式列出；适合非分层服务或刻意限制解析面。");
            host.AddConcept("RegisterOwnedFactory", "依赖要从 Container 现取、又必须随 Context 释放时用：懒/Eager 构造 + Singleton 缓存 + owned Dispose 一次完成。工厂产物仍由工厂显式接线，不自动 `[Inject]`。");
            host.AddConcept("[Inject] / this.GetXxx", "层与 class Command 可用 `[Inject]` 字段拿依赖（执行 / `Awake` 前注入、快照）；层里也可改用 `this.GetXxx<T>()` 实时解析。struct Command 不能 `[Inject]`（反射改的是装箱副本），只能 `ctx.GetXxx`。");
            host.AddNote("Container 按精确类型键解析、不做继承扫描——注册成什么类型，就用那个类型取。"
                + "解析顺序与父子 `Context` 回退见「多 Context · 作用域树」一章。");
#if UNITY_EDITOR
            host.AddActionRow("选中 demo 根 Context 节点", () =>
            {
                var ctx = UnityEngine.Object.FindFirstObjectByType<MonoDemoContext>();
                if (ctx != null) DemoEditorNav.PingSceneObject(ctx.gameObject);
            }, new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "class MonoDemoContext", "demo 根 Context 定义"));
            host.AddNote("各章纯 C# 的 Model / Service 都注册在这个 `Context` 的容器里——它们是运行时对象，Inspector 看不到。想 Inspector 可视化就走 Mono 路径（见「Model · 状态与 Inspector」）；想直接翻这个容器的注册表（契约 → 实例），开诊断窗口选中它即可（见「框架诊断面板」章）。");
#endif
        }
    }

    /// <summary>懒构造演示服务：构造时累计计数，用来证明“首次用才建、之后复用同一实例”。</summary>
    public interface ILazyService : IUtility
    {
        int ConstructCount { get; }
    }

    /// <summary>ILazyService 实现：由 OwnedFactory 懒构造、缓存为单例，并随 Context 释放。</summary>
    public sealed class LazyService : ILazyService, IDisposable
    {
        private static int _total;
        public LazyService() => _total++;   // 首次 Resolve 才会走到这里（懒），之后复用缓存实例
        public int ConstructCount => _total;
        public void Dispose() { }           // 实际服务在这里释放订阅/句柄；空实现只用于演示所有权契约
    }

    /// <summary>使用服务（struct Command）：不能 [Inject]（装箱），用 ctx.GetUtility 实时解析（首次即构造），返回累计构造次数。</summary>
    public readonly struct UseLazyServiceCommand : ICommand<int>
    {
        public int Execute(ICommandContext ctx) => ctx.GetUtility<ILazyService>().ConstructCount;
    }

    /// <summary>使用服务（class Command）：依赖声明成 [Inject] 字段，命令分发器在 Execute 前自动注入；struct 不能这样。</summary>
    public sealed class InjectServiceCommand : ICommand<int>
    {
        [Inject] private ILazyService _service;   // class Command 专属：执行前由框架按字段类型从容器注入
        public int Execute(ICommandContext ctx) => _service.ConstructCount;
    }
}
