using Game.Framework.Demo.Core;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 入门第一站：在钻进任何具体功能前，先把框架"整体怎么运转"讲清楚——五层职责、单向数据流、
    /// 编译期权限、依赖注入与统一生命周期。纯讲解页（无交互按钮），但附带跳进框架真实源码的链接。
    /// </summary>
    /// <remarks>
    /// 它仍然继承 <see cref="DemoModuleBase"/>、扮演"View 角色"，只是没发任何 Command——
    /// 用来示范"一个最简单的、只读不写的章节"长什么样。后面的章节在此基础上逐步加入交互。
    /// </remarks>
    public sealed class OverviewModule : DemoModuleBase
    {
        public override string Id => "overview";
        public override string Title => "框架总览";
        public override string Category => "入门";
        public override int Order => 0;
        public override DemoTeachingKind TeachingKind => DemoTeachingKind.Concept;
        public override string Summary =>
            "先建立整体认知再看具体功能就不会懵：MVCS 五层各管什么、写操作为什么只能单向流动、" +
            "“谁能调用谁”如何在编译期就被钉死，以及依赖注入和统一生命周期带来了什么。";

        public override void Build(DemoModuleHost host)
        {
            host.AddPositioning("先建立全局地图，再进入具体能力");
            host.AddNote("SSFramework 是一套面向 Unity 的游戏框架：用分层、受限接口和统一生命周期，让依赖方向清楚、状态变化可追踪。",
                new CodeRef("Assets/Game/Framework/Core/Internal/IGameContext.cs", "interface IGameContext", "IGameContext · 能力面"));
            host.AddSubNote("它不追求替你决定所有业务结构，而是把常见边界做成低摩擦默认值；小项目会多一点类型和 Command，大项目则换来更稳定的导航、测试与自动化接缝。");
            host.AddNote("这个 demo 本身就是用框架写的：每个章节（包括本页）都扮演框架里的“View 角色”在调用真实 API——" +
                         "所以你看到的演示就是真实手感。动作旁的“查看源码”按钮能直接跳进对应的框架 / 示例代码。");

            host.AddSectionTitle("MVCS 五层职责");
            host.AddNote("MVCS 由数据层（Model）、界面层（View）、意图层（Command）和逻辑层（System）的英文首字母组成。Event 与 Model 同属数据层，Utility 则提供跨层复用的基础设施能力。");
            host.AddConcept("界面层（View）", "只负责“发送 Command 表达意图”与“订阅只读状态刷新 UI”；不直接读写 Model、不发 Event——UI 永远是状态的投影。");
            host.AddConcept("意图层（Command）", "把“要做什么”封装成一次操作，是唯一能写 Model、能编排 System 的接缝。默认用 `readonly struct`（零分配），依赖多时才用 class。");
            host.AddConcept("逻辑层（System）", "承载玩法 / 业务规则，被 Command 调用；自身不发 Command，避免出现 System 调自己的回环。");
            host.AddConcept("数据层（Model）", "持有响应式状态（值一变就自动通知订阅者）。配套的 Event 总线做一对多广播——Model 与 Event 都是“被动数据”，只被读取 / 订阅，不主动调别人。");
            host.AddConcept("基础设施层（Utility）", "承载与具体玩法无关的共享能力：既可以是无状态纯函数，也可以是资源、对象池、存储等有生命周期的服务；可持有基础设施状态，但不持有金币、背包等业务状态。");
            host.AddNote("想看层的契约可以直接跳源码：");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Core/Command/ICommand.cs",
                "interface ICommand", "ICommand · 命令定义"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Core/Model/IModel.cs",
                "interface IModel", "IModel · Model 标记"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Core/View/MonoViewBase.cs",
                "class MonoViewBase", "MonoViewBase · View 基类（业务继承点）"));

            host.AddSectionTitle("一眼判断：代码应该放在哪一层");
            host.AddTable(
                new[] { "你正在表达…", "首选层", "判断线索" },
                new[] { "画面与输入", "界面层（`View`）", "只投影状态、发意图，不做业务判断" },
                new[] { "一次请求或查询", "意图层（`Command`）", "让外部动作经过统一、可审计的接缝" },
                new[] { "可复用的业务规则", "逻辑层（`System`）", "多步、跨 Model，或会被多个 Command 调用" },
                new[] { "持续存在的当前值", "数据层（`Model`）", "现在仍能读到，例如金币、HP、进度" },
                new[] { "已经发生的瞬时事实", "数据层（`Event`）", "只通知，不承担请求与返回值" },
                new[] { "与玩法无关的共享能力", "基础设施层（`Utility`）", "可替换的基础设施或纯函数，不反向读业务层" });
            host.AddTip("Command 会增加少量样板，但换来统一的日志、测试、回放与权限边界。简单原子写入可以由 Command 直接改 Model；只有规则需要复用、组合或独立演进时才引入 System，别为分层而分层。");

            host.AddSectionTitle("单向数据流");
            host.AddNote("写操作永远朝一个方向：View → Command → Model，复杂规则则由 Command 委托给 System 再写 Model。" +
                         "View 想改状态只能发 Command；Command 在 `ICommandContext` 里拿 Model / System 干活；" +
                         "Model 变化再通过只读订阅“反向”推回 View 刷新界面。");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Core/Command/ICommandContext.cs",
                "interface ICommandContext", "ICommandContext · Command 访问层的入口"));
            host.AddTip("约定：View 不直接读 Model，连读状态也经“只读查询 Command”拿回 ReadOnlyReactiveProperty<T>。" +
                        "这样 UI 始终是状态的投影，不会出现 UI 偷偷改数据、两份真相打架。下一章“计数器”就是这个闭环的最小例子。");

            host.AddSectionTitle("技术特点");
            host.AddNote("下面几点是相互独立的设计，不是一个大功能——按需各看各的。这里每点只一句，细节在框架文档与后续章节：");
            host.AddConcept("编译期权限", "用一组空标记接口，让越权写法直接编译不过（如 View 写不出 `GetModel`）。它不是万能锁——反射等仍能绕过——目的是减少人为出错、降低心智负担，顺手提供便利。");
            host.AddConcept("依赖注入", "一个轻量 DI 容器：按类型注册 / 解析依赖，`[Inject]` 字段自动装配。常规的小能力。");
            host.AddConcept("多 Context", "`GameContext` 是一棵作用域树，子 Context 解析不到就回退父级——可分出全局 / 场景 / 局部等不同生命周期的作用域。");
            host.AddConcept("Mono 自动绑定", "继承 `MonoXxxBase` 的组件在 Awake 自动找到所属 Context 并完成注册 / 注入，免去手动接线；纯 C# 对象走显式绑定。");
            host.AddConcept("统一生命周期", "`DisposableBag` 把订阅 / 资源句柄 / 对象池租借 / 子作用域都登记起来，宿主销毁时批量释放，不用手写一堆退订。");
            host.AddConcept("树状组织", "上面「多 Context / Mono 自动绑定 / 统一生命周期」三条共享同一个理念：结构性问题都用「树」回答——"
                + "Context 嵌套成作用域树（解析回退）、Hierarchy 决定注册去向（就近向上）、Bag 子作用域级联释放。"
                + "节点放进哪个子树 = 一次说清「依赖从哪来、注册到哪去、何时被清理」。");
            host.AddNote("想看源码（其余见各自章节）：");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Core/Internal/ICanSendCommand.cs",
                "interface ICanSendCommand", "编译期权限接口"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Core/Internal/IGameContext.cs",
                "interface IGameContext", "IGameContext · 能力面"));
            // 只链公共契约（接口 + 业务继承点）；GameContext / DisposableBag 等底层实现不在这里给跳转——
            // 它们的“怎么用”分别在「多 Context」「生命周期」章里以 demo 用法行呈现。

            host.AddSectionTitle("怎么逛这个 demo");
            host.AddNote("左侧导航把章节分成四组、由简入深——「核心」建议按顺序通读，「能力」/「进阶」各自独立、挑感兴趣的看：");
            host.AddConcept("入门", "本页总览 + 「最小闭环」计数器 + 「接入你的项目」：先把 View →(Command)→ Model 这一圈单向数据流亲手跑通，再看怎么把骨架搬进自己的项目。");
            host.AddConcept("核心", "MVCS 骨架与依赖注入——Model 状态、Command 三态、System 逻辑、Event、View（UGUI / UIToolkit 两种载体）、多 Context 作用域树、容器与生命周期，最后用「框架诊断面板」把这些运行时状态看个透。");
            host.AddConcept("能力", "建在核心之上、彼此独立的横切功能：对象池、资源加载、本地存储、音频、游戏流程、本地化 / 字体、UI 框架、响应式列表、网络——按需取用。");
            host.AddConcept("进阶", "底层原理与「可替换后端」思路：R3 操作符、YooAsset 底层、资源运营链路、热更、配置表、DOTS 融合。");
            host.AddTip("每个“查看源码”按钮都能跳进真实代码：演示自身的代码用 CodeRef.Here() 指向本文件，" +
                        "跳框架源码则用显式路径。点点看，能直接落到对应的类 / 方法定义那一行。");
        }
    }
}
