using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using Game.Framework.System;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·System：演示“逻辑归位”——一步操作 Command 直接改 Model 就够；带规则的逻辑抽到 System，
    /// Command 退化成只表达意图、调用 System。这才是推荐形态，别把 Command→Model 直连当终态。
    /// </summary>
    public sealed class SystemDemoModule : DemoModuleBase
    {
        public override string Id => "system";
        public override string Title => "System · 逻辑归位";
        public override string Category => "核心";
        public override int Order => 25;
        public override string Summary =>
            "一步操作 Command 直接改 Model 就够；带规则的逻辑（够钱才扣、扣钱再加道具）抽到 System——System 是把一类相关逻辑聚成的、能独立运转的“系统”，Command 只说“我要买”、调用它。";

        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterValue(new WalletModel(), typeof(WalletModel));
            builder.RegisterValue(new ShopSystem(), typeof(IShopSystem));
        }

        public override void Build(DemoModuleHost host)
        {
            host.AddSectionTitle("演示");
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
            host.AddNote("• “购买药水”有规则（够钱才扣、扣钱再加道具）——逻辑抽到 ShopSystem，Command 只表达意图、调用 System。",
                CodeRef.Here("class ShopSystem", "ShopSystem 里的逻辑"));
            host.AddTip("迁移心智：早期可以把规则先写在 Command 里；当相关逻辑变多、需要聚成一个整体来维护时，就抽到 System——"
                + "成本很低，Command 本就是入口，抽走逻辑后它退化成一行薄壳。");

            host.AddSectionTitle("System 的本质");
            host.AddConcept("逻辑聚合（核心）", "把一类相关逻辑（买、卖、定价、库存…）聚成一个内聚、能独立运转的“系统”（可有依赖）——这正是它叫 System 的原因。");
            host.AddConcept("意图 vs 逻辑", "Command 表达“要做什么”（薄入口），System 实现“怎么做”（厚逻辑）。逻辑从 Command 抽到 System，分工才清晰。");
            host.AddNote("说明：这个 ShopSystem 无状态，直接用传入的 ctx 取 Model 最省；需要持有状态的 System 可走 Mono（MonoSystemBase）或绑定 Context，改用 this.GetModel。");
        }
    }

    /// <summary>钱包 Model：金币 + 药水数量。</summary>
    public sealed class WalletModel : IModel
    {
        public readonly RP<int> Gold = new(100);
        public readonly RP<int> Potions = new(0);
    }

    /// <summary>商店逻辑层：带规则的操作放这里供 Command 调用。本例无状态，通过传入的 ctx 访问 Model。</summary>
    public interface IShopSystem : ISystem
    {
        bool TryBuyPotion(ICommandContext ctx);
    }

    /// <summary>商店逻辑实现：购买药水的多步规则。</summary>
    public sealed class ShopSystem : IShopSystem
    {
        public const int PotionPrice = 50;

        // 购买规则：够钱才扣、扣钱后加一瓶药水。多步逻辑——正是该放 System 的东西，而不是散在各个 Command 里。
        public bool TryBuyPotion(ICommandContext ctx)
        {
            var wallet = ctx.GetModel<WalletModel>();
            if (wallet.Gold.Value < PotionPrice) return false;
            wallet.Gold.Value -= PotionPrice;
            wallet.Potions.Value++;
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
        public void Execute(ICommandContext ctx) => ctx.GetSystem<IShopSystem>().TryBuyPotion(ctx);
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
