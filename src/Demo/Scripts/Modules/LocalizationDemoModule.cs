using Cysharp.Threading.Tasks;
using DemoCfg;
using Game.Framework.Audio;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Localization;
using Game.Framework.UI.Toolkit;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·本地化：响应式当前语言（SetLocale 推送、已显示 UI 自动刷新）+ key 查询回退链
    /// （fallbackLocale → 裸 key 上屏）+ 文本源接缝。本章文本源是<b>Luban 表 adapter</b>（最常见用法的实物），
    /// 并带图片 / 音频 per-locale 的实操样板（location 后缀约定 + 响应式重载）。ADR-0024。
    /// </summary>
    public sealed class LocalizationDemoModule : DemoModuleBase
    {
        public override string Id => "localization";
        public override string Title => "本地化 · 多语言";
        public override string Category => "能力";
        public override int Order => 60;
        public override string Summary =>
            "响应式多语言：Locale 是 RP、SetLocale 推送即全量刷新（BindLocalizedText 自动重取文本）；" +
            "文本源 = Luban 表 adapter（~10 行实物）；图片 / 音频按 locale 后缀命名 + 响应式重载。ADR-0024。";

        // locale code 是开放字符串 + 业务常量（与音频组 / 存储 key 同一「常量管理字符串契约」姿势）。
        private const string Zh = "zh-CN";
        private const string En = "en";

        private const string L10NDataDir = "Assets/Game/Framework/Demo/Configs~/Datas";

        /// <summary>
        /// 注册路径：文本源要吃配置表服务（另一个 Utility）——用 <c>RegisterFactory</c> 让容器解决依赖顺序：
        /// 首次解析（打开本章）时，场景里的配置服务早已注册完成。
        /// 工厂产物不被容器 Dispose（LocalizationUtility.Dispose 只完结 Locale 订阅，可接受）；
        /// 不依赖其他服务的源（如字典源）直接 RegisterOwned 即可（见 LocalizationTests）。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterFactory(
                c => new LocalizationUtility(
                    new LubanTextSource((IConfigUtility<Tables>)c.Resolve(typeof(IConfigUtility<Tables>))),
                    initialLocale: Zh, fallbackLocale: Zh),
                typeof(ILocalizationUtility));
        }

        /// <summary>
        /// 最常见用法的实物：~10 行包 Luban 表（TbL10N 一行一 key、一列一语言）。
        /// 配置表异步加载，就绪前 TryGet false → 裸 key 上屏（可见的「加载中」）；
        /// 翻译列留空 = 该语言缺失 → 同样 false，交给框架统一走 fallback 链。
        /// </summary>
        private sealed class LubanTextSource : ILocalizedTextSource
        {
            private readonly IConfigUtility<Tables> _config;

            public LubanTextSource(IConfigUtility<Tables> config) => _config = config;

            public bool TryGet(string locale, string key, out string text)
            {
                text = null;
                var row = _config.Tables?.TbL10N.GetOrDefault(key);
                if (row == null) return false;
                text = locale switch { Zh => row.ZhCn, En => row.En, _ => null };
                return !string.IsNullOrEmpty(text);
            }
        }

        public override void Build(DemoModuleHost host)
        {
            var loc = this.GetUtility<ILocalizationUtility>();

            // ── 定位 ──
            host.AddSectionTitle("定位：三件小事——语言状态、key 查询、换语言 UI 跟着变");
            host.AddNote("框架只管**「当前语言」全局状态（响应式 RP）+ key → 文本查询 + 换语言推送驱动重绑**；文本数据来自 `ILocalizedTextSource` 单方法接缝（`TryGet(locale, key, out text)`）。语言列表、`SystemLanguage` 映射、语言选择持久化（设置数据走 `IStorageUtility`，启动回灌）都归业务。",
                new CodeRef("Assets/Game/Framework/Core/Localization/ILocalizationUtility.cs", "public interface ILocalizationUtility", "本地化入口契约"));
            host.AddSubNote("locale code 是开放字符串 + 业务常量（本章 `Zh = \"zh-CN\"` / `En = \"en\"`）。其他多语言方案也能当源接入：I2 Localization 的指定语言查询 ~10 行 adapter；Unity 官方包因绑死 Addressables 与本框架 YooAsset 管线冲突，不建议混用——原则只有一条：**别让两个系统都认为自己管着当前语言**。");

            // ── 文本源：Luban 表 adapter ──
            host.AddSectionTitle("文本源：Luban 表 adapter（最常见用法的实物）");
            host.AddNote("本章注册的源就是 **`LubanTextSource`——~10 行包 `TbL10N` 表**（Excel 一行一 key、一列一语言，加语言 = 加一列，表本身就是翻译工作流）。它要吃配置表服务，所以用 `RegisterFactory` 注册：容器在首次解析时替你解决「配置服务先注册、本地化后构造」的依赖顺序，无需手工排序。",
                CodeRef.Here("private sealed class LubanTextSource", "adapter 全文（~10 行）"));
            var fromTableLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(fromTableLabel, "l10n/from-table");
            host.AddActionRow("打开 l10n.xlsx 所在目录（构建期源，~ 目录不导入）", () =>
                UnityEditor.EditorUtility.RevealInFinder($"{L10NDataDir}/l10n.xlsx"),
                CodeRef.Here("builder.RegisterFactory(", "本章的注册代码（工厂解依赖顺序）"));
            host.AddSubNote("改表后菜单「SSFramework/配置表构建/生成全部」重新生成即可生效。配置就绪前查询返回裸 key（可见的「加载中」）；小体量 / 测试用内置 `DictionaryLocalizedTextSource`，不用配表。");

            // ── 当前语言与切换 ──
            host.AddSectionTitle("切换语言：SetLocale 推送，绑定自动刷新");
            var localeLabel = host.AddValueDisplay();
            Bag.BindText(localeLabel, loc.Locale, l => $"当前语言：{l}"); // Locale 就是 RP，直接 BindText

            host.AddActionRow("切到中文（SetLocale zh-CN）", () => loc.SetLocale(Zh),
                CodeRef.Here("loc.SetLocale(Zh)", "切中文"));
            host.AddActionRow("切到英文（SetLocale en）", () => loc.SetLocale(En),
                CodeRef.Here("loc.SetLocale(En)", "切英文"));
            host.AddNote("本章下面所有文本、图片、声音都跟着这两个按钮走。同值 `SetLocale` 不推送（连点同一语言不会无谓重刷）。**不做「需重启生效」**：表驱动 + 响应式绑定下没有理由重启。");

            // ── 文本绑定 ──
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

            // ── 缺 key 语义 ──
            host.AddSectionTitle("缺 key：回退链 → 裸 key 上屏");
            var onlyZhLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(onlyZhLabel, "demo/only-zh");
            var missingLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(missingLabel, "demo/missing-everywhere");
            host.AddNote("第一行的 key 在表里**英文列留空**（翻译没来是常态）——切英文时走 fallbackLocale（本章配 zh-CN）仍显示中文，不留空白。第二行的 key **表里没有**——**裸 key 上屏**：不抛异常（文案缺失不炸游戏）、不给空串（静默丢文案最难发现），屏幕上看到 `demo/missing-everywhere` 就是最好的缺失报告（Console 另有一次性警告，同一缺失不刷屏）。");

            // ── 图片多语言 ──
            host.AddSectionTitle("图片多语言：location 后缀约定 + 响应式重载");
            var bannerCaption = host.AddValueDisplay();
            Bag.Bind(loc.Locale.Select(l => loc.Get("l10n/banner-caption", l)), s => bannerCaption.text = s);

            var banner = new Image { scaleMode = ScaleMode.ScaleToFit };
            banner.style.height = 64;
            banner.style.marginTop = 4;
            banner.style.marginBottom = 4;
            host.Content.Add(banner);

            var bannerBag = Bag.CreateChild();
            Bag.Subscribe(loc.Locale, l =>
            {
                // 换语言换图 = 释放旧句柄 + 按新 locale 重载（子 Bag 重建是资源章 §17 的既定写法）。
                bannerBag.Dispose();
                bannerBag = Bag.CreateChild();
                LoadBanner(bannerBag, banner, l).Forget();
            });
            host.AddNote("图按 **location 后缀约定**命名：`l10n-banner_zh-CN` / `l10n-banner_en`（YooAsset 按文件名寻址，放收集目录即可）；换语言 = `Bag.Subscribe(loc.Locale, ...)` 里 Dispose 旧子 Bag → 按新 locale `Load<Sprite>`。**框架刻意零专门 API**——这就是资源系统 + 响应式的一行组合；大体量项目按 locale 分包（YooAsset 多 package）同理，只是多一层「locale → 包名」映射。",
                CodeRef.Here("LoadBanner(bannerBag, banner, l)", "换语言重载图"));

            // ── 音频多语言 ──
            host.AddSectionTitle("音频多语言：同一后缀约定，播放时按当前语言取");
            var voiceCaption = host.AddValueDisplay();
            Bag.Bind(loc.Locale.Select(l => loc.Get("l10n/voice-caption", l)), s => voiceCaption.text = s);
            host.AddActionRow("播放当前语言提示音（中文上行双音 / 英文下行双音）", () => PlayVoice(loc).Forget(),
                CodeRef.Here("Bag.Load<AudioClip>($\"l10n-voice_", "按语言取音频"));
            host.AddSubNote("语音 / 配音类资源播放是**瞬时动作**，不需要「换语言重载」——播放时按 `Locale.CurrentValue` 拼 location 取当前语言的 clip 即可（`Bag.Load<AudioClip>` + `IAudioUtility.PlaySfx` 组合，见「音频」章）。");

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("复数 / 性别 / CLDR 规则", "ICU 级复杂度；绝大多数游戏文案用「{0} 个」直译绕开，真需要的项目接专门库，在 Get 的输出上再包一层。");
            host.AddConcept("翻译导出导入工具", "Luban 的 Excel 一列一语言，本身就是翻译工作流（本章 l10n.xlsx 就是活样例）。");
            host.AddConcept("场景静态文本收集", "本框架 UI 全代码驱动（窗口 = View 类），文本入口天然收敛在 BindLocalizedText，没有「散落场景里的 Text 组件」问题。");
            host.AddConcept("字体切换", "不在本接口——由「字体 · 多语言字体链」章的 `MonoLocaleFonts` 承接（订阅 `Locale` 自动切换 fallback 链，ADR-0025），本模块只出信号。");

            host.AddTip("速记：文本源 = ~10 行表 adapter（吃别的服务就 RegisterFactory）；UI 全用 Bag.BindLocalizedText(label, key)；图片 = 后缀约定 + 子 Bag 重载；音频 = 播放时按 CurrentValue 取；设置页 = SetLocale + 存档回灌。深度见 framework-guide 本地化章 / ADR-0024。");
        }

        private static async UniTaskVoid LoadBanner(DisposableBag bag, Image banner, string locale)
        {
            var sprite = await bag.Load<Sprite>($"l10n-banner_{locale}");
            if (sprite != null && !bag.IsDisposed) // 加载途中又切了语言：本次结果作废（句柄已随子 Bag 释放）
                banner.sprite = sprite;
        }

        private async UniTaskVoid PlayVoice(ILocalizationUtility loc)
        {
            var clip = await Bag.Load<AudioClip>($"l10n-voice_{loc.Locale.CurrentValue}");
            if (clip != null)
                this.GetUtility<IAudioUtility>().PlaySfx(clip);
        }
    }
}
