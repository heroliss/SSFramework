using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using Game.Framework.Systems;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·System：演示“逻辑归位”——一步操作 Command 直接改 Model 就够；带规则的逻辑抽到 System，
    /// Command 退化成只表达意图、调用 System。是否引入 System 取决于规则是否值得复用和独立演进。
    /// </summary>
    public sealed class SystemDemoModule : DemoModuleBase
    {
        public override string Id => "system";
        public override string Title => "逻辑系统（System）· 规则归位";
        public override string Category => "核心";
        public override int Order => 25;
        public override string Summary =>
            "一步操作 Command 直接改 Model 就够；带规则的逻辑（够钱才扣、扣钱再加道具）抽到 System——System 是把一类相关逻辑聚成的、能独立运转的“系统”，Command 只说“我要买”、调用它。";

        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterModel(new WalletModel());
            builder.RegisterSystem(new ShopSystem());
        }

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("带规则的逻辑从 Command 归位到 System");
            host.AddNote("当一次操作开始包含校验、多步状态变化或多个入口时，把规则散落在 Command 会产生重复与不一致。`System` 把这组业务不变量收进一个可复用实现，下面用赚金币和购买药水对照两种边界。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：一步操作 vs 带规则操作");
            var goldLabel = host.AddValueDisplay("", CodeRef.Here("struct GetGoldCommand", "GetGoldCommand"));
            var potionLabel = host.AddValueDisplay("", CodeRef.Here("struct GetPotionsCommand", "GetPotionsCommand"));
            Bag.Subscribe(this.ExecuteCommand(new GetGoldCommand()), v => goldLabel.text = $"金币：{v}");
            Bag.Subscribe(this.ExecuteCommand(new GetPotionsCommand()), v => potionLabel.text = $"药水：{v}");

            host.AddActionRow("赚金币 +50", () => this.ExecuteCommand(new EarnGoldCommand()),
                CodeRef.Here("struct EarnGoldCommand", "EarnGoldCommand"));
            host.AddActionRow($"购买药水（{ShopSystem.PotionPrice} 金）", () => this.ExecuteCommand(new BuyPotionCommand()),
                CodeRef.Here("struct BuyPotionCommand", "BuyPotionCommand"));

            host.AddSectionTitle("两种写法的分界");
            host.AddNote("• “赚金币”是一步操作——Command 直接改 Model 就够，这是简单形态。",
                CodeRef.Here("struct EarnGoldCommand", "直接改 Model"));
            host.AddNote("• “购买药水”有规则（够钱才扣、扣钱再加道具）——逻辑抽到 `ShopSystem`，Command 只表达意图、调用 System。",
                CodeRef.Here("class ShopSystem", "ShopSystem 里的逻辑"));
            host.AddTip("迁移心智：早期可以把规则先写在 Command 里；当相关逻辑变多、需要聚成一个整体来维护时，就抽到 System——"
                + "成本很低，Command 本就是入口，抽走逻辑后它退化成一行薄壳。");

            host.AddSectionTitle("System 的本质");
            host.AddConcept("逻辑聚合（核心）", "把一类相关逻辑（买、卖、定价、库存…）聚成一个内聚、能独立运转的“系统”（可有依赖）——这正是它叫 System 的原因。");
            host.AddConcept("意图 vs 逻辑", "Command 表达“要做什么”（薄入口），System 实现“怎么做”（厚逻辑）。逻辑从 Command 抽到 System，分工才清晰。");
            host.AddConcept("两头设计、Command 对接", "两层常从不同方向长出来：Command 从视图层倒推——View 要能做哪些操作，就声明哪些 Command（内容可以先留空占位）；"
                + "System 从逻辑自身的内聚出发——把相关规则聚成能独立自治、不关心谁来调的系统。两头各自定好，最后用 Command 收口对接：向下整理参数调 System、向上取数适配回 View。视图与逻辑因此能并行开发、互不耦合。");
            host.AddNote("`IShopSystem` 只暴露业务语义 `TryBuyPotion()`；WalletModel 是实现细节，由 Context 构建时注入 `ShopSystem`。调用者无需知道容器、Context 或数据存放方式。",
                CodeRef.Here("[Inject] private WalletModel", "实现侧注入依赖"));
            host.AddSubNote("这是一条窄接口、厚实现的接缝：以后把钱包改成服务端校验或加入库存规则，Command 仍只调用同一个业务动作。模块深度来自隐藏变化，而不是暴露更多框架类型。");

            host.AddSectionTitle("什么时候不需要 System");
            host.AddConcept("保留在 Command", "一次赋值、自增、重置等原子操作，没有跨对象不变量，也不会被其他入口复用。");
            host.AddConcept("提取到 System", "规则包含校验与多步提交、会被多个 Command/流程调用，或需要替换实现与独立测试。");
            host.AddTip("先按业务复杂度选择最浅结构，再在规则出现时加深模块；不要把“所有写入都必须经过 System”当成仪式。");
        }
    }

    /// <summary>钱包 Model：金币 + 药水数量。</summary>
    public sealed class WalletModel : IModel
    {
        public readonly RP<int> Gold = new(100);
        public readonly RP<int> Potions = new(0);
    }

    /// <summary>商店业务接口：只暴露领域动作，不泄漏 Command 的编排上下文或数据存放方式。</summary>
    public interface IShopSystem : ISystem
    {
        bool TryBuyPotion();
    }

    /// <summary>商店逻辑实现：购买药水的多步规则；钱包依赖在 Context 构建时注入。</summary>
    public sealed class ShopSystem : IShopSystem
    {
        public const int PotionPrice = 50;
        [Inject] private WalletModel _wallet;

        // 购买规则：够钱才扣、扣钱后加一瓶药水。多步逻辑——正是该放 System 的东西，而不是散在各个 Command 里。
        public bool TryBuyPotion()
        {
            if (_wallet.Gold.Value < PotionPrice) return false;
            _wallet.Gold.Value -= PotionPrice;
            _wallet.Potions.Value++;
            return true;
        }
    }

    /// <summary>赚金币 +50：一步操作，Command 直接改 Model（简单形态）。</summary>
    public readonly struct EarnGoldCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<WalletModel>().Gold.Value += 50;
    }

    /// <summary>购买药水：Command 只表达意图，逻辑在 ShopSystem（推荐形态）。</summary>
    public readonly struct BuyPotionCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetSystem<IShopSystem>().TryBuyPotion();
    }

    /// <summary>只读查询：金币流。</summary>
    public readonly struct GetGoldCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<WalletModel>().Gold;
    }

    /// <summary>只读查询：药水数量流。</summary>
    public readonly struct GetPotionsCommand : ICommand<ReadOnlyReactiveProperty<int>>
    {
        public ReadOnlyReactiveProperty<int> Execute(ICommandContext ctx) => ctx.GetModel<WalletModel>().Potions;
    }
}
