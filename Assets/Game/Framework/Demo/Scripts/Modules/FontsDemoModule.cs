using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.Fonts;
using Game.Framework.Localization;
using Game.Framework.UI.Toolkit;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UIElements;
using TextCoreFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·字体：三层字体策略（①精简主字体 + ②locale 补充字体 + ③OS 兜底）实物演示——
    /// 场景挂 <c>MonoLocaleFonts</c> 订阅 Locale，链条写进主字体的 fallback 表，文本渲染自动逐层找字形。
    /// 切语言按钮与「本地化」章共用同一个 <c>ILocalizationUtility</c>。ADR-0025。
    /// </summary>
    public sealed class FontsDemoModule : DemoModuleBase
    {
        public override string Id => "fonts";
        public override string Title => "字体 · 多语言字体链";
        public override string Category => "能力";
        public override int Order => 61;
        public override string Summary =>
            "CJK 全量字库太大：①精简常用字集随包 + ②locale 补充字体 + ③OS 字体运行时兜底，三层都挂在主字体的 " +
            "fallback 表上（MonoLocaleFonts 订阅 Locale 自动切换），业务代码零感知。ADR-0025。";

        private const string Zh = "zh-CN";
        private const string En = "en";

        // 固定中文样例：演示的是「字形覆盖」不是翻译——切到 en 时中文字形不在链上，看清链条按语言切换。
        private const string SampleZh = "你好，世界——中文字形演示 ①②③";

        // demo 是 UNITY_EDITOR 程序集（教学定位），字体资产直接按路径取；真实业务经 Inspector 拖拽在组件上配置。
        private const string ToolkitMainPath = "Assets/Game/Framework/Demo/Res/Fonts/DemoLatin SDF.asset";
        private const string TmpMainPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
        private const string LatinTtfPath = "Assets/TextMesh Pro/Fonts/LiberationSans.ttf";

        private LocaleFontChain _osOnlyChain;
        private GameObject _tmpOverlay;
        private TMP_FontAsset _tmpOsDemoFont; // ③演示用：不在场景链上的独立 TMP 字体（随 Bag 销毁）

        public override void Build(DemoModuleHost host)
        {
            var loc = this.GetUtility<ILocalizationUtility>();
            var toolkitMain = UnityEditor.AssetDatabase.LoadAssetAtPath<TextCoreFontAsset>(ToolkitMainPath);
            if (toolkitMain == null)
            {
                host.AddNote($"没找到 demo 主字体资产：`{ToolkitMainPath}`——请确认 demo 字体资产在工程里。");
                return;
            }

            // ── 定位 ──
            host.AddSectionTitle("定位：砍字库不砍显示——三层字体策略");
            host.AddNote("CJK 全量字库 15~30MB 起步，全量随包不现实；砍了字库，生僻字 / 用户输入又变豆腐块。" +
                "策略 = **①精简常用字集随包（99% 显示量）+ ②locale 补充字体（语言差集）+ ③OS 字体运行时兜底（不可预知文本）**，" +
                "三层都挂在**主字体资产的 fallback 表**上——文本渲染自动逐层找字形，业务代码零感知、零调用。");
            host.AddConcept("为什么写主字体的表、不写全局 settings", "per-font 表在 TMP 与 TextCore 双后端都是 public 可写（对称）；「共享主字体不换、换链上的语言层」正是它的形状。代价：主字体要显式列出，没列的字体不受链管理。");

            // ── 实物接线 ──
            host.AddSectionTitle("实物：场景组件 MonoLocaleFonts（订阅 Locale，零业务调用）");
            host.AddNote("本 demo 场景在根 Context 下挂了 `MonoLocaleFonts`：主字体 = Latin-only 的 LiberationSans" +
                "（TMP / Toolkit 各一份），`zh-CN` 档案 = ②NotoSansSC + ③OS 候选（微软雅黑等），`en` 档案 = 空（Latin 主字体已覆盖）。" +
                "它订阅 `ILocalizationUtility.Locale`（与「本地化」章同一信号），换语言自动重写链条；销毁时还原原始表。");
#if UNITY_EDITOR
            host.AddActionRow("选中场景里的 MonoLocaleFonts（看 Inspector 配置与「当前 fallback 链」诊断）", () =>
            {
                var fonts = Object.FindFirstObjectByType<MonoLocaleFonts>();
                if (fonts != null) DemoEditorNav.PingSceneObject(fonts.gameObject);
            });
#endif

            // ── 切语言 ──
            host.AddSectionTitle("切语言：与「本地化」章同一个开关");
            var localeLabel = host.AddValueDisplay();
            Bag.BindText(localeLabel, loc.Locale, l => $"当前语言：{l}");
            host.AddActionRow("切到中文（SetLocale zh-CN）", () => loc.SetLocale(Zh),
                CodeRef.Here("loc.SetLocale(Zh)", "切中文"));
            host.AddActionRow("切到英文（SetLocale en）", () => loc.SetLocale(En),
                CodeRef.Here("loc.SetLocale(En)", "切英文"));

            // ── Toolkit 侧：② 决定「用谁的字形」 ──
            host.AddSectionTitle("UI Toolkit 侧：② 决定「用谁的字形」");
            var protectedLabel = host.AddValueDisplay(SampleZh + "（这行字体在主字体列表里）");
            SetFont(protectedLabel, toolkitMain);
            // 固定文本不会因换语言被重设——手动踢一脚强制重排版，让链条变化立即可见（本地化文本无需这步：BindLocalizedText 重设 text 自带重排版）。
            Bag.Subscribe(loc.Locale, _ => ForceReshape(protectedLabel, SampleZh + "（这行字体在主字体列表里）"));

            var unprotectedLabel = host.AddValueDisplay(SampleZh + "（对照：这行字体没列进主字体列表）");
            SetFont(unprotectedLabel, CreateRuntimeToolkitLatin("DemoLatin-Unprotected"));

            host.AddNote("Unity 6 的 UI Toolkit 文本引擎**内建 OS 字形兜底**（TextCore `TextSettings` 层，缺字自动查系统字体）——" +
                "所以 Toolkit 侧缺字**不豆腐，但字形随平台走**（Windows 雅黑 / macOS 苹方，排版风格不受控）。" +
                "② 层在 Toolkit 侧的价值 = **把字形拿回自己手里**：链上的品牌字体（本例 NotoSansSC）优先于引擎 OS 兜底。" +
                "切 zh：第一行变 Noto 字形（笔画末端平切、字面更大）；切 en：② 撤下，两行都退到系统字形。",
                CodeRef.Here("SetFont(protectedLabel, toolkitMain)", "两行的字体接线"));

            // ── TMP 侧：真·豆腐块与 ③ ──
            host.AddSectionTitle("TMP（UGUI 侧）：真·豆腐块——②③ 在这里是刚需");
            host.AddNote("TMP **没有**引擎级 OS 兜底：缺字就是豆腐块（□）。浮层第一行用 TMP 主字体（场景链）：" +
                "**切 en → 豆腐块，切 zh → ②Rude NotoSansSC 接住**；第二行用**不在场景链上**的独立字体，演示 ③——" +
                "用下面按钮给它挂 / 撤纯 OS 候选链（不带 ②），看 OS 字体单独接住中文。");
            host.AddActionRow("弹出 / 关闭 TMP 浮层（屏幕左下角两行字）", () => ToggleTmpOverlay(host),
                CodeRef.Here("private void ToggleTmpOverlay", "运行时搭 TMP 浮层"));

            host.AddActionRow("③ 给第二行挂 OS 兜底链（Microsoft YaHei → PingFang SC → Noto Sans CJK SC）", () =>
            {
                if (_tmpOverlay == null) ToggleTmpOverlay(host); // 浮层没开先开，保证看得见效果
                if (_tmpOsDemoFont == null) return;
                if (_osOnlyChain == null || _osOnlyChain.IsDisposed)
                {
                    // 核心类是纯 C#，脱离 Mono 组件也能用——档案只配 ③OS 候选、不配 ②，看兜底单独工作。
                    _osOnlyChain = new LocaleFontChain(new[] { _tmpOsDemoFont }, null, new[]
                    {
                        new LocaleFontProfile(Zh, osFontNames: new[] { "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC" }),
                    });
                    Bag.Add(_osOnlyChain);
                }
                _osOnlyChain.Apply(Zh);
            }, CodeRef.Here("new LocaleFontChain(new[] { _tmpOsDemoFont }", "纯 C# 建链（只配 OS 候选）"));

            host.AddActionRow("③ 还原（Dispose：撤链、销毁运行时 OS 字体资产）", () => _osOnlyChain?.Dispose(),
                CodeRef.Here("_osOnlyChain?.Dispose()", "还原原始表"));

            host.AddNote("③ 层为「用户名 / 聊天 / UGC」这类**不可预知文本**兜底：运行时按族名候选创建动态字体资产" +
                "（找不到试下一个，全失败降级不炸）。⚠ 族名用**英文名**（「微软雅黑」查不到）；候选按目标平台配齐" +
                "（Windows/macOS/Android 各家系统字体不同）。Dispose 还原原始表 + 销毁运行时资产——Editor Play 会话不污染共享字体资产。");

            // ── ① 主字体怎么来 ──
            host.AddSectionTitle("① 主字体怎么来：常用字集菜单 + TMP Font Asset Creator");
            host.AddNote("菜单 **SSFramework/字体/生成常用字集**：扫配置表（xlsx 读 sharedStrings）/ 代码字符串字面量 / 文案文件，" +
                "去重出 charset 文件 → TMP Font Asset Creator 选主字体 ttf + Characters from File 烘焙 static atlas——" +
                "常用字随包秒显，生僻字交给 ②③。demo 图省事直接用 Latin 字体当①，正好让缺字效果可见。");
#if UNITY_EDITOR
            host.AddActionRow("打开常用字集配置（FontCharsetProfile）", () =>
                UnityEditor.EditorApplication.ExecuteMenuItem("SSFramework/字体/常用字集配置 (Charset Profile)"));
#endif

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("全字库随包 / 每语言完整字体", "fallback 链的意义就是共享通用字形、语言层只补差集——全量随包是链条要解决的问题本身。");
            host.AddConcept("运行时字形卸载 / atlas 调优", "动态 atlas 内存策略交 TMP / TextCore 默认，量化出问题再调。");
            host.AddConcept("远程字体下发协议", "字体资产就是普通资源——②层字体放 locale 分包按需下载（「本地化」章的多 package 组合），不需要专门协议。");
            host.AddConcept("每文本粒度换字体", "链条挂在主字体上全局生效；个别文本要专属字体直接在 UI 上指定，那不是「兜底」问题。");

            host.AddTip("速记：主字体显式列进 MonoLocaleFonts（没列不管理）；每语言一份档案（②补充资产 + ③OS 英文族名候选）；" +
                "换语言由 SetLocale 一并驱动、业务零调用；①用「生成常用字集」菜单烘焙。TMP 缺字真豆腐（②③刚需）、" +
                "Toolkit 引擎自带 OS 兜底（②管字形归属）。深度见 framework-guide 字体章 / ADR-0025。");
        }

        /// <summary>Toolkit 侧给单个 Label 指定字体资产（demo 演示用；真实业务通常在 USS / 主题里统一指定）。</summary>
        private static void SetFont(Label label, TextCoreFontAsset font)
        {
            label.style.unityFontDefinition = new StyleFontDefinition(font);
            label.style.whiteSpace = WhiteSpace.Normal;
        }

        /// <summary>
        /// 强制 Toolkit 文本重排版：fallback 表变化不触发已排版文本重查字形，重设 text（附带交替的零宽空格骗过同值检测）即重排。
        /// 本地化文本天然免疫（换语言会重设 text）；只有「固定文本 + 链条变化」的演示场景需要这一脚。
        /// </summary>
        private static void ForceReshape(Label label, string text)
        {
            const char zwsp = (char)0x200B; // 零宽空格：不可见，但足以让 text 与上次不同
            label.text = label.text != null && label.text.EndsWith(zwsp) ? text : text + zwsp;
        }

        /// <summary>用 LiberationSans.ttf 运行时创建 Toolkit 侧 Latin-only 字体资产（随模块 Teardown 销毁，不留资产）。</summary>
        private TextCoreFontAsset CreateRuntimeToolkitLatin(string name)
        {
            var ttf = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(LatinTtfPath);
            var asset = TextCoreFontAsset.CreateFontAsset(ttf);
            asset.name = name;
            Bag.Add(Disposable.Create(() =>
            {
                if (asset == null) return;
                if (asset.material != null) Object.Destroy(asset.material);
                if (asset.atlasTextures != null)
                    foreach (var tex in asset.atlasTextures)
                        if (tex != null) Object.Destroy(tex);
                Object.Destroy(asset);
            }));
            return asset;
        }

        /// <summary>
        /// 左下角 TMP 浮层（两行）：行一用 TMP 主字体（场景链，①②——切语言看豆腐块⇄中文，顺带验证框架的 ForceMeshUpdate 强刷）；
        /// 行二用独立运行时 TMP 字体（不在场景链上），配合 ③ 按钮演示纯 OS 兜底链。
        /// </summary>
        private void ToggleTmpOverlay(DemoModuleHost host)
        {
            if (_tmpOverlay != null)
            {
                Object.Destroy(_tmpOverlay);
                _tmpOverlay = null;
                return;
            }

            var tmpMain = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpMainPath);
            var ttf = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(LatinTtfPath);
            if (tmpMain == null || ttf == null)
            {
                host.AddSubNote($"没找到 TMP 主字体或 Latin ttf（`{TmpMainPath}` / `{LatinTtfPath}`）。");
                return;
            }

            if (_tmpOsDemoFont == null)
            {
                _tmpOsDemoFont = TMP_FontAsset.CreateFontAsset(ttf);
                _tmpOsDemoFont.name = "DemoTmpLatin-OsOnly";
                var osFont = _tmpOsDemoFont;
                Bag.Add(Disposable.Create(() =>
                {
                    if (osFont == null) return;
                    if (osFont.material != null) Object.Destroy(osFont.material);
                    if (osFont.atlasTextures != null)
                        foreach (var tex in osFont.atlasTextures)
                            if (tex != null) Object.Destroy(tex);
                    Object.Destroy(osFont);
                }));
            }

            _tmpOverlay = new GameObject("FontsDemo TMP Overlay");
            Bag.Add(Disposable.Create(() =>
            {
                if (_tmpOverlay != null) Object.Destroy(_tmpOverlay);
                _tmpOverlay = null;
            }));
            var canvas = _tmpOverlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200; // 盖在 demo 的 UI Toolkit 外壳之上（Overlay 才能与 Toolkit 同屏）

            CreateOverlayLine(tmpMain, SampleZh + "  ← 行一：TMP 主字体（场景链 ①②，切语言看它变）", 64);
            CreateOverlayLine(_tmpOsDemoFont, SampleZh + "  ← 行二：不在场景链上（用 ③ 按钮挂 OS 兜底链）", 16);
        }

        private void CreateOverlayLine(TMP_FontAsset font, string text, float y)
        {
            var go = new GameObject("TMP Line");
            go.transform.SetParent(_tmpOverlay.transform, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = font;
            tmp.fontSize = 24;
            tmp.text = text;
            var rect = tmp.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(16, y);
            rect.sizeDelta = new Vector2(1100, 40);
        }
    }
}
