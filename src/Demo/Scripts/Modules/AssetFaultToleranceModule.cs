using System;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·资源容错（<b>框架用法</b>）：资源加载失败时怎么正确兜底。只有一条规则——失败分两套、写法也分两套：
    ///   · 加载期失败（地址无效 / 类型不符）→ <c>Bag.Load</c> 返回 <c>null</c>（不抛）→ null 检查兜底；
    ///   · 初始化失败（CDN 不可达 / 断网）→ 加载方法上抛初始化异常 → try/catch 或先判 <c>InitState</c> + 重试。
    /// 与「资源加载」章互补：那章讲各 API 怎么用（含查询 / 下载 / 清缓存），本章只讲失败时怎么办，<b>不重复其按钮</b>。
    /// </summary>
    public sealed class AssetFaultToleranceModule : DemoModuleBase
    {
        public override string Id => "asset-fault-tolerance";
        public override string Title => "资源容错";
        public override string Category => "能力";
        public override int Order => 21; // 紧跟「资源加载」(Order 20)
        public override string Summary =>
            "失败分两套、写法也分两套：加载期失败（地址无效/类型不符）返回 null → null 检查兜底；初始化失败（CDN 不可达）抛异常 → try/catch 或先判 InitState + 重试。";

        private const string SamplesPackage = "FrameworkSamplesPackage"; // demo 资源所在包（与「资源加载」章一致）
        private const string LogoAddress = "SSFramework-Logo";            // 正常可加载的样例资源
        private const string MissingAddress = "__不存在的地址__";          // manifest 里没有的地址，制造加载失败

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            var samplesState = AssetInitState.Idle; // 由第二节订阅持续刷新，供按钮判断当前是否就绪

            var preview = new VisualElement();
            preview.style.width = 120;
            preview.style.height = 120;
            preview.style.marginBottom = 8;
            ShowPlaceholder(preview, "（预览区）");

            // ── 总览：一条规则 ──
            host.AddSectionTitle("一条规则：预期内的缺失给 null，系统性失败给异常");
            host.AddTable(
                new[] { "失败类型", "典型场景", "框架行为", "你该怎么写" },
                new[] { "加载期失败", "地址不在 manifest / 类型不符 / 空地址", "`Bag.Load` 返回 `null`（不抛）+ 日志", "`null` 检查 + 兜底" },
                new[] { "初始化失败", "包初始化失败：CDN 不可达 / 断网 / 502", "加载方法内部 `EnsureInitialized` 上抛异常", "`try/catch`，或先判 `InitState` 再加载" });
            host.AddNote("记忆点：包一旦 `Ready`，`Bag.Load` 只会返回 `null`（资源级问题）；会抛只发生在「init 还没成功你就加载」——它在提醒你先等资源系统就绪。所以真实项目 loading 界面先 await `EnsureInitialized` / 等 `InitState=Ready`，进主流程后 `Load` 基本只需 `null` 检查。下面两节分别演示这两套。");

            // ── 一、加载期失败 → null ──
            host.AddSectionTitle("一、加载期失败：Bag.Load 返回 null（不抛）");
            host.Content.Add(preview);
            var nullLabel = host.AddValueDisplay("点下面任一按钮制造加载失败，看兜底");
            host.AddActionRow("加载不存在的地址", async () =>
            {
                try
                {
                    var sprite = await Bag.Load<Sprite>(SamplesPackage, MissingAddress);
                    ShowPlaceholder(preview, "占位图");
                    nullLabel.text = sprite == null
                        ? "地址不在 manifest → Load 返回 null（不抛）→ 用占位图兜底，不崩。"
                        : $"意外加载到了：{sprite.name}";
                }
                catch (Exception e) { ShowPlaceholder(preview, "占位图"); nullLabel.text = "抛异常了——多半是资源系统没就绪，见下一节。"; Debug.LogException(e); }
            }, CodeRef.Here("Bag.Load<Sprite>(SamplesPackage, MissingAddress)", "地址无效 → null"));
            host.AddActionRow("把图片当 AudioClip 加载（类型不符）", async () =>
            {
                try
                {
                    var clip = await Bag.Load<AudioClip>(SamplesPackage, LogoAddress);
                    nullLabel.text = clip == null
                        ? "类型不符 → Load 返回 null（控制台另有一条 error 说明），同样 null 检查兜底。"
                        : $"意外得到 AudioClip：{clip.name}";
                }
                catch (Exception e) { nullLabel.text = "抛异常了——多半是资源系统没就绪，见下一节。"; Debug.LogException(e); }
            }, CodeRef.Here("Bag.Load<AudioClip>(SamplesPackage, LogoAddress)", "类型不符 → null"));
            host.AddNote("地址无效 / 类型不符 / 空地址都走同一条：`Load` 返回 `null` + 打日志，业务 `null` 检查后用占位资源 / 默认值兜底即可，这一类「不需要 try/catch」。想在加载前就拦掉无效地址，用 `CheckLocationValid` 预检（在「资源加载」章·查询，本章不重复）。");

            // ── 二、初始化失败 → 抛异常 + 重试 ──
            host.AddSectionTitle("二、初始化失败：加载方法抛异常 + RetryInitialize 重试");
            // 本节独立的预览框：不复用上一节那个，免得点本节「尝试加载」、图却显示到上一节去。
            var loadPreview = new VisualElement();
            loadPreview.style.width = 120;
            loadPreview.style.height = 120;
            loadPreview.style.marginBottom = 8;
            ShowPlaceholder(loadPreview, "（预览区）");
            host.Content.Add(loadPreview);
            var stateBadge = new Label { text = "（订阅样例包初始化状态…）" };
            stateBadge.AddToClassList("demo-badge");
            host.Content.Add(stateBadge);
            // 订阅样例包 InitState：失败时当「可操作引导」，并把当前状态记进 samplesState 供按钮判断。订阅即得当前值（R3 内置）。
            Bag.Subscribe(asset.GetInitState(SamplesPackage), s =>
            {
                samplesState = s;
                stateBadge.RemoveFromClassList("demo-badge--yes");
                stateBadge.RemoveFromClassList("demo-badge--no");
                if (s == AssetInitState.Ready) stateBadge.AddToClassList("demo-badge--yes");
                else if (s == AssetInitState.Failed) stateBadge.AddToClassList("demo-badge--no");
                stateBadge.text = s == AssetInitState.Failed
                    ? $"样例包初始化失败（{asset.CurrentPlayMode}）：Host 需先起本地 CDN 服务且端口一致，或改回 EditorSimulate。修好后点下面「重试初始化」。"
                    : $"样例包初始化：{s}（{asset.CurrentPlayMode}）";
            });
            var initLabel = host.AddValueDisplay();
#if UNITY_EDITOR
            // 白盒：这个按钮只切「模拟断网」开关这一个操作。开关是 RP<bool>——按钮文字订阅它，
            // 所以无论点按钮、还是直接在 AssetUtility 的 Inspector 勾选，文字都实时同步。是否生效由你手动点「重新初始化」触发。
            Button offlineBtn = null;
            offlineBtn = host.AddActionRow("", () =>
                asset.SetSimulateOffline(!asset.SimulateOffline.CurrentValue),
                CodeRef.Here("asset.SetSimulateOffline(...)", "切换模拟断网"));
            Bag.Subscribe(asset.SimulateOffline, on =>
                offlineBtn.text = $"模拟断网：{(on ? "开" : "关")}（点击切换，仅 Host/Web）");
#endif
            host.AddActionRow("重新初始化（RetryInitialize）", async () =>
            {
                initLabel.text = "初始化中…";
                await asset.RetryInitialize(SamplesPackage); // 不抛，结果回写 InitState（徽标 + samplesState 跟着变）
                initLabel.text = samplesState == AssetInitState.Ready
                    ? "已 Ready ✓ 可正常加载。（包一旦 Ready，RetryInitialize 即幂等空操作——运行时再开「模拟断网」也不会回退，见下方说明。）"
                    : $"初始化结果：{samplesState}。要复现 Failed，须在 Play 前就于 Inspector 开「模拟断网」让样例包从一开始拉不到远端——看徽标 / 控制台。";
            }, CodeRef.Here("asset.RetryInitialize(SamplesPackage)", "重新初始化"));
            host.AddActionRow("尝试加载（init 失败时会抛，已 try/catch）", async () =>
            {
                try
                {
                    var sprite = await Bag.Load<Sprite>(SamplesPackage, LogoAddress);
                    if (sprite != null) { ShowSprite(loadPreview, sprite); initLabel.text = $"加载成功：{sprite.name}（init 已就绪）"; }
                    else initLabel.text = "返回 null（init 就绪、但地址/类型有问题，属上一节）";
                }
                catch (Exception e)
                {
                    // init 失败时 Load 的真实表现：内部 EnsureInitialized 把 InitError 抛出来，而不是返回 null。
                    initLabel.text = "Load 抛异常 → try/catch 兜住：init 失败时加载方法上抛，不是返回 null。要么这样兜、要么先判 InitState=Ready 再加载。";
                    Debug.LogException(e);
                }
            }, CodeRef.Here("Bag.Load<Sprite>(SamplesPackage, LogoAddress)", "init 失败 → 抛"));
            host.AddNote("init 失败（CDN 不可达 / 断网）时，`Load` / `LoadScene` / `ClearCacheAsync` 内部的 `EnsureInitialized` 会上抛初始化异常——所以这一类要么 `try/catch`、要么先判 `InitState` / `IsInitialized`。`RetryInitialize` 重跑初始化、不抛、结果回写 `InitState`。");
            host.AddSubNote("为什么运行时开「模拟断网」后样例包仍是 `Ready`、还能加载？因为它只拦截新发起的远端请求——已 `Ready` 的包不会回退、已缓存的资源照常加载，此时 `RetryInitialize` 是幂等空操作。要真正复现初始化失败，请在 `AssetUtility` 的 Inspector 里进 Play 前就勾上「模拟断网」，让样例包从一开始就拉不到远端清单；或把运行模式切 `Host` 但不起本地服务。注意：「下载」中途失败不归这套——下载器自带按 `FailedTryAgain` 重试 + 断点续传（见「资源加载」章·下载），业务不用手写重试。");

            // ── 小结 ──
            host.AddSectionTitle("小结：两套写法别用混");
            host.AddConcept("null 检查", "对加载期失败（地址无效 / 类型不符 / 空地址）——`Load` 返回 `null`，兜底即可，不用 `try/catch`。");
            host.AddConcept("try/catch + 重试", "对初始化失败（CDN 不可达）——`Load` 上抛初始化异常；或先判 `InitState`。加载界面用 `RetryInitialize` 重试。");
            host.AddTip("心智：包 Ready 后 Load 不抛、只返 null；会抛 = 你在 init 成功前就加载了。所以先把流程 gate 在「资源系统就绪」上，后面就只需 null 检查。");
        }

        // 预览块显示成"贴图"：清掉占位文案/底色，贴上 sprite。
        private static void ShowSprite(VisualElement preview, Sprite sprite)
        {
            preview.Clear();
            preview.style.backgroundColor = StyleKeyword.None;
            preview.style.backgroundImage = new StyleBackground(sprite);
        }

        // 预览块显示成"占位"：清掉贴图，铺暗色底 + 居中文案。加载失败时的视觉兜底。
        private static void ShowPlaceholder(VisualElement preview, string text)
        {
            preview.style.backgroundImage = StyleKeyword.None;
            preview.style.backgroundColor = new Color(0.18f, 0.13f, 0.13f, 1f);
            preview.Clear();
            var l = new Label(text);
            l.style.flexGrow = 1;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.enableRichText = false;
            preview.Add(l);
        }
    }
}
