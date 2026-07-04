using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Localization;
using Game.Framework.UI.Toolkit;
using R3;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·本地化：响应式当前语言（SetLocale 推送、已显示 UI 自动刷新）+ key 查询回退链
    /// （fallbackLocale → 裸 key 上屏）+ 文本源接缝（本章用内置字典源；真实项目 ~10 行包自己的配置表）。ADR-0024。
    /// </summary>
    public sealed class LocalizationDemoModule : DemoModuleBase
    {
        public override string Id => "localization";
        public override string Title => "本地化 · 多语言";
        public override string Category => "能力";
        public override int Order => 60;
        public override string Summary =>
            "响应式多语言：Locale 是 RP、SetLocale 推送即全量刷新（BindLocalizedText 自动重取文本）；" +
            "缺 key = 裸 key 上屏（最好的缺失报告）；文本源是单方法接缝，业务包自己的配置表。ADR-0024。";

        // locale code 是开放字符串 + 业务常量（与音频组 / 存储 key 同一「常量管理字符串契约」姿势）。
        private const string Zh = "zh-CN";
        private const string En = "en";

        /// <summary>
        /// 注册路径：文本源经构造注入（同存储 provider 姿势）。本章用内置字典源；
        /// 真实项目的源是 ~10 行包 Luban 表的 adapter（TryGet 查表即可），换源不动业务代码。
        /// ⚠ 本方法在临时实例上被调（见 DemoModuleBase 说明），Build 要用的对象不能存字段、只能从 Context 解析。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterOwned(
                new LocalizationUtility(BuildDemoSource(), initialLocale: Zh, fallbackLocale: Zh),
                typeof(ILocalizationUtility));
        }

        public override void Build(DemoModuleHost host)
        {
            var loc = this.GetUtility<ILocalizationUtility>();

            // ── 定位 ──
            host.AddSectionTitle("定位：三件小事——语言状态、key 查询、换语言 UI 跟着变");
            host.AddNote("框架只管**「当前语言」全局状态（响应式 RP）+ key → 文本查询 + 换语言推送驱动重绑**；文本数据来自 `ILocalizedTextSource` 单方法接缝（`TryGet(locale, key, out text)`）——业务包自己的 Luban 表就是 adapter，表本身就是翻译工作流（Excel 一列一语言发给翻译）。语言列表、`SystemLanguage` 映射、语言选择持久化（设置数据走 `IStorageUtility`，启动回灌）都归业务。",
                new CodeRef("Assets/Game/Framework/Core/Localization/ILocalizationUtility.cs", "public interface ILocalizationUtility", "本地化入口契约"));
            host.AddSubNote("locale code 是开放字符串 + 业务常量（本章 `Zh = \"zh-CN\"` / `En = \"en\"`，与音频组、存储 key 同一姿势）；本章文本源是内置 `DictionaryLocalizedTextSource`（测试 / 小游戏也直接用它）。");

            // ── 当前语言与切换 ──
            host.AddSectionTitle("切换语言：SetLocale 推送，绑定自动刷新");
            var localeLabel = host.AddValueDisplay();
            Bag.BindText(localeLabel, loc.Locale, l => $"当前语言：{l}"); // Locale 就是 RP，直接 BindText

            host.AddActionRow("切到中文（SetLocale zh-CN）", () => loc.SetLocale(Zh),
                CodeRef.Here("loc.SetLocale(Zh)", "切中文"));
            host.AddActionRow("切到英文（SetLocale en）", () => loc.SetLocale(En),
                CodeRef.Here("loc.SetLocale(En)", "切英文"));
            host.AddNote("下面所有文本都是**绑定**出来的——点上面两个按钮观察它们整体切换。同值 `SetLocale` 不推送（连点同一语言不会无谓重刷）。**不做「需重启生效」**：表驱动 + 响应式绑定下没有理由重启。");

            // ── 绑定演示 ──
            host.AddSectionTitle("BindLocalizedText：文本绑 key，不绑死文案");
            var startLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(startLabel, "menu/start");

            var welcomeLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(welcomeLabel, "lobby/welcome", "SS"); // 静态格式化参数
            host.AddNote("`Bag.BindLocalizedText(label, key)`：立即取当前语言文本、换语言自动重取，订阅随 Bag 退订（与 `BindText` 同心智、经 bag 的 Context 解析服务与 `Bag.Load` 同源）。带参重载适合**绑定时就固定**的参数（上面的玩家名）。",
                CodeRef.Here("Bag.BindLocalizedText(startLabel, \"menu/start\")", "本地化绑定"));

            // 动态参数 × 语言 双源组合：R3 CombineLatest 一行，不需要框架专门 API。
            var clicks = new RP<int>(0);
            Bag.Add(clicks);
            var clicksLabel = host.AddValueDisplay();
            Bag.Bind(clicks.CombineLatest(loc.Locale, (c, _) => loc.Get("demo/clicks", c)),
                s => clicksLabel.text = s);
            host.AddActionRow("点我 +1（动态参数 × 语言 双源组合）", () => clicks.Value++,
                CodeRef.Here("clicks.CombineLatest(loc.Locale", "双源组合"));
            host.AddSubNote("参数要**动态变**的文案不用专门 API：`CombineLatest(数据, Locale)` 一行组合——点按钮变数字、切语言变模板，两个方向都即时刷新。");

            // ── 缺 key 语义 ──
            host.AddSectionTitle("缺 key：回退链 → 裸 key 上屏");
            var onlyZhLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(onlyZhLabel, "demo/only-zh");
            var missingLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(missingLabel, "demo/missing-everywhere");
            host.AddNote("上面第一行的 key **只有中文有**——切英文时回退 fallbackLocale（本章配 zh-CN）仍显示中文，不留空。第二行的 key **哪都没有**——直接**裸 key 上屏**：不抛异常（文案缺失不炸游戏）、不给空串（静默丢文案最难发现），屏幕上看到 `demo/missing-everywhere` 就是最好的缺失报告（Console 另有一次性警告，同一缺失不刷屏）。");

            // ── per-locale 资源 ──
            host.AddSectionTitle("per-locale 资源：组合既有原语，刻意零 API");
            host.AddConcept("按 locale 分包", "YooAsset collector 按语言建 package（`L10N_zh` / `L10N_en`），业务按 locale 映射包名 `Bag.Load<T>(pkg, location)`——多 package 是现成能力（「资源系统」章）。");
            host.AddConcept("换语言换图", "`Bag.Subscribe(loc.Locale, l => ...重新 Load...)` 响应式组合；命名 / 分包约定各项目不同，框架提供 helper 反而强加约定。");

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("复数 / 性别 / CLDR 规则", "ICU 级复杂度；绝大多数游戏文案用「{0} 个」直译绕开，真需要的项目接专门库，在 Get 的输出上再包一层。");
            host.AddConcept("翻译导出导入工具", "Luban 的 Excel 一列一语言，本身就是翻译工作流。");
            host.AddConcept("场景静态文本收集", "本框架 UI 全代码驱动（窗口 = View 类），文本入口天然收敛在 BindLocalizedText，没有「散落场景里的 Text 组件」问题。");
            host.AddConcept("字体切换", "归字体策略（ADR-0025，规划中）：字体模块订阅 `Locale` RP，本模块只出信号。");

            host.AddTip("速记：注册 = RegisterOwned(new LocalizationUtility(源, 初始语言, fallback))；UI 全用 Bag.BindLocalizedText(label, key)；设置页 = SetLocale + 存档回灌；缺 key 裸 key 上屏。深度见 framework-guide 本地化章 / ADR-0024。");
        }

        // demo 文本源：中英双语 + 一条只有中文（演示 fallback）。真实项目这些行在 Luban 表里。
        private static DictionaryLocalizedTextSource BuildDemoSource() => new DictionaryLocalizedTextSource()
            .Add(Zh, "menu/start", "开始游戏")
            .Add(Zh, "lobby/welcome", "欢迎回来，{0}！")
            .Add(Zh, "demo/clicks", "已点击 {0} 次")
            .Add(Zh, "demo/only-zh", "这条文案只有中文（英文下走 fallback 仍显示我）")
            .Add(En, "menu/start", "Start Game")
            .Add(En, "lobby/welcome", "Welcome back, {0}!")
            .Add(En, "demo/clicks", "Clicked {0} time(s)");
    }
}
