using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·资源加载（<b>框架用法</b>）：只讲与底层库无关的框架资源 API——Bag.Load 借资源随宿主自动释放、
    /// AssetReference 拖拽自动绑定、ScriptableObject 配置加载 + 一键绑定、查询与下载、显式包名加载、清缓存，
    /// 以及加载失败的两套兜底（加载期返回 null / 初始化失败抛异常）。
    /// 当前默认后端 YooAsset 的底层原理（清单 / 目录 / 构建管线 / CDN / Host 流程）在「YooAsset · 底层实现」章。
    /// </summary>
    public sealed class AssetLoadingModule : DemoModuleBase
    {
        public override string Id => "asset-loading";
        public override string Title => "资源加载";
        public override string Category => "能力";
        public override int Order => 20;
        public override string Summary =>
            "框架统一的资源入口，与底层库解耦：Bag.Load 借资源随宿主释放、AssetReference 拖拽自动绑定、SO 配置加载 + 一键绑定、查询/下载/清缓存、显式包名加载。底层 YooAsset 原理见「YooAsset · 底层实现」章。";

        // demo 资源都在 FrameworkSamplesPackage（见 collector）；地址 = 文件名（AddressByFileName 规则）。
        private const string LogoAddress = "SSFramework-Logo";
        private const string ConfigAddress = "DemoAssetConfig";
        private const string SamplesPackage = "FrameworkSamplesPackage";
        private const string MissingAddress = "__不存在的地址__"; // manifest 里没有的地址，用于演示加载期失败 → null

        // collector 给 demo 资源打的 tag（FrameworkDemoGroup 的 AssetTags=framework-demo）；tag 下载器按它统计需下载的 bundle。
        private const string DemoTag = "framework-demo";

        // 这两组按钮会分别共享同一个子 Bag / 下载缓存。闸门必须跟模块实例走，不能是 Build 局部变量：
        // UIDocument 重建会先取消旧 Host、再立即 Build 新 UI，而底层异步取消仍可能需要一小段时间才能收尾。
        // 租约会校验“这一轮 owner”，旧异步续体迟到释放时不会误放行后来进入的新操作。
        private readonly DemoOperationGate _configOperationGate = new();
        private readonly DemoOperationGate _downloadOperationGate = new();

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            var refs = UnityEngine.Object.FindFirstObjectByType<DemoAssetRefs>();
            var settingsModel = UnityEngine.Object.FindFirstObjectByType<AssetSystemConfigModel>();
#if UNITY_EDITOR
            // 模拟断网是共享 AssetUtility 的 Editor 状态，不属于本章。离章时恢复进入前值，避免污染另一资源章节。
            var previousSimulateOffline = asset.SimulateOffline.CurrentValue;
            Bag.Add(Disposable.Create(() => asset.SetSimulateOffline(previousSimulateOffline)));
#endif

            // ── 定位 ──
            host.AddPositioning("框架无关的资源 API，借了随宿主自动放");
            host.AddNote("只讲与底层库无关的框架资源 API：`Bag.Load<T>(location)` 按地址加载、句柄进 `Bag` 随宿主自动释放；`AssetReference` Inspector 拖引用、Awake 自动绑定；查询 / 下载 / 清缓存一条龙。底层 YooAsset 机制在「YooAsset · 底层实现」章。");

            // ── 1. 初始化与状态 ──
            host.AddSectionTitle("初始化与状态");
            var stateLabel = host.AddValueDisplay("", CodeRef.Here("asset.InitState", "订阅初始化状态"));
            var defaultState = AssetInitState.Idle; // 由下面订阅持续刷新，供「初始化失败与重试」节的按钮判断当前是否就绪
            // InitState 是状态流，订阅即得当前值；切走本章 Bag.Dispose 时自动退订。启动 loading 界面就订阅它驱动。
            // Failed 时把它转成可操作的引导（而不是只显示 Failed）——常见于「切了 Host/Offline 但没先构建」。
            Bag.Subscribe(asset.InitState, s =>
            {
                defaultState = s;
                stateLabel.text = s == AssetInitState.Failed
                    ? $"默认包初始化失败（运行模式：{asset.CurrentPlayMode}）：Host/Offline 需先构建资源。" +
                      "请用菜单 SSFramework/资源构建 依次「1 构建资源包 → 2 部署 → 3 启动本地 CDN 服务」后重进 Play，或改回 EditorSimulate（免构建）。底层见「YooAsset · 底层实现」章；失败的正确兜底/重试见下方「初始化失败与重试」。"
                    : $"默认包初始化：{s}　｜　运行模式：{asset.CurrentPlayMode}";
            });
#if UNITY_EDITOR
            host.AddActionRow("定位资源系统配置节点（AssetSystem）", () =>
            {
                if (settingsModel != null) DemoEditorNav.PingSceneObject(settingsModel.gameObject);
            });
#endif
            host.AddNote("资源系统是 MVCS 三层：`AssetSystemConfigModel`（配置：默认包 / PlayMode / CDN）→ `AssetInitSystem`（进游戏逐包初始化）→ `AssetUtility`（加载 API），挂在同一 `Context` 节点（上面按钮可定位）。业务只经 `this.GetUtility<IAssetUtility>()` / `Bag.Load` 访问。");
            host.AddSubNote("初始化**已经触发**时，`Bag.Load` 会等待同一个 Pending / Initializing attempt 到终态；但包还在 `Idle` 时会 fail-fast，不会替业务擅自联网。要么为该包开启自动初始化，要么先显式 `Initialize` / `Bag.EnsureInitialized()`，再进入正常加载流程。");

            // 默认包自动初始化徽标：自动初始化现在是【按包】配置（每包 AutoInitialize），这里展示默认包当前是否自动初始化。
            var defaultPkg = settingsModel != null ? settingsModel.DefaultPackageName : null;
            var autoInitOn = settingsModel != null && !string.IsNullOrEmpty(defaultPkg)
                             && settingsModel.ShouldAutoInitialize(defaultPkg);
            var autoInitBadge = new Label(autoInitOn
                ? "默认包自动初始化：开 —— 进 Play 即拉版本 / 清单（Host 会联网）"
                : "默认包自动初始化：关 —— 启动不联网，等业务调 Initialize 触发（见下方）");
            autoInitBadge.AddToClassList("demo-badge");
            autoInitBadge.AddToClassList(autoInitOn ? "demo-badge--yes" : "demo-badge--no");
            host.Content.Add(autoInitBadge);
            host.AddSubNote("自动初始化是**按包**的：`AssetSystemConfigModel` 包列表里每个包各有「自动初始化」开关。关掉某包后启动**不碰它的网络**，由业务在合适时机（隐私同意 / 选区 / 流量确认后，或进 DLC 副本时）调下方「初始化失败与重试」的 `Initialize()` 冷启动它——手机端合规启动 / 大型 DLC 懒加载常这么做；把要联网的包全关掉 = 启动前零网络连接。本 demo 的默认包就设了「不自动初始化」，所以本节停在 `Idle`，点下方「初始化」才启动。");

            // ── 1b. 初始化失败与重试（init 失败/未初始化的兜底：抛异常 + Initialize）──
            host.AddSectionTitle("初始化失败与重试：加载方法抛异常 + Initialize");
            var initLabel = host.AddValueDisplay("默认包没自动初始化（或 init 失败）时加载方法会抛（不是返回 null）；下面用 Initialize 冷启动 / 失败后重试，不重启 App。");
#if UNITY_EDITOR
            host.AddExperimentNotice(
                "修改共享 AssetUtility 的 Editor 模拟断网开关，会影响本次 Play 的所有资源章节；已 Ready 的包不会回退。",
                "上方状态流与按钮文字同步变化；只有后续新发起的 Host/Web 请求会被拦截。",
                "再次点击可手动还原；切离本章时也会自动恢复进入本章前的值。");
            // 白盒：这个按钮只切「模拟断网」一个开关。开关是 RP<bool>——按钮文字订阅它，
            // 无论点按钮、还是直接在 AssetUtility 的 Inspector 勾选，文字都实时同步。是否生效要手动点下面「重新初始化」触发。
            Button offlineBtn = null;
            offlineBtn = host.AddExperimentActionRow("模拟断网", () =>
                asset.SetSimulateOffline(!asset.SimulateOffline.CurrentValue),
                CodeRef.Here("asset.SetSimulateOffline(!asset.SimulateOffline", "切换模拟断网"));
            Bag.Subscribe(asset.SimulateOffline, on =>
                offlineBtn.text = $"教学实验 · 模拟断网：{(on ? "开" : "关")}（点击切换，仅 Host/Web）");
#endif
            host.AddExperimentNotice(
                "只在默认包为 Idle 时发起一次加载请求，不修改资源或初始化状态；Pending/Initializing 会等待，不属于 fail-fast 实验。",
                "仅精确匹配“未初始化 + Initialize 指引”的 InvalidOperationException 并就地显示；其他异常继续交给 Host，不能伪装成教学预期。",
                "点击下方 Initialize，等状态变为 Ready 后再进行正常加载。");
            host.AddExperimentAsyncActionRow("未初始化时尝试加载（本地捕获）", async ct =>
            {
                if (defaultState == AssetInitState.Ready)
                {
                    initLabel.text = "默认包已经 Ready，本次 Play 无法再复现未初始化异常；这是正常的幂等状态。重新进入 Play 且先别点 Initialize 可重测。";
                    return;
                }
                if (defaultState == AssetInitState.Pending || defaultState == AssetInitState.Initializing)
                {
                    initLabel.text = $"当前包为 {defaultState}：此时 Load 会等待同一初始化 attempt，而不是 fail-fast。等它到 Ready/Failed 后再决定加载或重试。";
                    return;
                }
                if (defaultState == AssetInitState.Failed)
                {
                    initLabel.text = "当前包已经 Failed，Load 会重新抛出那次初始化的原始失败。错误类型由底层原因决定，本实验不把任意异常吞成“预期”；请点 Initialize 重试。";
                    return;
                }

                try
                {
                    await Bag.Load<Sprite>(LogoAddress, ct);
                    initLabel.text = "加载没有抛异常：包状态可能刚刚转为 Ready，请看上方实时状态。";
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidOperationException e) when (IsExpectedUninitializedFailure(e))
                {
                    initLabel.text = $"[教学预期] 已捕获 {e.GetType().Name}：{e.Message}　下一步点击 Initialize，等 Ready 后再加载。";
                }
            }, CodeRef.Here("catch (InvalidOperationException e) when (IsExpectedUninitializedFailure(e))", "只捕获契约内的未初始化异常"));
            host.AddAsyncActionRow("初始化（Initialize）", async ct =>
            {
                initLabel.text = "初始化中…";
                // 普通初始化失败不抛、结果回写 InitState；调用者取消则保留 OCE，但底层 owner 会继续完成初始化。
                await asset.Initialize(ct: ct);
                initLabel.text = defaultState == AssetInitState.Ready
                    ? "已 Ready ✓ 可正常加载。（包一旦 Ready，Initialize 即幂等空操作——运行时再开「模拟断网」也不会回退，见下方说明。）"
                    : $"初始化结果：{defaultState}。要复现 Failed，须在 Play 前就于 AssetUtility 的 Inspector 开「模拟断网」让默认包从一开始拉不到远端——看上方状态 / 控制台。";
            }, CodeRef.Here("asset.Initialize(ct: ct)", "初始化默认包"));
            host.AddNote("默认包没自动初始化（本 demo 即如此，状态停在 `Idle`）或 init 失败（CDN 不可达 / 断网 → `Failed`）时，`Load` / `LoadScene` / `ClearCache` 内部的 `EnsureInitialized` 都会**上抛**异常——所以这一类要么 `try/catch`、要么先判 `InitState` / `IsInitialized`。`Initialize` 既是**未自动初始化包的冷启动入口**、也是**失败后的重试**：普通失败不抛、结果回写 `InitState`（上方状态会跟着变）；调用者取消只离开等待并保留 OCE，已启动的物理初始化仍由 utility 完成。真实项目里「关掉某包自动初始化 → 同意联网后 Initialize」「CDN 不可达 → 修好网络 → Initialize 重试」都不必重启 App。");
            host.AddSubNote("⚠ 包 `Idle`（既没自动初始化、也没 Initialize 过）时直接 `Load` 它会**抛**「未初始化」异常，不是无限等待——这是刻意的 fail-fast：要加载的包，要么开自动初始化、要么先 `Initialize`。为什么运行时开「模拟断网」后已 `Ready` 的包仍能加载？因为它只拦截新发起的远端请求——已 `Ready` 的包不回退、已缓存的资源照常加载，此时 `Initialize` 是幂等空操作。要复现初始化失败，请在 `AssetUtility` 的 Inspector 进 Play 前就勾「模拟断网」，或切 `Host` 但不起本地服务。注意「下载」失败另说——单文件失败下载器自动重试，但整体最终失败仍会**抛**、要 `try/catch`（详见下方·下载）。");

            // ── 2. 按地址加载（Bag.Load）──
            host.AddSectionTitle("按地址加载：Bag.Load（借资源随宿主自动释放）");
            var spritePreview = NewPreview();
            host.Content.Add(spritePreview);
            var loadLabel = host.AddValueDisplay("点下面按钮加载");
            // 用子 Bag 装本节借来的句柄：基类 Bag 要切走本章才释放，开个子 Bag 就能在「释放」按钮里随手 Dispose，
            // 既演示资源释放、又方便反复测加载（Assets/Game/AGENTS.md「DisposableBag 是默认生命周期入口」的局部子 Bag 用法）。
            var logoBag = Bag.CreateChild();
            host.AddAsyncActionRow("加载 Logo（Sprite）", async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    loadLabel.text = "默认包尚未 Ready。请先点上方 Initialize；正常加载按钮不会混入“未初始化异常”实验。";
                    return;
                }
                var sprite = await logoBag.Load<Sprite>(LogoAddress, ct);
                if (sprite != null)
                {
                    spritePreview.style.backgroundImage = new StyleBackground(sprite);
                    loadLabel.text = $"已加载 Sprite：{sprite.name}（{sprite.rect.width:0}×{sprite.rect.height:0}）";
                }
            }, CodeRef.Here("logoBag.Load<Sprite>(LogoAddress, ct)", "Bag.Load 用法"));
            host.AddActionRow("释放 Logo（Dispose 子 Bag → 句柄释放）", () =>
            {
                // Dispose 子 Bag 释放它托管的全部句柄；再 CreateChild 重建，下次加载用新子 Bag。
                logoBag.Dispose();
                logoBag = Bag.CreateChild();
                // 句柄已随 Dispose 释放；UI 上的图是另一份引用，得清掉显示元素画面才消失（同 §3 释放按钮的注释）。
                spritePreview.style.backgroundImage = StyleKeyword.None;
                loadLabel.text = "已释放本节 Logo 句柄并清空预览。再点「加载 Logo」会重新加载。";
            }, CodeRef.Here("logoBag.Dispose()", "手动释放本节句柄"));
            host.AddAsyncActionRow("卸载内存中无用 bundle（UnloadUnusedAssets）", async ct =>
            {
                // 只卸引用归零的 bundle：要先「释放 Logo」让它引用归零，本按钮才会把它从内存卸掉；仍持有的不受影响。
                await asset.UnloadUnusedAssets(ct);
                ct.ThrowIfCancellationRequested();
                loadLabel.text = "已卸载引用归零的 bundle（释放内存）。顺序：先「释放 Logo」→ 再本按钮才真卸内存；之后磁盘缓存若也清了，再加载就会重新下载 / 读盘。";
            }, CodeRef.Here("asset.UnloadUnusedAssets(ct)", "卸载无用内存 bundle"));
#if UNITY_EDITOR
            host.AddActionRow("定位 Logo 资产（被加载的源资源）", () =>
                DemoEditorNav.PingAsset("Assets/Game/Framework/Branding/SSFramework-Logo.png"));
#endif
            host.AddNote("`Bag.Load<T>(location)` 借来的资源 handle 进 `Bag`，切走本章 `Bag.Dispose` 自动释放，业务不持有句柄。想提前释放某批句柄，就像本节这样开个 `Bag.CreateChild()` 子 Bag 装它们、需要时 `Dispose`（再 `CreateChild` 重建）。`Bag.Load` 是泛型：prefab 用 `GameObject`、场景用 `LoadScene`；`LoadText` / `LoadBytes` 则是**内容直读**——拷出即释放句柄、不进 Bag（文本类资产 .bytes/.txt 等适用）。跨包用带 `packageName` 的重载（见下）。");
            host.AddSubNote("**释放分三层**，清哪层退到哪层：① `Unload` / `Dispose` 释放 handle → 引用归零但 bundle **还在内存**（所以「释放 Logo」后再加载仍秒出）；② `UnloadUnusedAssets` 把零引用 bundle **从内存卸掉**（上面按钮）；③ `ClearCache` 删**磁盘**下载缓存（见下方·下载）。要逼资源真正重新下载：释放 handle → 卸内存 → 清磁盘 → 再 Load。");
            host.AddSubNote("首次 `Load` 为什么卡一下？`Host` 模式下该资源 bundle 没缓存时，`Load` 会**当场按需下载**它（卡顿来源）；`EditorSimulate` / `Offline` 本地读取、不卡。想消除首加载卡顿就**预热**：先用下方·下载的下载器把 bundle 提前缓存好，之后 `Load` 直接命中不卡。");

            // 加载失败 → null（不抛）：地址无效 / 类型不符都走这条，业务 null 检查后兜底即可。
            var nullLabel = host.AddValueDisplay("加载失败时 Bag.Load 返回 null（不抛），业务 null 检查后兜底即可");
            host.AddExperimentNotice(
                "只验证当前请求，不写磁盘、不改变资源配置；前提是默认包已经 Ready。",
                "返回 null 保持玩家流程可兜底，同时 Console 各出现 1 条 Error，暴露地址或类型配置缺陷。",
                "无需恢复；改用有效地址/类型即可。若资源缺失属于正常分支，先用 GetLocationState 预检，避免制造错误日志。");
            host.AddExperimentAsyncActionRow("加载不存在的地址（→ null）", async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    nullLabel.text = "请先 Initialize 到 Ready；否则会测到系统初始化异常，而不是“地址缺失 → null”。";
                    return;
                }
                var sprite = await Bag.Load<Sprite>(MissingAddress, ct);
                nullLabel.text = sprite == null
                    ? "[教学预期] 地址不在 manifest → 返回 null；Console 1 条 Error。玩家流程可用占位资源继续，开发者仍能看到配置缺陷。"
                    : $"意外加载到了：{sprite.name}";
            }, CodeRef.Here("Bag.Load<Sprite>(MissingAddress, ct)", "地址无效 → null"));
            host.AddExperimentAsyncActionRow("把 Logo 当 AudioClip 加载（→ null）", async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    nullLabel.text = "请先 Initialize 到 Ready；否则会测到系统初始化异常，而不是“类型不符 → null”。";
                    return;
                }
                var clip = await Bag.Load<AudioClip>(LogoAddress, ct);
                nullLabel.text = clip == null
                    ? "[教学预期] 类型不符 → 返回 null；Console 1 条 Error。玩家流程可兜底，开发者仍能定位错误类型。"
                    : $"意外得到 AudioClip：{clip.name}";
            }, CodeRef.Here("Bag.Load<AudioClip>(LogoAddress, ct)", "类型不符 → null"));
            host.AddNote("地址无效 / 类型不符 / 空地址都走同一条：`Load` 返回 `null` + 打日志，业务 `null` 检查后用占位资源 / 默认值兜底即可，这一类「不需要 try/catch」。想在加载前就拦掉无效地址，用下方·查询的 `GetLocationState` 预检。这与上一节「init 失败会抛」是两套：init 没就绪才抛，包 `Ready` 后加载只返 `null`。");

            // ── 失败语义小结：两套别用混（demo 后的对照锚点）──
            host.AddSectionTitle("失败语义小结：预期内的缺失给 null，系统性失败给异常");
            host.AddTable(
                new[] { "失败类型", "典型场景", "框架行为", "你该怎么写" },
                new[] { "加载期失败", "地址不在 manifest / 类型不符 / 空地址", "`Bag.Load` 返回 `null`（不抛）+ 日志", "`null` 检查 + 兜底" },
                new[] { "初始化失败", "包初始化失败：CDN 不可达 / 断网 / 502", "加载方法内部 `EnsureInitialized` 上抛异常", "`try/catch`，或先判 `InitState` 再加载" });
            host.AddNote("记忆点：包一旦 `Ready`，`Bag.Load` 只会返回 `null`（资源级问题）；会抛只发生在「init 还没成功你就加载」——它在提醒你先等资源系统就绪。所以真实项目 loading 界面先 await `EnsureInitialized` / 等 `InitState=Ready`，进主流程后 `Load` 基本只需 `null` 检查。上面两节（初始化失败 / 加载失败）分别演示了这两套。");
            host.AddTip("心智：包 Ready 后 Load 不抛、只返 null；会抛 = 你在 init 成功前就加载了。所以先把流程 gate 在「资源系统就绪」上，后面就只需 null 检查。");

            // ── 3. AssetReference（Inspector 拖拽）──
            host.AddSectionTitle("AssetReference：Inspector 拖资源、Awake 自动绑定");
            var refLabel = host.AddValueDisplay();
            if (refs == null)
            {
                refLabel.text = "没找到 DemoAssetRefs";
                host.AddNote("请确认 demo 根节点下挂了 `DemoAssetRefs`，并在 Inspector 拖好了 Logo 引用。");
            }
            else
            {
                refLabel.text = "点下面按钮用拖拽引用加载";
                // 一个可平铺的预览区：单张铺大图、多张铺缩略图、Unload 清空——同一区域，结果就近、不再各搞一个框。
                var refArea = new VisualElement();
                refArea.style.flexDirection = FlexDirection.Row;
                refArea.style.flexWrap = Wrap.Wrap;
                refArea.style.marginBottom = 8;
                host.Content.Add(refArea);

                void ShowSprites(params Sprite[] arr)
                {
                    refArea.Clear();
                    foreach (var s in arr)
                    {
                        if (s == null) continue;
                        var thumb = NewPreview();
                        thumb.style.marginRight = 6;
                        thumb.style.backgroundImage = new StyleBackground(s);
                        refArea.Add(thumb);
                    }
                }

                host.AddAsyncActionRow("Get() 单个 Logo 引用", async ct =>
                {
                    if (defaultState != AssetInitState.Ready)
                    {
                        refLabel.text = "默认包尚未 Ready。请先点上方 Initialize；普通 AssetReference.Get 不应混入未初始化实验。";
                        return;
                    }
                    var sprite = await refs.LogoRef.Get(ct);
                    if (sprite != null) { ShowSprites(sprite); refLabel.text = $"AssetReference.Get() 得到：{sprite.name}"; }
                    else refLabel.text = "LogoRef 未配置（Inspector 拖一张 Sprite 进去）";
                }, CodeRef.Here("refs.LogoRef.Get(ct)", "AssetReference.Get"));
                host.AddAsyncActionRow("GetAll() 批量加载列表", async ct =>
                {
                    if (defaultState != AssetInitState.Ready)
                    {
                        refLabel.text = "默认包尚未 Ready。请先点上方 Initialize，再测试批量引用加载。";
                        return;
                    }
                    var sprites = await refs.LogoList.GetAll(ct);
                    ShowSprites(sprites); // 把加载到的每一张都平铺展示出来
                    refLabel.text = $"AssetReferenceList.GetAll() 并行加载了 {sprites.Length} 张";
                }, CodeRef.Here("refs.LogoList.GetAll(ct)", "批量加载"));
                host.AddActionRow("释放本节图片（Unload / UnloadAll）", () =>
                {
                    refs.LogoRef.Unload();        // 释放 Get() 的单个引用
                    refs.LogoList.UnloadAll();    // 列表多张一键全放
                    // Unload 只释放资源句柄（引用计数→底层可卸载）；UI 上已显示的图是 backgroundImage 另持的一份引用，
                    // 不会因 Unload 自动消失——必须 Clear 掉显示元素，画面才真正清空（也免得界面继续引用可能已被卸载的资源）。
                    refArea.Clear();
                    refLabel.text = "已释放：LogoRef.Unload() + LogoList.UnloadAll()，预览清空（再点 Get / GetAll 重新加载）。";
                }, CodeRef.Here("refs.LogoList.UnloadAll()", "统一释放引用"));
            }
            host.AddNote("`AssetReference` 在 Inspector 直接拖资源（内部存 GUID，业务不碰 GUID）；挂在 `MonoView/Model/System/Utility` 上的字段会在 `Awake` 自动绑定加载器并登记进宿主 `Bag`，宿主销毁统一释放——零样板。`DemoAssetRefs` 就是个真实 `MonoModelBase`，这些引用是它 `Awake` 自动绑好的。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoAssetRefs.cs", "class DemoAssetRefs", "DemoAssetRefs 定义"));
#if UNITY_EDITOR
            host.AddActionRow("定位资源引用配置节点（DemoAssetRefs）", () =>
            {
                if (refs != null) DemoEditorNav.PingSceneObject(refs.gameObject);
            }, new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoAssetRefs.cs", "class DemoAssetRefs", "资源引用节点定义"));
#endif

            // ── 3b. ScriptableObject 配置：加载 + 一键绑定它的引用 ──
            host.AddSectionTitle("ScriptableObject 配置：加载 + Bag.BindAssetReferences");
            var soLabel = host.AddValueDisplay("白盒三步，顺序点：① 加载配置 SO → ② 绑定它的引用 → ③ 取 IconRef");
            var soPreview = NewPreview();
            host.Content.Add(soPreview);
            // 拆三个独立按钮（加载 SO / 绑它内部的引用 / 取引用），闭包持有加载到的 cfg——
            // 拆开正是为了让「SO 必须手动 Bind」这步显形：它不像 MonoXxxBase 字段那样 Awake 自动绑。
            // config SO 用子 Bag 装：方便「释放配置 SO」按钮一次 Dispose 掉它 + 它内部已绑的引用，便于反复重测整个流程。
            DemoAssetConfig loadedConfig = null;
            var configBag = Bag.CreateChild();
            var configReferencesBoundToCurrentBag = false;

            async UniTask RunConfigOperation(CancellationToken ct, Func<CancellationToken, UniTask> operation)
            {
                if (!_configOperationGate.TryEnter(out var lease))
                {
                    soLabel.text = "另一个配置 SO 操作正在进行，请稍候。";
                    return;
                }

                using (lease)
                {
                    await operation(ct);
                }
            }

            host.AddAsyncActionRow("① 加载配置 SO（Bag.Load<DemoAssetConfig>）", chapterCt => RunConfigOperation(chapterCt, async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    soLabel.text = "默认包尚未 Ready。请先点上方 Initialize，再进入配置 SO 的正常三步流程。";
                    return;
                }
                // config SO 像资源一样被加载进来（真实游戏的常见形态：配置也走资源系统下发/热更）。
                // 快照本次请求的 owner：释放按钮或 UI 重建都可能换掉闭包里的 configBag，旧请求不得把结果发布给新 owner。
                var requestBag = configBag;
                var config = await requestBag.Load<DemoAssetConfig>(ConfigAddress, ct);
                ct.ThrowIfCancellationRequested();
                if (requestBag.IsDisposed) return;
                loadedConfig = config;
                configReferencesBoundToCurrentBag = false;
                soLabel.text = loadedConfig != null
                    ? $"已加载配置 {loadedConfig.name}。它内部的 AssetReference 还没归当前子 Bag 管理——点②建立所有权后再点③。"
                    : "加载失败（地址 DemoAssetConfig 在 FrameworkSamplesPackage？）";
            }), CodeRef.Here("requestBag.Load<DemoAssetConfig>(ConfigAddress, ct)", "加载配置 SO"));
            host.AddActionRow("② 绑定它的引用（Bag.BindAssetReferences）", () =>
            {
                if (_configOperationGate.IsEntered) { soLabel.text = "配置 SO 正在加载 / 取引用，请等操作完成后再绑定。"; return; }
                if (loadedConfig == null) { soLabel.text = "请先点①加载配置 SO。"; return; }
                // SO 不是 MonoXxxBase、字段不会自动绑定：一行把它内部所有 AssetReference 绑到本节子 Bag（随它一起释放）。
                configBag.BindAssetReferences(loadedConfig);
                configReferencesBoundToCurrentBag = true;
                soLabel.text = $"已把 {loadedConfig.name} 的全部 AssetReference 绑到本节子 Bag，现在可安全点③取引用。";
            }, CodeRef.Here("configBag.BindAssetReferences(loadedConfig)", "一键绑定 SO 内引用"));
            host.AddAsyncActionRow("③ 取 IconRef（IconRef.Get）", chapterCt => RunConfigOperation(chapterCt, async ct =>
            {
                if (loadedConfig == null) { soLabel.text = "请先点①加载配置 SO。"; return; }
                if (!configReferencesBoundToCurrentBag)
                {
                    soLabel.text = "当前引用尚未归本节子 Bag 管理，请先点②。旧兼容回退可能让未绑定引用“看似能加载”，但不会把 handle 交给本节 Bag 释放，本 Demo 因此拒绝走这条危险路径。";
                    return;
                }
                var config = loadedConfig;
                var ownerBag = configBag;
                var icon = await config.IconRef.Get(ct);
                ct.ThrowIfCancellationRequested();
                if (ownerBag.IsDisposed || !ReferenceEquals(config, loadedConfig)) return;
                if (icon != null)
                {
                    soPreview.style.backgroundImage = new StyleBackground(icon);
                    soLabel.text = $"用 {config.name} 的 IconRef 取到：{icon.name}";
                }
                else soLabel.text = "IconRef 未配置（在 DemoAssetConfig 资产里拖一张 Sprite）。";
            }), CodeRef.Here("config.IconRef.Get(ct)", "取 SO 内引用"));
            host.AddActionRow("释放 IconRef（Unload）", () =>
            {
                if (_configOperationGate.IsEntered) { soLabel.text = "配置 SO 正在加载 / 取引用，请等操作完成后再释放。"; return; }
                if (loadedConfig == null) { soLabel.text = "请先点①加载配置 SO。"; return; }
                loadedConfig.IconRef.Unload();
                // 同理：Unload 只放句柄，清掉显示元素画面才消失。
                soPreview.style.backgroundImage = StyleKeyword.None;
                soLabel.text = "已 IconRef.Unload()，预览清空（再点③重新取）。";
            }, CodeRef.Here("loadedConfig.IconRef.Unload()", "释放 SO 内引用"));
            host.AddActionRow("释放配置 SO（Dispose 子 Bag，重测用）", () =>
            {
                if (_configOperationGate.IsEntered) { soLabel.text = "配置 SO 正在加载 / 取引用，请等操作完成后再释放。"; return; }
                configBag.Dispose();          // 一次释放 config SO 自身 + 已绑定的内部引用
                configBag = Bag.CreateChild();
                loadedConfig = null;
                configReferencesBoundToCurrentBag = false;
                soPreview.style.backgroundImage = StyleKeyword.None;
                soLabel.text = "已释放配置 SO 及当前子 Bag 托管的内部引用。可重新按① → ② → ③重测。";
            }, CodeRef.Here("configBag.Dispose()", "释放配置 SO"));
            host.AddNote("`ScriptableObject` 配置是「被加载的数据资产」，不是 `Model` 层（它常需像资源一样异步加载，无法在启动时注册成 `Model`）。它内部的 `AssetReference` 不会自动绑定（框架刻意不递归 SO），由加载 / 持有它的宿主一行 `Bag.BindAssetReferences`(配置) 把它的全部引用绑到自身生命周期——之后随本章 `Bag` 一起释放。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoAssetConfig.cs", "class DemoAssetConfig", "DemoAssetConfig 定义"));
            host.AddSubNote("多个按钮共用同一个子 `Bag` 时，单按钮防连点还不够：加载 / 绑定 / 取引用 / 释放必须共用一把资源级闸门。否则一个按钮仍可能释放另一个按钮正在使用的 owner；真实界面可用同一流程状态统一置灰整组控件。");
            host.AddSubNote("未显式绑定的旧代码仍可从 `GameContext.Main` 回退加载，但框架会输出 Warning：这只是迁移逃生口，只补加载器、不把 handle 交给当前 `Bag`。调用方必须手动 `Dispose`；新代码应始终由 Mono 自动绑定或调用 `Bag.BindAssetReferences` 建立清晰所有权。");
#if UNITY_EDITOR
            host.AddActionRow("定位 DemoAssetConfig 资产（被加载的配置 SO）", () =>
                DemoEditorNav.PingAsset("Assets/Game/Framework/Demo/Res/DemoAssetConfig.asset"),
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoAssetConfig.cs", "class DemoAssetConfig", "配置 SO 定义"));
#endif

            // ── 4. 查询：资源地址四态快照 ──
            host.AddSectionTitle("查询：一个四态快照替代两次布尔猜测");
            var checkBadgeLabel = new Label("点下面按钮检测");
            checkBadgeLabel.AddToClassList("demo-badge");
            host.Content.Add(checkBadgeLabel);

            void SetCheckBadge(bool good, string text)
            {
                checkBadgeLabel.text = text;
                checkBadgeLabel.RemoveFromClassList("demo-badge--yes");
                checkBadgeLabel.RemoveFromClassList("demo-badge--no");
                checkBadgeLabel.AddToClassList(good ? "demo-badge--yes" : "demo-badge--no");
            }

            host.AddActionRow("GetLocationState(Logo)", () =>
            {
                var locationState = asset.GetLocationState(LogoAddress);
                switch (locationState)
                {
                    case AssetLocationState.AvailableLocally:
                        SetCheckBadge(true, "AvailableLocally ✓　地址有效，资源已内置或已缓存");
                        break;
                    case AssetLocationState.RequiresDownload:
                        SetCheckBadge(false, "RequiresDownload ↓　地址有效，需要从远端下载");
                        break;
                    case AssetLocationState.Invalid:
                        SetCheckBadge(false, "Invalid ✗　空地址或 manifest 中不存在");
                        break;
                    case AssetLocationState.PackageNotReady:
                        SetCheckBadge(false, $"PackageNotReady　当前包状态：{defaultState}");
                        break;
                    default:
                        SetCheckBadge(false, $"未知资源地址状态：{locationState}");
                        break;
                }
            }, CodeRef.Here("var locationState = asset.GetLocationState(LogoAddress)", "资源地址四态快照"));
            host.AddNote("`GetLocationState` 一次返回四种互斥结果：`PackageNotReady`（还不能查）、`Invalid`（地址无效）、`AvailableLocally`（已内置 / 已缓存）、`RequiresDownload`（要从远端拉）。这比「先看初始化，再拼 `CheckLocationValid` 与 `IsNeedDownload` 两个 bool」更难误用；要继续区分未就绪究竟是 Idle、排队、初始化中还是失败，再读 `GetInitState(package)`。`EditorSimulate` / `Offline` 下有效资源通常是 `AvailableLocally`；只有远端模式（`Host` / `Web`）才可能是 `RequiresDownload`，底层见「YooAsset · 底层实现」章。");
            host.AddSubNote("为什么不把初始化细节也塞进这个枚举？资源位置和包生命周期是两个正交概念：前者回答「这份内容现在在哪里」，后者回答「包为何还不能工作」。四态快照只给高频决策所需的最小信息，诊断与重试仍由 `AssetInitState` 负责，避免一个结果类型无限膨胀。");

            // ── 5. 下载与清缓存（三种范围的下载器）──
            host.AddSectionTitle("下载与清缓存：下载器（按 tag / 全部 / 按地址）");
            var dlMode = settingsModel != null ? settingsModel.ActualPlayMode : asset.CurrentPlayMode;
            bool dlIsReal = dlMode == AssetPlayMode.Host || dlMode == AssetPlayMode.Web;
            var modeBadge = new Label(dlIsReal
                ? $"当前：真实下载（{dlMode}，从 CDN 拉）"
                : dlMode == AssetPlayMode.Offline
                    ? "当前：Offline —— 全本地内置，不发生下载"
                    : "当前：EditorSimulate —— 资源全本地，无需下载（要看真实下载进度切 Host）");
            modeBadge.AddToClassList("demo-badge");
            if (dlIsReal) modeBadge.AddToClassList("demo-badge--yes");
            host.Content.Add(modeBadge);
            var progressLabel = host.AddValueDisplay("先点「创建下载器」统计，再点「开始下载」");
            var progressBar = new ProgressBar { lowValue = 0f, highValue = 1f };
            progressBar.style.marginBottom = 8;
            host.Content.Add(progressBar);
            // 白盒：创建下载器与启动下载是两个独立操作，拆开。创建有三种范围——按 tag / 全部 / 按地址，建好后共用下面「开始下载」。
            IAssetDownloader downloader = null;

            // 单个异步按钮只会禁用自己，但这一组按钮共享同一个 downloader 与缓存目录，必须再做资源级互斥。
            // 真实启动器通常用一个流程状态机或整组控件置灰；Demo 用模块级 gate 同时覆盖按钮之间与 UIDocument 重建前后的并发。
            async UniTask RunDownloadOperation(CancellationToken ct, Func<CancellationToken, UniTask> operation)
            {
                if (defaultState != AssetInitState.Ready)
                {
                    progressLabel.text = $"默认包尚未 Ready（当前 {defaultState}）。请先点上方 Initialize；下载与清缓存的正常入口不会混入未初始化异常实验。";
                    return;
                }
                if (!_downloadOperationGate.TryEnter(out var lease))
                {
                    progressLabel.text = "另一个下载 / 缓存操作正在进行，请稍候。";
                    return;
                }

                using (lease)
                {
                    await operation(ct);
                }
            }

            // 复用：把建好的下载器接上进度条 + 写一句统计反馈。「无需下载」是常态（已缓存 / 已内置 / EditorSimulate 全本地 / 没匹配到），TotalCount=0、瞬间完成。
            void BindDownloader(IAssetDownloader d, string scope)
            {
                downloader = d;
                Bag.Subscribe(d.Progress, r =>
                {
                    progressBar.value = r.Progress;
                    progressBar.title = $"{r.Progress:P0}　{r.CurrentSizeMB}/{r.TotalSizeMB} MB";
                });
                progressLabel.text = d.TotalCount == 0
                    ? $"已创建·{scope}：无需下载（0 个，已缓存/已内置/本地）。Host 下想重测先点「清空下载缓存」。"
                    : $"已创建·{scope}：待下载 {d.TotalCount} 个 / {d.TotalBytes / 1048576f:0.00} MB。点「开始下载」。";
            }

            // 下载器从 IAssetUtility 创建（不在 Bag 上）：它是用完即弃的工厂产物、不进 bag 托管——
            // Bag 只收「借出 + 跟随生命周期」的东西（Load / Rent / Spawn / 订阅）。
            host.AddAsyncActionRow("创建下载器·按 tag", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                BindDownloader(asset.CreateTagDownloader(DemoTag), $"tag「{DemoTag}」");
            }), CodeRef.Here("asset.CreateTagDownloader(DemoTag)", "按 tag 下载器"));
            host.AddAsyncActionRow("创建下载器·全部（整包）", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                BindDownloader(asset.CreateAllDownloader(), "全部");
            }), CodeRef.Here("asset.CreateAllDownloader()", "全量下载器"));
            host.AddAsyncActionRow("创建下载器·按地址（Logo）", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                BindDownloader(asset.CreateLocationDownloader(LogoAddress), $"地址「{LogoAddress}」");
            }), CodeRef.Here("asset.CreateLocationDownloader(LogoAddress)", "按地址下载器"));
            host.AddAsyncActionRow("开始下载（Download）", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                var currentDownloader = downloader; // 下载器是创建时快照；整个 await 都只读这一份，避免完成时读到被替换的实例。
                if (currentDownloader == null) { progressLabel.text = "请先点「创建下载器」。"; return; }
                progressLabel.text = "下载中…";
                try
                {
                    await currentDownloader.Download(ct);
                    ct.ThrowIfCancellationRequested();
                    progressLabel.text = $"下载完成 ✓（{currentDownloader.TotalCount} 个 / {currentDownloader.TotalBytes / 1048576f:0.00} MB）";
                }
                catch (OperationCanceledException) { throw; } // 切章取消是正常控制流，交回 Host 静默收口，不能伪装成下载失败。
                catch (Exception e)
                {
                    // Download() 最终失败（自带 FailedTryAgain 重试耗尽 / CDN 不可达）会抛——和 init 失败同属「抛」那套，必须 try/catch。
                    // 重试不是重复点同一个下载器（一次性、失败即重抛），而是「重新创建下载器」再下：已下成功的分片已进缓存会被跳过（断点续传）。
                    progressLabel.text = $"下载失败 ✗：{e.Message}　自动重试已耗尽（网络 / CDN 问题）。重试请重新「创建下载器」再下载（已下分片已缓存、会跳过）。";
                    Debug.LogException(e);
                }
            }), CodeRef.Here("currentDownloader.Download(ct)", "启动下载（失败会抛，已 try/catch）"));

            // 运行时清缓存：清完内存缓存记录同步更新，地址快照立刻变 RequiresDownload，同一次 Play 里就能再测真实下载——免去停 Play。
            host.AddExperimentNotice(
                "前提是默认包已经 Ready；否则按钮会就地提示先 Initialize。操作只删除本地下载缓存，不删除项目源资源；All 清全部，tag/location 缩小范围但仍以 bundle 为最小粒度。",
                "完成后下载器旧快照会作废；Host/Web 下地址可能变为 RequiresDownload，EditorSimulate/Offline 通常仍在本地可用。",
                "重新创建下载器并下载即可恢复缓存；若只为回收旧版本空间，优先使用 Unused。");
            host.AddExperimentAsyncActionRow("清空默认包全部下载缓存", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                // ClearCache 的调用者取消只会离开等待，底层清理仍继续。这里一旦提交就等物理操作真正结束，
                // 让模块级 gate 覆盖完整收尾期；结束后再检查章节令牌，避免向已销毁的 UI 发布结果。
                await asset.ClearCache(AssetCacheClearMode.All, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                downloader = null;  // 老下载器的待下载列表是创建时的快照，缓存清了它不会更新；置空逼重建，否则点「开始下载」会执行旧快照（0 个）瞬间完成。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                var postClearLocationState = asset.GetLocationState(LogoAddress);
                progressLabel.text = $"已清空下载缓存 ✓　GetLocationState(Logo)={postClearLocationState}（远端模式下应为 RequiresDownload）。下载器已重置——请重新点「创建下载器」再「开始下载」才会重新统计。";
            }), CodeRef.Here("asset.ClearCache(AssetCacheClearMode.All, CancellationToken.None)", "运行时清缓存"));
            host.AddExperimentAsyncActionRow("清除无用缓存（Unused）", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                await asset.ClearCache(AssetCacheClearMode.Unused, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                downloader = null;  // 同上：清缓存后下载器快照过期，置空逼重建。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                progressLabel.text = "已清除无用缓存 ✓——只清「不被当前版本清单引用」的旧版本残留 bundle（热更后回收空间用）；单版本 / 没热更过通常无可清。要全清用上面「清空下载缓存」。下载器已重置。";
            }), CodeRef.Here("asset.ClearCache(AssetCacheClearMode.Unused, CancellationToken.None)", "清未使用缓存"));
            host.AddExperimentAsyncActionRow("按 tag 清缓存（本 demo tag）", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                await asset.ClearCacheByTags(new[] { DemoTag }, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                downloader = null;  // 同上：清缓存后下载器快照过期，置空逼重建。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                progressLabel.text = $"已按 tag「{DemoTag}」清缓存 ✓——只清这批 tag 的 bundle，正适合卸载某关卡 / DLC 的资源（其余缓存不动）。下载器已重置，重测请重新「创建下载器」。";
            }), CodeRef.Here("asset.ClearCacheByTags(new[] { DemoTag }, CancellationToken.None)", "按 tag 清缓存"));
            host.AddExperimentAsyncActionRow("按地址清缓存（Logo 所在 bundle）", chapterCt => RunDownloadOperation(chapterCt, async ct =>
            {
                await Bag.EnsureInitialized(ct);
                ct.ThrowIfCancellationRequested();
                await asset.ClearCacheByLocations(new[] { LogoAddress }, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                downloader = null;  // 同上：清缓存后下载器快照过期，置空逼重建。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                progressLabel.text = $"已按地址「{LogoAddress}」清缓存 ✓——点名清这个资源所在的 bundle；注意是 bundle 粒度，同 bundle 的邻居会被连带清。下载器已重置，重测请重新「创建下载器」。";
            }), CodeRef.Here("asset.ClearCacheByLocations(new[] { LogoAddress }, CancellationToken.None)", "按地址清缓存"));
            host.AddNote("下载器有三种范围：`CreateTagDownloader(tags)` 按 tag（某关卡 / DLC 整批）、`CreateAllDownloader()` 全部尚未缓存的 bundle（整包预下）、`CreateLocationDownloader(locations)` 按地址点名（含依赖）——都订阅 `Progress`（R3 状态流）驱动进度条、`Download()` 启动。`ClearCache` 清本地已下载缓存（`All` 全清 / `Unused` 清旧版本），按 tag 清用 `ClearCacheByTags`、按地址清用 `ClearCacheByLocations`，与下载器三种范围一一对应。单文件下载失败由下载器自带按 `AssetSystemConfigModel.FailedTryAgain`（默认 3）重试，业务不必手写重试循环；但**整体最终失败**（重试耗尽 / 持续断网）时 `Download()` 会**抛**——和 init 失败同属「抛」那套，要 `try/catch`（见「开始下载」按钮）。重试靠**重建下载器**再下：已下分片已缓存会被跳过，即断点续传。");
            host.AddSubNote("下载器是「创建那一刻的待下载快照」，不是「下载时去看缺什么补什么」：清缓存并不会更新已建好的下载器，得重新 `CreateTagDownloader` 才会按最新缓存重新统计。所以「清缓存 → 重建下载器 → 开始下载」是固定顺序。`GetLocationState` / `Create*Downloader` 又是同步快照：同包维护正在运行或已排队时会立即提示维护后重试，不会卡住 Unity 主线程，也不会越过维护读中间态。");
            host.AddSubNote("取消还有一条容易漏掉的边界：YooAsset 的下载 / 清理一旦开始就不能安全强停，调用者令牌只让当前等待者离开，进程级 package owner 仍观察到真实终态；无人接收的成功 handle 会释放，后台失败会进日志。因此本 demo 在**提交清理前**响应切章取消；提交后用 `CancellationToken.None` 保持本组业务闸门直到物理清理结束，再检查章节令牌且不更新旧 UI。若产品真要“停止网络流量”，需要另设计显式 Stop/终止契约，不能把等待 OCE 当成底层已停。");
            host.AddSubNote("`ClearCacheByTags` 多 tag 是并集（命中任意一个就清）；`ClearCacheByLocations` 与 tag 清一样都是 bundle 粒度——按地址清会连带同 bundle 的其他资源，想精确隔离要在打包时让该资源独占 bundle。");
            host.AddSubNote("默认 `Load` 未缓存资源时会**当场按需下载**（Host 模式，每包「启用按需下载」默认勾选）。想避免「误 Load 一个资源就自动拖下整批」（典型如大型 DLC）：在 `AssetSystemConfigModel` 的包列表里把该包的「启用按需下载」**取消勾选**，之后 Load 本包未缓存资源**直接失败**（不下载），强制先显式跑下载器（带进度 UI）。按包配置，基础包通常留默认（启用）；仅 Host 模式有意义。");
            host.AddSubNote("下载缓存目录在哪、各清单文件、各 `PlayMode` 的底层差异——见「YooAsset · 底层实现」章。`EditorSimulate` 下资源全本地、不发生真实下载（下载器恒为 0 个）；要看真实下载进度切 `Host`（配本地 CDN 服务，可在构建 profile 里开限速模拟弱网）。本节只演示框架 API 用法。");

            // ── 6. 显式包名加载 ──
            host.AddSectionTitle("显式指定包名（多包项目用它跨包）");
            var crossLabel = host.AddValueDisplay();
            var crossPreview = NewPreview();
            host.Content.Add(crossPreview);
            var crossBag = Bag.CreateChild(); // 用子 Bag 装本节句柄，方便就近释放（同 §2）
            host.AddAsyncActionRow("显式从包名加载（Bag.Load(package, location)）", async ct =>
            {
                var packageState = asset.GetInitState(SamplesPackage).CurrentValue;
                if (packageState != AssetInitState.Ready)
                {
                    crossLabel.text = $"包「{SamplesPackage}」尚未 Ready（当前 {packageState}）。请先 Initialize，再测试显式包名重载。";
                    return;
                }
                var sprite = await crossBag.Load<Sprite>(SamplesPackage, LogoAddress, ct);
                if (sprite != null)
                {
                    crossPreview.style.backgroundImage = new StyleBackground(sprite);
                    crossLabel.text = $"从包「{SamplesPackage}」加载到：{sprite.name}";
                }
                else crossLabel.text = "加载失败";
            }, CodeRef.Here("crossBag.Load<Sprite>(SamplesPackage", "显式包名加载"));
            host.AddActionRow("释放（Dispose 子 Bag）", () =>
            {
                crossBag.Dispose();
                crossBag = Bag.CreateChild();
                // 同理：Dispose 只放句柄，清掉显示元素画面才消失。
                crossPreview.style.backgroundImage = StyleKeyword.None;
                crossLabel.text = "已释放显式包名加载的句柄并清空预览。";
            }, CodeRef.Here("crossBag.Dispose()", "释放本节句柄"));
            host.AddNote("所有加载方法都有带 `packageName` 的重载；多包项目用它从非默认包加载。本 Demo 当前只登记一个 `FrameworkSamplesPackage`，而且它就是默认包，所以这里诚实地演示的是**显式包名重载**，不是伪造一次跨包：当项目再登记 DLC / 关卡包后，传那个包名就是跨包加载。所有包都登记在 `AssetSystemConfigModel`，`Default Package` 只指定省略包名时落到哪一个；子 `Context` 经 `Container` 父级回退共享父级 `AssetUtility`，不必各挂一套。正式项目的包名建议用菜单 `SSFramework/资源构建/生成包名常量代码` 生成常量；Demo 在框架程序集里引用不到业务生成物，所以保留本地 const。");

            // ── 7. 使用路径 / 注册=生命周期 / 解耦 ──
            host.AddSectionTitle("使用路径");
            host.AddConcept("Bag.Load / LoadScene / LoadText", "动态加载：借来的资源进 `Bag`，宿主销毁自动释放，心智同 `Bag.Rent` / `Bag.Spawn`。");
            host.AddConcept("AssetReference", "Inspector 拖拽引用：`MonoXxxBase` 字段 `Awake` 自动绑定 + 入 `Bag`；`ScriptableObject` / 手动创建的 ref 由宿主 `Bag.BindAssetReferences`(对象) 一键绑。");
            host.AddConcept("IAssetUtility", "手动入口：`this.GetUtility<IAssetUtility>()`——查初始化状态、读 `GetLocationState` 四态快照、建下载器、清下载缓存（`ClearCache`）。");

            host.AddSectionTitle("注册 = 生命周期");
            host.AddConcept("三层 Mono", "`AssetSystemConfigModel` + `AssetUtility` + `AssetInitSystem` 挂同一 `Context` 节点，`Awake` 顺序由 `ExecutionOrder` 保证（`Utility` -400 / `Model` -300 / `System` -200）。");
            host.AddNote("框架与底层库解耦：所有 YooAsset 接触面都收口在 `IAssetProvider`。默认实现由 Adapter 在自己的 Assembly 上注册，Core 不知道 `YooAssetProvider` 类型名；换 Addressables / 自研库时删除旧 Adapter、安装一个新实现并注册即可，`AssetUtility` 与业务 API 不改。若同时注册两个后端，框架会明确报冲突而不是按加载顺序猜。当前 YooAsset 后端的底层原理见「YooAsset · 底层实现」章。",
                new CodeRef("Assets/Game/Framework/Asset.Yoo/AssemblyInfo.cs", "DefaultAssetProvider", "默认 Provider 注册属于 Adapter"));

            host.AddTip("约定：动态加载优先 Bag.Load（自动释放）；Inspector 引用优先 AssetReference（自动绑定）；SO/手动 ref 用 Bag.BindAssetReferences；多包项目跨包时用带 packageName 的重载。框架不提供 UnloadPackage——要释放就 Dispose handle / 整 Context 重建。");
        }

        // 120×120 预览框：每个小节各用一个独立实例，加载结果就近显示，不跨小节复用同一个框（免得读者点这节、图却出现在别节而疑惑）。
        private static VisualElement NewPreview(int size = 120)
        {
            var p = new VisualElement();
            p.style.width = size;
            p.style.height = size;
            p.style.marginBottom = 8;
            p.style.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
            return p;
        }

        private static bool IsExpectedUninitializedFailure(InvalidOperationException exception)
        {
            if (exception == null) return false;
            return exception.Message.IndexOf("未初始化", StringComparison.Ordinal) >= 0 &&
                   exception.Message.IndexOf("Initialize", StringComparison.Ordinal) >= 0;
        }
    }
}
