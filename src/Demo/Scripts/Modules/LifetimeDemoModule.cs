using Game.Framework;
using Game.Framework.Demo.Core;
using R3;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·生命周期：DisposableBag 把“有生命周期的东西”统一托管、宿主销毁批量释放；
    /// CreateChild 开更短的子作用域，可提前释放且不牵连父级。本章聚焦子作用域，用一个本地 R3 信号源演示。
    /// </summary>
    public sealed class LifetimeDemoModule : DemoModuleBase
    {
        public override string Id => "lifetime";
        public override string Title => "生命周期 · DisposableBag";
        public override string Category => "核心";
        public override int Order => 50;
        public override string Summary =>
            "订阅 / 资源句柄 / 池租借 / 子作用域都登记进 Bag，宿主销毁一次性释放、不漏。需要更短作用域时 CreateChild：子 Bag 可提前 Dispose、不碰父级，父级释放自动级联子级。";

        public override void Build(DemoModuleHost host)
        {
            // 本地 R3 信号源，父子两条订阅都听它。真实项目里状态放 Model，这里只为聚焦 Bag，用个本地流当信号。
            var clock = new ReactiveProperty<int>(0);
            Bag.Add(clock); // 流本身也是 IDisposable —— 进 Bag，随本章（Teardown）一起释放

            // ── 定位 ──
            host.AddPositioning("有生命周期的东西都进 Bag，宿主销毁一次性释放");
            host.AddNote("订阅 / 资源句柄 / 池租借 / 子作用域全登记进 `Bag`，宿主销毁时批量释放、一个不漏，业务不写手动清理。需要比宿主更短的作用域就 `CreateChild`：子 Bag 可提前 Dispose、不碰父级，父级释放自动级联子级。");

            // ── 动手试 ──
            host.AddSectionTitle("动手试：子作用域可提前释放，父级不受影响");

            var parentLabel = host.AddValueDisplay();
            Bag.Subscribe(clock, v => parentLabel.text = $"父作用域订阅看到：{v}");

            var child = Bag.CreateChild();
            var childLabel = host.AddValueDisplay();
            child.Subscribe(clock, v => childLabel.text = $"子作用域订阅看到：{v}");

            host.AddActionRow("信号 +1", () => clock.Value++,
                CodeRef.Here("Bag.Subscribe(clock", "订阅用法"));
            host.AddActionRow("释放子作用域", () =>
            {
                child.Dispose(); // 批量释放子 Bag 里的所有订阅；幂等，重复点无副作用
                childLabel.text = "子作用域订阅看到：（已释放，停止接收）";
            }, CodeRef.Here("child.Dispose(); // 批量释放子 Bag", "释放用法"));
            host.AddActionRow("重建子作用域", () =>
            {
                child.Dispose(); // 先确保旧的释放（幂等），避免重复订阅叠加
                child = Bag.CreateChild(); // 重建已释放的局部作用域
                // R3 ReactiveProperty 订阅即推当前值，子作用域立刻同步到最新信号
                child.Subscribe(clock, v => childLabel.text = $"子作用域订阅看到：{v}");
            }, CodeRef.Here("child = Bag.CreateChild(); // 重建已释放", "CreateChild"));

            host.AddNote("点几次「信号 +1」两条一起涨 → 「释放子作用域」后再点，只有父级涨、子级冻住 → 「重建子作用域」后子级订阅立刻补回当前值、恢复跟随。",
                CodeRef.Here("var child = Bag.CreateChild()", "子作用域用法"));

            host.AddSectionTitle("Bag 装什么");
            host.AddConcept("订阅", "R3 / Framework Event / UnityEvent / C# event —— 都自动包成 `IDisposable` 入 `Bag`。");
            host.AddConcept("资源句柄", "`Bag.Load` / `LoadScene` 返回资源本体、句柄留在 `Bag`（详见「资源加载 · 就绪与生命周期」）。");
            host.AddConcept("池租借", "`Bag.Rent` / `Bag.Spawn` 借来的实例，释放时自动归还；个别要提前退场的，在同一 bag 上 `Return` / `Despawn` 单个归还（详见「对象池」章）。");
            host.AddConcept("子作用域 / 任意 IDisposable", "`CreateChild` 的子 `Bag`，以及 `Bag.Add` 进来的任何 `IDisposable`。");

            host.AddSectionTitle("作用域怎么分");
            host.AddConcept("基类 Bag", "`MonoView/Model/System/Utility` 内置，跟宿主 `OnDestroy` 一起释放——最常用的一档。");
            host.AddConcept("CreateChild", "需要比宿主更短的作用域时开：`OnDisable` / 一个回合 / “清理一次”按钮。子 `Bag` 单独 `Dispose` 不碰父级，父级释放自动级联子级（`Dispose` 幂等）。");

            host.AddSectionTitle("容易混淆：自动清理不等于旧引用还能继续用");
            host.AddConcept("Utility 是借用能力", "`GetUtility<T>()` 取得的服务归 Context 或 Mono 宿主所有；可以在当前作用域复用，但不要缓存到 owner 之外。宿主结束后应从新的 Context 重新解析，而不是继续调用旧引用。");
            host.AddConcept("终态按风险不同", "UI / 存储在释放后继续操作会抛 `ObjectDisposedException`，因为幽灵开窗或丢存档必须 fail-fast；音频播放与音量修改则记录开发期错误后安全 no-op，因为漏一声不该炸掉游戏，只读查询仍可看到最终快照。");
            host.AddConcept("保留内核 ≠ 复活服务", "Mono 外壳销毁时保留 disposed 内核或显式终态守卫，只是为了让旧借用引用得到清晰、一致的生命周期错误；不会重建 Canvas、UIDocument、存储 Provider 或 AudioSource 根。把字段清成 null 反而会丢失真正原因，只剩 NRE。");
            host.AddTip("命名约定：按“何时清理”给子 Bag 起名 _enableBag / _roundBag / _loadedBag，在对应回调里 Dispose 后再 CreateChild 重建。"
                + "统一进 Bag 的意义在于——只要东西进了 Bag 就不会忘记释放，宿主一销毁全部连根带走；但从 Context 解析出的借用服务仍不能越过 Context 寿命。核心主线最后一章会打开诊断面板，把 Context 树、Container 注册和 Bag 存活趋势放在一起观察。");
        }
    }
}
