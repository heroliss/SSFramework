using System.Threading;
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
    /// 能力·本地化：语言与文本源修订共同驱动 UI 自动刷新 + 带可用性语义的 key 查询回退链
    /// （fallbackLocale → 裸 key上屏）+ 文本源接缝。本章文本源是<b>Luban 表 Adapter</b>（最常见用法的实物），
    /// 并带图片 / 音频 per-locale 的实操样板（location 后缀约定 + 响应式重载）。ADR-0024。
    /// </summary>
    public sealed class LocalizationDemoModule : DemoModuleBase
    {
        public override string Id => "localization";
        public override string Title => "本地化 · 多语言";
        public override string Category => "能力";
        public override int Order => 60;
        public override string Summary =>
            "TextRevision 同时覆盖切语言与延迟文本源就绪，BindLocalizedText 会自动重取且不制造假缺失警告。" +
            "Luban 表 Adapter、fallback、图片和音频按 locale 组合均有可运行样板。";

        // locale code 是开放字符串 + 业务常量（与音频组 / 存储 key 同一「常量管理字符串契约」姿势）。
        private const string Zh = "zh-CN";
        private const string En = "en";

        private const string L10NDataDir = "Assets/Game/Framework/Demo/Configs~/Datas";

        /// <summary>
        /// 注册路径：文本源要吃配置表服务（另一个 Utility）——用 <c>RegisterOwnedFactory</c> 让容器解决依赖顺序：
        /// 首次解析（打开本章）时，场景里的配置服务早已注册完成；LocalizationUtility 仍随根 Context 释放。
        /// 不依赖其他服务的源（如字典源）直接 RegisterOwned 即可（见 LocalizationTests）。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterOwnedFactory(
                c => new LocalizationUtility(
                    new LubanTextSource((IConfigUtility<Tables>)c.Resolve(typeof(IConfigUtility<Tables>))),
                    initialLocale: Zh, fallbackLocale: Zh),
                typeof(ILocalizationUtility));
        }

        /// <summary>
        /// 最常见用法的实物：~10 行包 Luban 表（TbL10N 一行一 key、一列一语言）。
        /// 配置表异步加载，就绪前 Lookup 返回 Unavailable；State 变化发失效信号，让既有绑定在同一语言下重取。
        /// Ready 后翻译列留空才是 Missing，交给框架统一走 fallback 链与缺失报告。
        /// </summary>
        private sealed class LubanTextSource : ILocalizedTextSource
        {
            private readonly IConfigUtility<Tables> _config;

            public LubanTextSource(IConfigUtility<Tables> config) => _config = config;

            public Observable<Unit> Invalidated => _config.State.Select(_ => Unit.Default);

            public LocalizedTextLookupStatus Lookup(string locale, string key, out string text)
            {
                text = null;
                if (_config.State.CurrentValue != ConfigInitState.Ready || _config.Tables == null)
                    return LocalizedTextLookupStatus.Unavailable;

                var row = _config.Tables.TbL10N.GetOrDefault(key);
                if (row == null) return LocalizedTextLookupStatus.Missing;
                text = locale switch { Zh => row.ZhCn, En => row.En, _ => null };
                return string.IsNullOrEmpty(text)
                    ? LocalizedTextLookupStatus.Missing
                    : LocalizedTextLookupStatus.Found;
            }
        }

        /// <summary>专供本章现场演示 Unavailable → Found；生产 Adapter 通常把配置/远端表状态映射为同一契约。</summary>
        private sealed class DelayedTextSource : ILocalizedTextSource
        {
            private readonly Subject<Unit> _invalidated = new();
            private bool _available;
            private string _text;

            public Observable<Unit> Invalidated => _invalidated;

            public LocalizedTextLookupStatus Lookup(string locale, string key, out string text)
            {
                text = null;
                if (!_available) return LocalizedTextLookupStatus.Unavailable;
                if (locale != Zh || key != "demo/delayed-source" || _text == null)
                    return LocalizedTextLookupStatus.Missing;
                text = _text;
                return LocalizedTextLookupStatus.Found;
            }

            public void SetUnavailable()
            {
                _available = false;
                _text = null;
                _invalidated.OnNext(Unit.Default);
            }

            public void SetReady(string text)
            {
                _available = true;
                _text = text;
                _invalidated.OnNext(Unit.Default);
            }
        }

        public override void Build(DemoModuleHost host)
        {
            var loc = this.GetUtility<ILocalizationUtility>();

            // ── 定位 ──
            host.AddPositioning("语言状态、文本查询、内容失效各管一件事");
            host.AddNote("框架把 `Locale`（语言身份）、`Lookup`（Found / Missing / Unavailable）与 `TextRevision`（文本应重取）分开；语言列表、`SystemLanguage` 映射和选择持久化仍归业务。",
                new CodeRef("Assets/Game/Framework/Core/Localization/ILocalizationUtility.cs", "public interface ILocalizationUtility", "本地化入口契约"));
            host.AddSubNote("locale code 是开放字符串 + 业务常量（本章 `Zh = \"zh-CN\"` / `En = \"en\"`）。其他多语言方案也能当源接入；原则只有一条：**别让两个系统都认为自己管着当前语言**。文本 UI 订 `TextRevision`，字体和按语言换图/音频仍只订 `Locale`，避免源就绪时无谓重载资源。");

            // ── 文本源：Luban 表 adapter ──
            host.AddSectionTitle("文本源：Luban 表 adapter（最常见用法的实物）");
            host.AddNote("本章注册的源就是 **`LubanTextSource`——~10 行包 `TbL10N` 表**（Excel 一行一 key、一列一语言，加语言 = 加一列，表本身就是翻译工作流）。它要吃配置表服务，所以用 `RegisterOwnedFactory` 注册：容器在首次解析时替你解决「配置服务先注册、本地化后构造」的依赖顺序，并在根 Context 结束时 Dispose 本地化服务。",
                CodeRef.Here("private sealed class LubanTextSource", "adapter 全文（~10 行）"));
            var fromTableLabel = host.AddValueDisplay();
            Bag.BindLocalizedText(fromTableLabel, "l10n/from-table");
            host.AddActionRow("打开 l10n.xlsx 所在目录（构建期源，~ 目录不导入）", () =>
                UnityEditor.EditorUtility.RevealInFinder($"{L10NDataDir}/l10n.xlsx"),
                CodeRef.Here("builder.RegisterOwnedFactory(", "本章的注册代码（工厂解依赖顺序 + 生命周期）"));
            host.AddSubNote("改表后到「SSFramework/代码生成/配置表 (Luban)」工作台重新生成即可生效。配置未就绪是 `Unavailable`：可暂用裸 key 占位，但不报缺文案；State 到 Ready 会自动重取。小体量 / 测试用内置 `DictionaryLocalizedTextSource`。");

            // ── 延迟源可观察实验 ──
            host.AddSectionTitle("延迟源：不切语言也会自动刷新");
            var delayedSource = new DelayedTextSource();
            var delayedLoc = new LocalizationUtility(delayedSource, Zh);
            Bag.Add(delayedLoc);
            var delayedLabel = host.AddValueDisplay();
            Bag.Subscribe(delayedLoc.TextRevision,
                _ => delayedLabel.text = delayedLoc.Get("demo/delayed-source"));
            host.AddActionRow("让延迟源 Ready（标签应原地变成正文）",
                () => delayedSource.SetReady("延迟文本已到达；没有切语言。"),
                CodeRef.Here("delayedSource.SetReady", "Unavailable → Found"));
            host.AddActionRow("恢复 Unavailable（只占位，不报缺 key）", delayedSource.SetUnavailable);
            host.AddNote("初始标签显示裸 key 只是临时占位；点 Ready 后 Source 发 `Invalidated`，Utility 只推进 `TextRevision`，既有绑定原地重取。`Locale` 没有变化，所以字体链、图片和音频不会被误触发。",
                CodeRef.Here("Bag.Subscribe(delayedLoc.TextRevision", "文本失效绑定"));

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

            // 动态参数 × 文本修订双源组合：同时覆盖换语言与源失效，不需要框架专门 API。
            var clicks = new RP<int>(0);
            Bag.Add(clicks);
            var clicksLabel = host.AddValueDisplay();
            Bag.Bind(clicks.CombineLatest(loc.TextRevision, (c, _) => loc.Get("demo/clicks", c)),
                s => clicksLabel.text = s);
            host.AddActionRow("点我 +1（动态参数 × 文本修订双源）", () => clicks.Value++,
                CodeRef.Here("clicks.CombineLatest(loc.TextRevision", "双源组合"));

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
            Bag.Bind(loc.TextRevision.Select(_ => loc.Get("l10n/banner-caption", loc.Locale.CurrentValue)),
                s => bannerCaption.text = s);

            var banner = new Image { scaleMode = ScaleMode.ScaleToFit };
            banner.style.height = 64;
            banner.style.marginTop = 4;
            banner.style.marginBottom = 4;
            host.Content.Add(banner);

            var bannerBag = Bag.CreateChild();
            Bag.Subscribe(loc.Locale, l =>
            {
                // 换语言换图 = 释放旧句柄 + 按新 locale 重载（子 Bag 重建见用户手册「AssetReference」章）。
                bannerBag.Dispose();
                bannerBag = Bag.CreateChild();
                LoadBanner(bannerBag, banner, l).Forget();
            });
            host.AddNote("图按 **location 后缀约定**命名：`l10n-banner_zh-CN` / `l10n-banner_en`（YooAsset 按文件名寻址，放收集目录即可）；换语言 = `Bag.Subscribe(loc.Locale, ...)` 里 Dispose 旧子 Bag → 按新 locale `Load<Sprite>`。**框架刻意零专门 API**——这就是资源系统 + 响应式的一行组合；大体量项目按 locale 分包（YooAsset 多 package）同理，只是多一层「locale → 包名」映射。",
                CodeRef.Here("LoadBanner(bannerBag, banner, l)", "换语言重载图"));

            // ── 音频多语言 ──
            host.AddSectionTitle("音频多语言：同一后缀约定，播放时按当前语言取");
            var voiceCaption = host.AddValueDisplay();
            Bag.Bind(loc.TextRevision.Select(_ => loc.Get("l10n/voice-caption", loc.Locale.CurrentValue)),
                s => voiceCaption.text = s);
            host.AddAsyncActionRow("播放当前语言提示音（中文上行双音 / 英文下行双音）", ct => PlayVoice(loc, ct),
                CodeRef.Here("Bag.Load<AudioClip>($\"l10n-voice_", "按语言取音频"));
            host.AddSubNote("语音 / 配音类资源播放是**瞬时动作**，不需要「换语言重载」——播放时按 `Locale.CurrentValue` 拼 location 取当前语言的 clip 即可（`Bag.Load<AudioClip>` + `IAudioUtility.PlaySfx` 组合，见「音频」章）。");

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("复数 / 性别 / CLDR 规则", "ICU 级复杂度；绝大多数游戏文案用「{0} 个」直译绕开，真需要的项目接专门库，在 Get 的输出上再包一层。");
            host.AddConcept("翻译导出导入工具", "Luban 的 Excel 一列一语言，本身就是翻译工作流（本章 l10n.xlsx 就是活样例）。");
            host.AddConcept("场景静态文本收集", "本框架 UI 全代码驱动（窗口 = View 类），文本入口天然收敛在 BindLocalizedText，没有「散落场景里的 Text 组件」问题。");
            host.AddConcept("字体切换", "不在本接口——由「字体 · 多语言字体链」章的 `MonoLocaleFonts` 承接（订阅 `Locale` 自动切换 fallback 链，ADR-0025），本模块只出信号。");

            host.AddTip("速记：Source 用 Lookup 区分 Unavailable / Missing 并在答案变化时发 Invalidated；文本 UI 订 TextRevision，字体/图片/音频订 Locale；设置页仍是 SetLocale + 存档回灌。深度见 framework-guide 本地化章 / ADR-0024 / ADR-0035。");
        }

        private static async UniTaskVoid LoadBanner(DisposableBag bag, Image banner, string locale)
        {
            var sprite = await bag.Load<Sprite>($"l10n-banner_{locale}");
            if (sprite != null && !bag.IsDisposed) // 加载途中又切了语言：本次结果作废（句柄已随子 Bag 释放）
                banner.sprite = sprite;
        }

        private async UniTask PlayVoice(ILocalizationUtility loc, CancellationToken ct)
        {
            var clip = await Bag.Load<AudioClip>($"l10n-voice_{loc.Locale.CurrentValue}", ct);
            if (clip != null)
                this.GetUtility<IAudioUtility>().PlaySfx(clip);
        }
    }
}
