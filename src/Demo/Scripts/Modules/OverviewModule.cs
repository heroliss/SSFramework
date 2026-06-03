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
        public override string Summary =>
            "先建立整体认知再看具体功能就不会懵：MVCS 五层各管什么、写操作为什么只能单向流动、" +
            "“谁能调用谁”如何在编译期就被钉死，以及依赖注入和统一生命周期带来了什么。";

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("这个框架是什么");
            host.AddNote("SSFramework 是一套面向 Unity 的游戏框架，设计目标：结构清晰、对人类可读、对 AI 友好。" +
                         "它不求大而全，而是把“分层 + 单向数据流”的骨架做扎实，让正确的写法成为最省力的写法。");
            host.AddNote("这个 demo 本身就是用框架写的：每个章节（包括本页）都扮演框架里的“View 角色”在调用真实 API——" +
                         "所以你看到的演示就是真实手感。动作旁的“查看源码”按钮能直接跳进对应的框架 / 示例代码。");

            host.AddSectionTitle("MVCS 五层职责");
            host.AddNote("MVCS 取自四个主层的首字母：Model / View / Command / System。Event 与 Model 同属“数据层”" +
                         "（都是被动数据、只被读取或订阅），Utility 则是横切各层的通用工具。逐层来看：");
            host.AddConcept("View", "界面层。只能“发 Command 表达意图”+“只读订阅状态刷新 UI”；不直接读写 Model、不发 Event——UI 永远是状态的投影。");
            host.AddConcept("Command", "意图层。把“要做什么”封装成一次操作，是唯一能写 Model、能编排 System 的接缝。默认用 readonly struct（零分配），依赖多时才用 class。");
            host.AddConcept("System", "逻辑层。承载玩法 / 业务规则，被 Command 调用；自身不发 Command，避免出现 System 调自己的回环。");
            host.AddConcept("Model", "数据层。持有响应式状态 RP<T>，变化经 R3 推给订阅者。配套的 Event 总线做一对多广播——Model 与 Event 都是“被动数据”，只被读取 / 订阅，不主动调别人。");
            host.AddConcept("Utility", "工具层。与玩法无关的通用能力（资源加载、对象池、存储等），各层都能取用。");
            host.AddNote("想看层的契约可以直接跳源码：");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Scripts/Command/ICommand.cs",
                "interface ICommand", "ICommand · 命令定义"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Scripts/Model/IModel.cs",
                "interface IModel", "IModel · Model 标记"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Scripts/View/MonoViewBase.cs",
                "class MonoViewBase", "MonoViewBase · View 基类（业务继承点）"));

            host.AddSectionTitle("单向数据流");
            host.AddNote("写操作永远朝一个方向：View →(Command)→ Model / System。" +
                         "View 想改状态只能发 Command；Command 在 ICommandContext 里拿 Model / System 干活；" +
                         "Model 变化再通过只读订阅“反向”推回 View 刷新界面。");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Scripts/Command/ICommandContext.cs",
                "interface ICommandContext", "ICommandContext · Command 访问层的入口"));
            host.AddTip("约定：View 不直接读 Model，连读状态也经“只读查询 Command”拿回 ReadOnlyReactiveProperty<T>。" +
                        "这样 UI 始终是状态的投影，不会出现 UI 偷偷改数据、两份真相打架。下一章“计数器”就是这个闭环的最小例子。");

            host.AddSectionTitle("技术特点");
            host.AddNote("下面几点是相互独立的设计，不是一个大功能——按需各看各的。这里每点只一句，细节在框架文档与后续章节：");
            host.AddConcept("编译期权限", "用一组空标记接口，让越权写法直接编译不过（如 View 写不出 GetModel）。它不是万能锁——反射等仍能绕过——目的是减少人为出错、降低心智负担，顺手提供便利。");
            host.AddConcept("依赖注入", "一个轻量 DI 容器：按类型注册 / 解析依赖，[Inject] 字段自动装配。常规的小能力。");
            host.AddConcept("多 Context", "GameContext 是一棵作用域树，子 Context 解析不到就回退父级——可分出全局 / 场景 / 局部等不同生命周期的作用域。");
            host.AddConcept("Mono 自动绑定", "继承 MonoXxxBase 的组件在 Awake 自动找到所属 Context 并完成注册 / 注入，免去手动接线；纯 C# 对象走显式绑定。");
            host.AddConcept("统一生命周期", "DisposableBag 把订阅 / 资源句柄 / 对象池租借 / 子作用域都登记起来，宿主销毁时批量释放，不用手写一堆退订。");
            host.AddNote("想看源码（其余见各自章节）：");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Scripts/Internal/ICanSendCommand.cs",
                "interface ICanSendCommand", "编译期权限接口"));
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Scripts/Internal/IGameContext.cs",
                "interface IGameContext", "IGameContext · 能力面"));
            // 只链公共契约（接口 + 业务继承点）；GameContext / DisposableBag 等底层实现不在这里给跳转——
            // 它们的“怎么用”分别在「多 Context」「生命周期」章里以 demo 用法行呈现。

            host.AddSectionTitle("怎么逛这个 demo");
            host.AddNote("左侧章节由简入深：先从“计数器”看最小闭环，再过一遍“核心”（Model + 响应式、Command 三态、Event、依赖注入、生命周期），" +
                         "然后是“能力”（对象池 / 资源加载）与“视图”（UGUI 手感）；最后“规划中”是设计阶段的热更 / 配置表。建议按顺序看。");
            host.AddTip("每个“查看源码”按钮都能跳进真实代码：演示自身的代码用 CodeRef.Here() 指向本文件，" +
                        "跳框架源码则用显式路径。点点看，能直接落到对应的类 / 方法定义那一行。");
        }
    }
}
