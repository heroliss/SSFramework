using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.Fonts;
using Game.Framework.Localization;
using Game.Framework.UI.Bridge;
using Game.Framework.UI.Toolkit;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
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
        private TMP_FontAsset _tmpOsDemoFont; // ③演示用：不在场景链上的独立 TMP 字体（随 Bag 销毁）

        public override void Build(DemoModuleHost host)
        {
            var loc = this.GetUtility<ILocalizationUtility>();
            var toolkitMain = UnityEditor.AssetDatabase.LoadAssetAtPath<TextCoreFontAsset>(ToolkitMainPath);
            if (toolkitMain == null)
            {
                host.AddUnavailable(
                    $"工程中找不到本章的 UI Toolkit 主字体资产：`{ToolkitMainPath}`，无法构造可对照的三层 fallback 链。",
                    "恢复 Demo/Res/Fonts 下的 DemoLatin SDF 字体资产，或同步修改 `ToolkitMainPath` 指向等价的 Latin-only TextCore 字体。",
                    "资产恢复后重新进入本章即可观察字形切换；恢复前可先阅读“本地化”章理解驱动字体链切换的 Locale 状态。",
                    CodeRef.Here("private const string ToolkitMainPath", "主字体资产接线"));
                return;
            }

            // ── 定位 ──
            host.AddPositioning("砍字库不砍显示——三层字体策略");
            host.AddNote("CJK 全量字库 15~30MB 起步，全量随包不现实；砍了字库，生僻字 / 用户输入又变豆腐块。策略是把字形分**三层**，都挂在**主字体资产的 fallback 表**上——文本渲染自动逐层找字形，业务代码零感知、零调用：");
            host.AddConcept("① 精简主字体（随包）", "常用字集烘焙进随包的主字体，覆盖约 99% 显示量：秒显、体积可控。");
            host.AddConcept("② locale 补充字体", "每种语言补自己的差集字形（如中文的 NotoSansSC），挂进 fallback 表，换语言自动切换。");
            host.AddConcept("③ OS 字体兜底（运行时）", "按系统字体族名运行时建动态字体，接住不可预知文本（用户名 / 聊天 / UGC）；找不到降级不炸。");
            host.AddConcept("为什么写主字体的表、不写全局 settings", "per-font 表在 TMP 与 TextCore 双后端都 public 可写（对称）；「共享主字体不换、换链上的语言层」正是它的形状。代价：主字体要显式列出，没列的不受链管理。");

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

            // ── TMP 侧：真·豆腐块与 ③（经 RenderTexture 桥内联嵌入，不再是浮层）──
            host.AddSectionTitle("TMP（UGUI 侧）：真·豆腐块——②③ 在这里是刚需");
            host.AddNote("TMP **没有**引擎级 OS 兜底：缺字就是豆腐块（□）。下面是一块**内联嵌入**的 TMP 样本卡（经 RenderTexture 桥嵌进本章内容、随章滚动，不再是浮在角落的浮层——TMP 是 UGUI/mesh 渲染塞不进 VisualElement，桥把它渲进纹理当 Toolkit 内容显示，见「UI 融合」章）：" +
                "**第 1 行**用 TMP 主字体（场景链 ①②）——**切 en → 豆腐块，切 zh → ②NotoSansSC 接住**；" +
                "**第 2 行**用**不在场景链上**的独立字体，演示 ③——用下面按钮给它挂 / 撤纯 OS 候选链（不带 ②），看 OS 字体单独接住中文。",
                CodeRef.Here("void BuildInlineTmpSample", "内联搭 TMP 样本（嵌入桥）"));
            BuildInlineTmpSample(host);

            host.AddActionRow("③ 给第 2 行挂 OS 兜底链（Microsoft YaHei → PingFang SC → Noto Sans CJK SC）", () =>
            {
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
            host.AddSectionTitle("① 主字体怎么来：字集工作台 + TMP Font Asset Creator");
            host.AddNote("工作台 **SSFramework/代码生成/字体字集**：扫配置表（xlsx 读 sharedStrings）/ 代码字符串字面量 / 文案文件，" +
                "去重出 charset 文件 → TMP Font Asset Creator 选主字体 ttf + Characters from File 烘焙 static atlas——" +
                "常用字随包秒显，生僻字交给 ②③。demo 图省事直接用 Latin 字体当①，正好让缺字效果可见。");
#if UNITY_EDITOR
            host.AddActionRow("打开常用字集配置（FontCharsetProfile）", () =>
                UnityEditor.EditorApplication.ExecuteMenuItem("SSFramework/代码生成/字体字集"));
#endif

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("全字库随包 / 每语言完整字体", "fallback 链的意义就是共享通用字形、语言层只补差集——全量随包是链条要解决的问题本身。");
            host.AddConcept("运行时字形卸载 / atlas 调优", "动态 atlas 内存策略交 TMP / TextCore 默认，量化出问题再调。");
            host.AddConcept("远程字体下发协议", "字体资产就是普通资源——②层字体放 locale 分包按需下载（「本地化」章的多 package 组合），不需要专门协议。");
            host.AddConcept("每文本粒度换字体", "链条挂在主字体上全局生效；个别文本要专属字体直接在 UI 上指定，那不是「兜底」问题。");

            host.AddTip("速记：主字体显式列进 MonoLocaleFonts（没列不管理）；每语言一份档案（②补充资产 + ③OS 英文族名候选）；" +
                "换语言由 SetLocale 一并驱动、业务零调用；①在字体字集工作台生成 charset 后烘焙。TMP 缺字真豆腐（②③刚需）、" +
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
        /// 内联 TMP 样本卡（两行）：经 RenderTexture 桥嵌进本章内容流（不再是浮层）。第 1 行用 TMP 主字体（场景链 ①②——
        /// 切语言看豆腐块⇄中文，靠 MonoLocaleFonts + 本处 locale 订阅 ForceMeshUpdate 强刷）；第 2 行用独立运行时字体，配合 ③ 演示纯 OS 兜底链。
        /// </summary>
        private void BuildInlineTmpSample(DemoModuleHost host)
        {
            var loc = this.GetUtility<ILocalizationUtility>();
            var tmpMain = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpMainPath);
            var ttf = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(LatinTtfPath);
            var embed = FindFontsEmbed();
            if (embed == null || tmpMain == null || ttf == null)
            {
                host.AddSubNote($"场景没挂字体嵌入宿主 `UGuiEmbedFontsHost`，或缺 TMP 字体 / Latin ttf（`{TmpMainPath}` / `{LatinTtfPath}`）——跳过内联 TMP 样本。");
                return;
            }

            EnsureOsDemoFont(ttf);

            var root = embed.EnsureContentRoot();
            ClearChildren(root); // 每次进章重建，先清旧

            var bgGo = new GameObject("Backdrop", typeof(RectTransform));
            bgGo.transform.SetParent(root, false);
            var bgRt = (RectTransform)bgGo.transform;
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one; bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            bgGo.AddComponent<RawImage>().color = new Color(0.05f, 0.06f, 0.09f, 1f);

            // 标题行用 ASCII（避免解释文字被 Latin 主字体渲成豆腐——本章正演示这件事）
            AddSampleLine(root, tmpMain, "TMP sample  (line 1: main / line 2: standalone)", new Vector2(0.5f, 0.80f), 15f);
            var row1 = AddSampleLine(root, tmpMain, "1)  " + SampleZh, new Vector2(0.5f, 0.50f), 26f);
            var row2 = AddSampleLine(root, _tmpOsDemoFont, "2)  " + SampleZh, new Vector2(0.5f, 0.20f), 26f);

            var sample = new RenderTextureElement();
            sample.style.height = 150;
            sample.style.marginTop = 6;
            sample.style.marginBottom = 6;
            host.Content.Add(sample);
            embed.Bind(sample);

            // 固定文本：链条变化不自动重排，切语言时强刷这两行重新查 fallback 字形（本地化文本天然免疫、无需此步）。
            Bag.Subscribe(loc.Locale, _ =>
            {
                if (row1 != null) row1.ForceMeshUpdate(false, true);
                if (row2 != null) row2.ForceMeshUpdate(false, true);
            });

            Bag.Add(Disposable.Create(() =>
            {
                embed.Unbind();
                ClearChildren(root);
            }));
        }

        // 运行时创建 ③演示用独立 TMP 字体（不在场景链上，随 Bag 销毁）。
        private void EnsureOsDemoFont(Font ttf)
        {
            if (_tmpOsDemoFont != null) return;
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

        // 往嵌入桥的托管 Canvas 加一行居中 TMP，返回它（供 locale 强刷）。
        private static TMP_Text AddSampleLine(RectTransform root, TMP_FontAsset font, string text, Vector2 anchor, float fontSize)
        {
            var go = new GameObject("TMP Line", typeof(RectTransform));
            go.transform.SetParent(root, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = font;
            tmp.fontSize = fontSize;
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            var rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(620f, 46f);
            return tmp;
        }

        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--) Object.Destroy(root.GetChild(i).gameObject);
        }

        private static MonoUGuiEmbed FindFontsEmbed()
        {
            var go = GameObject.Find("UGuiEmbedFontsHost");
            return go != null ? go.GetComponent<MonoUGuiEmbed>() : null;
        }
    }
}
