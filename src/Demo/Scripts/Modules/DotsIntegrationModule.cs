using Game.Framework.Demo.Core;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·DOTS/ECS 与框架融合：讲「想在框架里用 DOTS 该怎么接」——答案是<b>框架什么都不用为 DOTS 做</b>：
    /// 把 DOTS 藏在零依赖纯 C# 接缝后（ports &amp; adapters），System 层每帧 Tick 驱动、World 自建不进 player loop、
    /// ECS 程序集永不进热更列表（AOT 边界）。这是「可替换后端」模式套在计算密集仿真上，和「资源加载」章把
    /// YooAsset 藏在 <c>IAssetProvider</c> 后是同一招。
    /// </summary>
    /// <remarks>
    /// <b>本章刻意只跳转框架自身代码</b>（<c>IAssetProvider</c> 接缝先例、System 层、热更程序集图）——
    /// DOTS 的真实活样板在垂直切片 Outpost 里（战斗仿真后端），而框架与切片未来可能拆成两个独立 package，
    /// 故 Outpost 只做文字指路、不硬链文件路径，保证框架 demo 单独成包时零断链。
    /// </remarks>
    public sealed class DotsIntegrationModule : DemoModuleBase
    {
        public override string Id => "dots-integration";
        public override string Title => "DOTS/ECS · 与框架融合";
        public override string Category => "进阶";
        public override int Order => 40; // 最深一章：建立在「热更(20)」「资源底层(10)」的接缝/AOT 认知之上
        public override string Summary =>
            "想在框架里用 DOTS？框架不为 DOTS 加任何模块——把它藏在零依赖纯 C# 接缝后（ports & adapters），" +
            "System 层每帧 Tick、World 自建不进 player loop、ECS 程序集永不进热更（AOT 边界）。同「资源加载」把 YooAsset 藏在 IAssetProvider 后是同一招。";

        // 框架自身的接缝先例 / 驱动点 / AOT 边界执行处——本章可点击跳转全部落在这些框架文件上（不跳 Outpost）。
        private static readonly CodeRef SeamPrecedent = new(
            "Assets/Game/Framework/Core/Asset/AssetProviderFactory.cs", "CreateDefault",
            "IAssetProvider 后端工厂（换后端只改这一行的先例）");
        private static readonly CodeRef SeamInterface = new(
            "Assets/Game/Framework/Core/Asset/IAssetProvider.cs", "interface IAssetProvider",
            "零依赖接缝接口（ports & adapters 的 port）");
        private static readonly CodeRef SystemDriver = new(
            "Assets/Game/Framework/Core/Systems/MonoSystemBase.cs", "class MonoSystemBase",
            "System 层（realtime 仿真的驱动位，ADR-0014）");
        private static readonly CodeRef AotBoundary = new(
            "Assets/Game/Framework/Build/Editor/HotUpdateAssemblyGraph.cs", "class HotUpdateAssemblyGraph",
            "热更程序集图（AOT↔热更边界机器校验）");

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddSectionTitle("定位：框架不「集成」DOTS，而是不挡它的路");
            host.AddNote("框架内核没有 DOTS 模块、不引用 Entities、不引用 Burst——这是刻意的：DOTS 是可选的性能后端，" +
                         "不该让每个用框架的项目都背上 Entities 包。框架要做的只有一件事：给出一个够窄的接缝，让一个 DOTS 后端能" +
                         "「整体塞进去、消费方零改动」。你已经在「资源加载」章见过这招——YooAsset 就藏在 `IAssetProvider` 后，" +
                         "换 Addressables/自研只改工厂一行。DOTS 是同一招，套在「计算密集的逐帧仿真」上。", SeamPrecedent);
            host.AddConcept("接缝(seam)",
                "一个零依赖纯 C# 接口：规则演算在它后面，表现/数据/编排在它前面。接口只用 `System.Numerics` 这类中立类型、" +
                "不碰 UnityEngine，放进一个 `noEngineReferences` 的 asmdef——这样它永远是 AOT、永远可单测、永远能被 DOTS 或 OOP 任一实现填充。");
            host.AddConcept("ports & adapters",
                "port = 接缝接口（框架/业务只认它）；adapter = 具体后端（OOP 参考实现 / DOTS 实现）。" +
                "换 adapter 不动 port，消费方（System/Model/表现）零改动——这正是「可对拍、可切换」的结构前提。");

            // ── 五步 ──
            host.AddSectionTitle("五步：把一个 DOTS 后端塞进框架");
            host.AddStep("⓪", "定义零依赖接缝接口：`Tick(dt)` + 只读快照（按索引零分配遍历）+ 事件（在 Tick 调用栈内同步触发）。" +
                              "接口住在 `noEngineReferences` 的 asmdef，永保 AOT 与可测。", SeamInterface);
            host.AddStep("①", "先写 OOP 参考实现（直白 List + 逐帧演算）——它是规则的<b>可执行规格</b>，也是后面 DOTS 后端的<b>对拍基线</b>。" +
                              "「先 OOP、留接缝」：不预先为性能上重武器，但把后路留好。");
            host.AddStep("②", "System 层每帧 Tick：逐帧实时仿真归 System（ADR-0014），不走 Command。System 持有接缝实例，`Update` 里 `Tick(dt)`，" +
                              "把事件翻成表现（渲染/特效/音效）、把聚合值写进 Model 供 View 只读订阅。", SystemDriver);
            host.AddStep("③", "加 DOTS 后端（引 Entities/Burst，另开一个 AOT asmdef）：自建 `World` 不进 player loop、不用 SystemGroup——" +
                              "接缝契约是「外部逐帧 Tick、同步返回」，所以所有 job 当帧 `Complete`，事件以记录缓冲带回主线程按序重放" +
                              "（托管委托进不了 Burst）。换后端只改工厂一个分支，和换 `IAssetProvider` 一模一样。", SeamPrecedent);
            host.AddStep("④", "AOT 边界：DOTS 后端程序集<b>永不进热更列表</b>——Burst 产物是 AOT 原生码，HybridCLR 解释器执行不了。" +
                              "接缝接口在内核/热更侧、实现在 AOT 侧，ports & adapters 让这条边界自然成立，且由构建期校验器机器执行。", AotBoundary);

            // ── 驱动契约 ──
            host.AddSectionTitle("驱动契约：World 生命周期与线程");
            host.AddTable(
                new[] { "维度", "约定", "为什么" },
                new[] { "World", "自建、不进 player loop、不用 SystemGroup", "接缝契约是「外部逐帧 Tick、同步返回」；放进 player loop 就失去了同步返回的调用栈" },
                new[] { "job 完成", "每个 job 当帧 `Complete`", "Tick 返回时快照与事件必须已就绪（消费方在同一调用栈里读）" },
                new[] { "事件", "job 内记录进缓冲 → 回主线程按序重放", "Burst 不认托管委托；顺序在缓冲里定死＝两后端事件流可复现" },
                new[] { "三角函数", "留托管侧（`System.Math`）", "Burst libm 与 .NET 在超越函数上有 ulp 级差异——进对拍的量必须收口单一编译域" },
                new[] { "Dispose", "World 随接缝 `IDisposable` 统一释放", "消费方按 `IDisposable` 管理，完全不感知底层是 DOTS 还是 OOP" });

            // ── 对拍两级 ──
            host.AddSectionTitle("对拍两级：怎么证明两个后端「是同一个游戏」");
            host.AddNote("这是 DOTS 后端最容易翻车的地方：同一套规则、两种执行模型，凭什么相信它们结果一致？靠<b>对拍</b>——" +
                         "同 Setup + 同种子 + 同 Tick 序列跑两个后端，比对输出。分两级：");
            host.AddTable(
                new[] { "级别", "怎么做", "证明什么" },
                new[] { "逻辑级", "关 Burst（`EnableBurstCompilation=false`，job 走 Mono JIT＝与参考实现同浮点语义），逐 tick <b>逐位</b>断言全部聚合值", "移植零逻辑偏差——纯算法搬对了" },
                new[] { "规格级", "开 Burst，两后端各自独立跑，按波比聚合值", "「同一个游戏、不是逐位同一局」：跨编译域浮点 ulp 差异会被混沌放大成极小的归属漂移，但规格等价" });
            host.AddTip("关键发现：FloatMode.Strict 也挡不住 Burst×Mono 的 ulp 级差异——lockstep 级确定性（帧同步/回放/断线重连）必须把参与的运算收口在单一编译域。" +
                        "把 DOTS 和网络同步结合时，这是绕不开的硬约束。");

            // ── 何时值得 ──
            host.AddSectionTitle("性能：什么时候才值得上 DOTS");
            host.AddTable(
                new[] { "负载特征", "OOP（托管 List）", "DOTS（Burst + chunk）", "怎么选" },
                new[] { "数百实体", "够用", "过度工程", "别上——接缝留着就行" },
                new[] { "数千实体逐帧全算", "开始吃紧", "从容", "考虑，先量再换" },
                new[] { "<b>累计增长</b>的状态（如残骸、弹幕历史）", "随时间/规模持续劣化", "并行摊平、耗时平坦", "值得——差距随规模<b>和时间</b>拉大" });
            host.AddNote("「先 OOP、留接缝，规模真到了再换 DOTS」——这就是接缝优先的价值：接缝的成本只是多写一个接口，" +
                         "换来的是「不赌未来性能、但永远留着换后端的后路」。等 profiler 告诉你 OOP 后端在帧预算边缘了，换 adapter 就行，消费方一行不改。");

            // ── 活样板（Outpost，纯文字指路，不硬链路径）──
            host.AddSectionTitle("活样板：垂直切片 Outpost");
            host.AddNote("本框架的垂直切片 demo「Outpost」（塔式生存自动战斗）把上面每一步都走了一遍真的：战斗仿真藏在零依赖接缝 " +
                         "`IBattleSim` 后，OOP 参考后端与 DOTS 后端（自建 World + Burst job）<b>可对拍、可一键切换</b>（战斗设置窗里选，下一局生效）；" +
                         "敌人海 + 真弹道扫掠碰撞 + 残骸推挤被推到「切 OOP 后期肉眼掉帧、切 DOTS 满帧」的量级——后端置换的收益从「基准里的数字」变成了「手感」。" +
                         "若你的工程里带着 Outpost 切片，去它的 `Sim/`（接缝 + 参考实现）与 `Sim.Ecs/`（DOTS 后端）看真实现，以及各自的 ADR（对拍方法论 / 规模基准）。");
            host.AddSubNote("为什么这里只指路不给跳转按钮：框架与 Outpost 切片未来可能拆成两个独立 package，框架 demo 硬链 Outpost 文件路径会在拆分后断链。" +
                            "本章的可点击跳转因此全部落在框架自身的接缝先例上（上面那些「查看源码」）——DOTS 后端就是这套接缝套在计算后端上的应用。");
        }
    }
}
