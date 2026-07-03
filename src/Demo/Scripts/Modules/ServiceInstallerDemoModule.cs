using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.System;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·服务注册生成：演示"注册样板交给代码生成"——固定目录放纯 C# 服务 → 生成显式安装器（.g.cs）→
    /// Context 里一行接线；配套演示构建期值绑定的"注册即注入"（ADR-0019）。
    /// </summary>
    public sealed class ServiceInstallerDemoModule : DemoModuleBase
    {
        private const string Dir = "Assets/Game/Framework/Demo/Scripts/Modules/ServiceInstaller";

        public override string Id => "service-installer";
        public override string Title => "服务注册生成 · 安装器";
        public override string Category => "核心";
        public override int Order => 46;   // 紧跟「容器」章：先懂注册/解析机制，再看注册样板如何生成掉
        public override string Summary =>
            "纯 C# 服务的 InstallBindings 注册样板可以生成：固定目录放服务类 → 菜单生成一份显式安装器（.g.cs，注册关系 git 可审）→ "
            + "Context 里一行接线。刻意不做运行时反射扫描（启动零反射、AOT/热更友好）；值绑定实例在 Context 构造时自动完成 [Inject] 注入与附着。";

        public override void InstallBindings(ContainerBuilder builder)
        {
            // 一行接线：把生成的安装器装进 demo 根 Context——装进哪个 Context 由调用方决定，生成器不指认。
            Generated.DemoServicesInstaller.Install(builder);

            // opt-out 服务（[ExcludeFromInstaller]）回落手写：构造带参数，工厂就是显式接线位（工厂产物不自动注入）。
            builder.RegisterFactory(
                _ => new Services.DemoExcludedService("构造需要参数"),
                typeof(Services.IDemoExcludedService));
        }

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("演示");
            var greetLabel = host.AddValueDisplay("点「问候」→ 生成安装器注册的 System 返回一句话",
                CodeRef.Here("struct GreetDemoCommand", "GreetDemoCommand"));
            host.AddActionRow("问候（生成注册的 System）",
                () => greetLabel.text = this.ExecuteCommand(new GreetDemoCommand()),
                new CodeRef(Dir + "/Services/DemoGreeterSystem.cs", "class DemoGreeterSystem", "DemoGreeterSystem"));
            host.AddSubNote("问候语里的时间来自它 [Inject] 的时间工具——两个服务都由生成的安装器注册，注入是 Context 构造时自动完成的。");

            var excludedLabel = host.AddValueDisplay("点「问被排除的服务」→ 手写注册的服务也在同一容器",
                CodeRef.Here("struct DescribeExcludedCommand", "DescribeExcludedCommand"));
            host.AddActionRow("问被排除的服务（手写注册）",
                () => excludedLabel.text = this.ExecuteCommand(new DescribeExcludedCommand()),
                new CodeRef(Dir + "/Services/DemoExcludedService.cs", "class DemoExcludedService", "DemoExcludedService"));

            host.AddSectionTitle("生成工作流（三步）");
            host.AddStep("1", "服务类放固定目录，文件名 = 类名——本章的两个服务就在 `ServiceInstaller/Services/`。",
                new CodeRef(Dir + "/Services/DemoTimeUtility.cs", "class DemoTimeUtility", "看服务类"));
            host.AddStep("2", "`ServiceInstallerProfile` 资产配「扫描目录 → 输出路径/命名空间」（本章样板在 `ServiceInstaller/` 下），"
                + "菜单「SSFramework/服务注册/生成服务安装器代码」生成 .g.cs。",
                new CodeRef(Dir + "/Generated/DemoServicesInstaller.g.cs", "public static void Install", "看生成产物"));
            host.AddStep("3", "Context 的 `InstallBindings` 里一行接线调用——测试 Context / 子 Context 想装同一批服务就再调一次。",
                CodeRef.Here("Generated.DemoServicesInstaller.Install", "本模块的接线"));

            host.AddSectionTitle("要点");
            host.AddNote("• **扫描口径**：顶层非抽象 class、实现恰一个层标记（Model/System/Utility）体系、非 Mono、公共无参构造。"
                + "契约推导与 Mono 路径同口径：具体类型 + 层派生接口——所以接口和具体类型都解析得到。");
            host.AddNote("• **注册即注入**：构建期值绑定（RegisterValue/RegisterOwned）的实例在 Context 构造时自动 Inject + AttachTo，"
                + "与 Mono 路径对称——`DemoGreeterSystem` 的 [Inject] 字段没写一行手动注入。工厂产物除外（工厂是显式接线位）。",
                new CodeRef(Dir + "/Services/DemoGreeterSystem.cs", "[Inject] private IDemoTimeUtility", "看 [Inject] 字段"));
            host.AddNote("• **opt-out**：标 `[ExcludeFromInstaller]` 的类生成器跳过（翻生成的 .g.cs 里确实没有 `DemoExcludedService`），"
                + "注册自管——带参构造 / 懒构造 / 契约要特殊裁剪的服务都走这条路。",
                CodeRef.Here("RegisterFactory", "手写接线在这"));
            host.AddNote("• **重复契约**：同一安装器里两个实现撞同一接口契约 → 生成期直接报错列出双方（构建期绑定是静默后覆盖先，生成场景不允许）。");
            host.AddTip("IDisposable 服务会自动用 RegisterOwned 注册（随 Context Dispose 释放）。设计取舍全文见 docs/adr/0019-service-installer-codegen.md，用法见 framework-guide §11。");
        }
    }

    /// <summary>查询：让生成注册的问候 System 打个招呼（一次性返回值，非响应式状态）。</summary>
    public readonly struct GreetDemoCommand : ICommand<string>
    {
        public string Execute(ICommandContext ctx) => ctx.GetSystem<Services.IDemoGreeterSystem>().Greet();
    }

    /// <summary>查询：让被排除（手写注册）的服务自述——证明 opt-out 服务与生成注册的服务在同一容器。</summary>
    public readonly struct DescribeExcludedCommand : ICommand<string>
    {
        public string Execute(ICommandContext ctx) => ctx.GetSystem<Services.IDemoExcludedService>().Describe();
    }
}
