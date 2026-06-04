using System.Collections.Generic;
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
    /// 能力·资源加载（YooAsset）：用真实 API 把资源系统跑一遍——Bag.Load 借资源随宿主自动释放、
    /// AssetReference 拖拽引用自动绑定、订阅初始化状态、是否需下载 / 下载进度（EditorSimulate 模拟）、
    /// 跨包加载，以及一键本地 CDN 看真实远端下载。底层是 MVCS 三层（AssetSystemConfigModel + AssetInitSystem + AssetUtility）。
    /// </summary>
    public sealed class AssetLoadingModule : DemoModuleBase
    {
        public override string Id => "asset-loading";
        public override string Title => "资源加载 · YooAsset";
        public override string Category => "能力";
        public override int Order => 20;
        public override string Summary =>
            "Bag.Load<T> 借资源随宿主自动释放、AssetReference 拖拽引用自动绑定、订阅初始化状态、是否需下载与下载进度、跨包加载，以及一键本地 CDN 看真实远端下载。";

        // demo 资源都在 FrameworkSamplesPackage（见 collector）；地址 = 文件名（AddressByFileName 规则）。
        private const string LogoAddress = "SSFramework-Logo";
        private const string LogoCardAddress = "DemoLogoCard";
        private const string SamplesPackage = "FrameworkSamplesPackage";

        // collector 给 demo 资源打的 tag（AssetBundleCollectorSetting → FrameworkDemoGroup 的 AssetTags=framework-demo）。
        // tag 下载器按它统计需下载的 bundle——必须与 collector 实际 tag 一致，否则匹配 0 个、真实下载永远是 0。
        private const string DemoTag = "framework-demo";

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            var refs = UnityEngine.Object.FindFirstObjectByType<DemoAssetRefs>();
            var settingsModel = UnityEngine.Object.FindFirstObjectByType<AssetSystemConfigModel>();

            // ── 1. 初始化状态 ──
            host.AddSectionTitle("初始化状态：Settings → InitSystem → Utility");
            var stateLabel = host.AddValueDisplay();
            // InitState 是状态流，订阅即得当前值；切走本章 Bag.Dispose 时自动退订。
            Bag.Subscribe(asset.InitState, s => stateLabel.text =
                $"默认包初始化：{s}　｜　运行模式：{asset.CurrentPlayMode}");
            host.AddActionRow("等待初始化完成（EnsureInitialized）", async () =>
            {
                await Bag.EnsureInitialized();
                stateLabel.text = $"默认包初始化：{asset.InitState.CurrentValue}　｜　运行模式：{asset.CurrentPlayMode}";
            }, CodeRef.Here("Bag.EnsureInitialized()", "等待初始化"));
#if UNITY_EDITOR
            host.AddActionRow("定位资源系统配置节点（AssetSystem）", () =>
            {
                if (settingsModel != null) PingSceneObject(settingsModel.gameObject);
            });
#endif
            host.AddNote("资源系统是 MVCS 三层：AssetSystemConfigModel（配置：默认包 / PlayMode / CDN）→ AssetInitSystem（进游戏逐包初始化）→ AssetUtility（加载 API），挂在同一 Context 节点（上面按钮可定位）。业务只经 this.GetUtility<IAssetUtility>() / Bag.Load 访问；Bag.Load 内部自动等初始化完成，无需关心时序。");

            // ── 2. 按地址加载（Bag.Load）──
            host.AddSectionTitle("按地址加载：Bag.Load（借资源随宿主自动释放）");
            var spritePreview = new VisualElement();
            spritePreview.style.width = 120;
            spritePreview.style.height = 120;
            spritePreview.style.marginBottom = 8;
            spritePreview.style.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
            host.Content.Add(spritePreview);
            var loadLabel = host.AddValueDisplay("点下面按钮加载");
            host.AddActionRow("加载 Logo（Sprite）", async () =>
            {
                var sprite = await Bag.Load<Sprite>(LogoAddress);
                if (sprite != null)
                {
                    spritePreview.style.backgroundImage = new StyleBackground(sprite);
                    loadLabel.text = $"已加载 Sprite：{sprite.name}（{sprite.rect.width:0}×{sprite.rect.height:0}）";
                }
            }, CodeRef.Here("Bag.Load<Sprite>(LogoAddress)", "Bag.Load 用法"));
            host.AddNote("Bag.Load<T>(location) 借来的资源 handle 进 Bag，切走本章（Bag.Dispose）自动释放，业务不持有句柄；跨包用带 packageName 的重载。");

            // 加载 GameObject 预制体：演示资源系统不止能加载 Sprite，也能加载整个 prefab。
            // 卡片是屏幕空间 UGUI，Instantiate 到居中 overlay 容器、盖在 UI 之上、点一下销毁——世界空间精灵会被全屏 UI 盖住故不用。
            var spawnedCards = new List<GameObject>();
            var cardLabel = host.AddValueDisplay("点按钮加载 Logo 卡片预制体");

            host.AddActionRow("加载 Logo 卡片（prefab）并实例化（居中、点卡片销毁）", async () =>
            {
                if (refs == null || refs.LogoSpawnRoot == null)
                {
                    cardLabel.text = "没找到居中容器（DemoAssetRefs.LogoSpawnRoot 未接线）";
                    return;
                }
                // 资源系统加载 prefab（Bag 缓存 handle，切走本章自动释放）。
                var cardPrefab = await Bag.Load<GameObject>(LogoCardAddress);
                if (cardPrefab == null)
                {
                    cardLabel.text = "加载失败（地址 DemoLogoCard 在 FrameworkSamplesPackage？）";
                    return;
                }
                var go = Object.Instantiate(cardPrefab, refs.LogoSpawnRoot);
                if (go.transform is RectTransform rt)               // 略微级联偏移，多张不完全叠死、便于分别点击
                    rt.anchoredPosition = new Vector2(spawnedCards.Count % 6 * 26f, spawnedCards.Count % 6 * -26f);
                spawnedCards.Add(go);
                var card = go.GetComponent<DemoLogoCard>();
                if (card != null)
                    card.Clicked += () => { spawnedCards.Remove(go); Object.Destroy(go); cardLabel.text = $"屏幕中央 Logo 卡片：{spawnedCards.Count} 张"; };
                cardLabel.text = $"屏幕中央 Logo 卡片：{spawnedCards.Count} 张";
            }, CodeRef.Here("Bag.Load<GameObject>(LogoCardAddress)", "加载 prefab 并实例化"));
            // 切走本章时销毁还留屏的卡片（prefab 的 handle 由 Bag 释放，但 Instantiate 出来的实例要自己销毁）。
            Bag.Add(Disposable.Create(() =>
            {
                foreach (var go in spawnedCards) if (go != null) Object.Destroy(go);
                spawnedCards.Clear();
            }));
            host.AddNote("资源系统不止能加载图片：Bag.Load<GameObject> 把整个 prefab 加载进来，再 Instantiate 到屏幕中央，点卡片销毁。实例复用（对象池）是另一回事，见「对象池」章。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoLogoCard.cs", "class DemoLogoCard", "Logo 卡片脚本"));

            // ── 3. AssetReference（Inspector 拖拽）──
            host.AddSectionTitle("AssetReference：Inspector 拖资源、Awake 自动绑定");
            var refLabel = host.AddValueDisplay();
            if (refs == null)
            {
                refLabel.text = "没找到 DemoAssetRefs";
                host.AddNote("请确认 DemoApp 下挂了 DemoAssetRefs，并在 Inspector 拖好了 Logo / prefab 引用。");
            }
            else
            {
                refLabel.text = "点下面按钮用拖拽引用加载";
                host.AddActionRow("Get() 单个 Logo 引用", async () =>
                {
                    var sprite = await refs.LogoRef.Get();
                    if (sprite != null)
                    {
                        spritePreview.style.backgroundImage = new StyleBackground(sprite);
                        refLabel.text = $"AssetReference.Get() 得到：{sprite.name}";
                    }
                    else refLabel.text = "LogoRef 未配置（Inspector 拖一张 Sprite 进去）";
                }, CodeRef.Here("refs.LogoRef.Get()", "AssetReference.Get"));
                host.AddActionRow("GetAll() 批量加载列表", async () =>
                {
                    var sprites = await refs.LogoList.GetAll();
                    refLabel.text = $"AssetReferenceList.GetAll() 并行加载了 {sprites.Length} 张";
                }, CodeRef.Here("refs.LogoList.GetAll()", "批量加载"));
                host.AddActionRow("Unload 单个引用", () =>
                {
                    refs.LogoRef.Unload();
                    refLabel.text = "LogoRef 已 Unload（再点 Get 会重新加载）";
                }, CodeRef.Here("refs.LogoRef.Unload()", "释放引用"));
            }
            host.AddNote("AssetReference 在 Inspector 直接拖资源（内部存 GUID，业务不碰 GUID）；挂在 MonoView/Model/System/Utility 上的字段会在 Awake 自动绑定加载器并登记进宿主 Bag，宿主销毁统一释放——零样板。DemoAssetRefs 就是个真实 MonoModelBase（资源引用配置），这些引用是它 Awake 自动绑好的。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoAssetRefs.cs", "class DemoAssetRefs", "DemoAssetRefs 定义"));

            // ── 3b. ScriptableObject 里的 AssetReference（手动 Bind）──
            host.AddSectionTitle("ScriptableObject 里的 AssetReference：手动 Bind 后 Get");
            var soLabel = host.AddValueDisplay();
            var cfg = refs != null ? refs.AssetConfig : null;
            if (cfg == null)
            {
                soLabel.text = "没找到 DemoAssetConfig（DemoAssetRefs.AssetConfig 未接线）";
                host.AddNote("请在 DemoApp 下的 DemoAssetRefs 上拖入一个 DemoAssetConfig 资产，并给它的 IconRef 配一张 Sprite。");
            }
            else
            {
                soLabel.text = $"配置资产：{cfg.name}　｜　IconRef.IsBound = {cfg.IconRef.IsBound}（SO 不会自动绑定）";
                host.AddActionRow("手动 Bind 后 Get()（用 SO 里的引用加载）", async () =>
                {
                    // SO 不是 MonoBehaviour、没有 Awake 自动绑定：用前先把它内部的 ref 绑到本 Context 的 utility +
                    // 本章 Bag 的销毁信号（切走本章自动取消加载），再 Get。
                    cfg.IconRef.Bind(asset, Bag.DisposeToken);
                    var sprite = await cfg.IconRef.Get();
                    if (sprite != null)
                    {
                        spritePreview.style.backgroundImage = new StyleBackground(sprite);
                        soLabel.text = $"已用 SO 里的 IconRef 加载：{sprite.name}　｜　IsBound = {cfg.IconRef.IsBound}";
                    }
                    else soLabel.text = "IconRef 未配置（在 DemoAssetConfig 资产里拖一张 Sprite）";
                }, CodeRef.Here("cfg.IconRef.Bind", "手动 Bind + Get"));
                // SO 是共享资产、长期存活：它内部 ref 持有的 handle 不随某个宿主销毁，切走本章时手动 Unload。
                Bag.Add(Disposable.Create(() => cfg.IconRef.Unload()));
            }
            host.AddNote("和 MonoXxxBase 字段不同：ScriptableObject / 纯 C# 对象没有 Awake 自动绑定，所以 SO 里的 AssetReference 用前要手动 ref.Bind(utility, hostToken)（这里 token 传本章 Bag 的 DisposeToken）。SO 是共享资产、不随某个宿主销毁，handle 要自己决定何时 Unload。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoAssetConfig.cs", "class DemoAssetConfig", "DemoAssetConfig 定义"));

            // ── 4. 是否需下载 / 地址有效 ──
            host.AddSectionTitle("是否需下载 / 地址有效");
            // 把布尔结果做成绿/红徽标，一眼可辨，比纯文字直观。
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

            host.AddActionRow("CheckLocationValid(Logo)", () =>
            {
                bool valid = asset.CheckLocationValid(LogoAddress);
                SetCheckBadge(valid, valid ? "地址有效 ✓（manifest 里有这个地址）" : "地址无效 ✗（manifest 里没有）");
            }, CodeRef.Here("asset.CheckLocationValid", "地址有效性"));
            host.AddActionRow("IsNeedDownload(Logo)", () =>
            {
                bool need = asset.IsNeedDownload(LogoAddress);
                SetCheckBadge(!need, need ? "需要下载 ↓（远端缺，要从 CDN 拉）" : "无需下载 ✓（已在本地）");
            }, CodeRef.Here("asset.IsNeedDownload", "是否需下载"));
            host.AddNote("CheckLocationValid 判断 manifest 里有没有这个地址；IsNeedDownload 判断该资源是否要从远端拉。EditorSimulate / Offline 下资源都在本地，IsNeedDownload 恒为「无需下载」——真实远端缺资源时才需要（下面 Host 模式可看到）。");

            // ── 5. 下载进度（tag 下载器）──
            host.AddSectionTitle("下载进度：tag 下载器（模拟 / 真实自动切换）");
            // 标明当前走哪种：EditorSimulate = 模拟、Offline = 全本地不下载、Host/Web = 真实远端。
            var dlMode = settingsModel != null ? settingsModel.PlayMode : asset.CurrentPlayMode;
            bool dlIsReal = dlMode == AssetPlayMode.Host || dlMode == AssetPlayMode.Web;
            var modeBadge = new Label(dlIsReal
                ? $"当前：真实下载（{dlMode}，从 CDN 拉）"
                : dlMode == AssetPlayMode.Offline
                    ? "当前：Offline —— 全本地内置，不发生下载"
                    : "当前：模拟下载（EditorSimulate，无真实文件，按 大小÷速度 模拟进度）");
            modeBadge.AddToClassList("demo-badge");
            if (dlIsReal) modeBadge.AddToClassList("demo-badge--yes");
            host.Content.Add(modeBadge);
            var progressLabel = host.AddValueDisplay("点下面按钮开始下载");
            var progressBar = new ProgressBar { lowValue = 0f, highValue = 1f };
            progressBar.style.marginBottom = 8;
            host.Content.Add(progressBar);
            host.AddActionRow("创建下载器并下载", async () =>
            {
                await Bag.EnsureInitialized();
                var dl = Bag.CreateTagDownloader(DemoTag);

                // 真实模式下「没有要下载的」是常态（已缓存命中 / 已内置 / 或 tag 没匹配到 bundle）：
                // 此时 TotalCount=0，直接 Download() 会瞬间完成、进度恒 0，看着像“什么都没发生”。显式交代清楚，别让人困惑。
                if (!dl.IsSimulated && dl.TotalCount == 0)
                {
                    progressBar.value = 1f;
                    progressBar.title = "无需下载（0 个 / 0 MB）";
                    progressLabel.text = $"无需下载：tag「{DemoTag}」下没有要从 CDN 拉的资源（已缓存/已内置，或没匹配到 bundle）。" +
                        "想重测真实下载：点下面「清空下载缓存（运行时）」再点本按钮即可（免停 Play）；或停 Play 手动删缓存目录后重进 Play。";
                    return;
                }

                Bag.Subscribe(dl.Progress, r =>
                {
                    progressBar.value = r.Progress;
                    progressBar.title = $"{r.Progress:P0}　{r.CurrentSizeMB}/{r.TotalSizeMB} MB";
                });
                progressLabel.text = dl.IsSimulated
                    ? $"下载中…（EditorSimulate 模拟：总 {dl.TotalBytes / 1048576f:0.00} MB，按配置大小÷速度推进）"
                    : $"下载中…（真实远端：{dl.TotalCount} 个 / {dl.TotalBytes / 1048576f:0.00} MB）";
                await dl.Download();
                progressLabel.text = $"下载完成 ✓（{dl.TotalCount} 个 / {dl.TotalBytes / 1048576f:0.00} MB）";
            }, CodeRef.Here("Bag.CreateTagDownloader(DemoTag)", "下载器用法"));

            // 运行时清缓存：清完内存缓存记录同步更新，IsNeedDownload 立刻变真，同一次 Play 里就能再测真实下载——免去停 Play 手删。
            host.AddActionRow("清空下载缓存（运行时，免停 Play 即可重测）", async () =>
            {
                await Bag.EnsureInitialized();
                await asset.ClearCacheAsync(AssetCacheClearMode.All);
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                bool need = asset.IsNeedDownload(LogoAddress);
                progressLabel.text = $"已清空下载缓存 ✓　IsNeedDownload(Logo)={need}（远端模式下应变 true——再点上面「创建下载器并下载」即可看到真实下载）。";
            }, CodeRef.Here("asset.ClearCacheAsync", "运行时清缓存"));
            host.AddNote($"CreateTagDownloader(tags) 统计这些 tag 下需下载的资源（这里用 collector 给样例资源打的 tag「{DemoTag}」），订阅 Progress（R3 状态流，订阅即得快照）驱动进度条，Download() 启动。");
            host.AddSubNote("EditorSimulate 下资源都在本地、真实 downloader 的 TotalCount=0、UI 会瞬间跳满。框架检测到这种情况就包一层 SimulatedAssetDownloader：用「模拟大小 ÷ 速度」算时长、按固定 50ms 间隔推进 Progress 与已下载字节，让你能真实验证下载 UI（含总大小 / 已下载 / IsSimulated「模拟」标）。大小、速度 = AssetSystemConfigModel 上「模拟下载」两项（默认 8MB ÷ 2MB/s ≈ 4 秒，任一设 0 关闭模拟）。",
                new CodeRef("Assets/Game/Framework/Scripts/Asset/SimulatedAssetDownloader.cs", "class SimulatedAssetDownloader", "模拟下载器实现"));
            host.AddSubNote("Host/Web（真实模式）下显示的是真要从 CDN 拉的量：tag 没匹配到 bundle、或资源已在本地缓存/内置，都会是 0。模拟只在 EditorSimulate 生效，所以切到 Host 后看到的就是真实统计。");
            host.AddSubNote("ClearCacheAsync 不只是测试用，正式游戏也会用：All = 设置里「清除缓存」/ 资源损坏恢复 / 强制全量重下；Unused = 热更到新版本后回收旧版本残留 bundle（省空间）。它只删盘上的下载缓存并同步内存记录，不卸载已加载到内存的资源。");
#if UNITY_EDITOR
            host.AddActionRow("定位模拟下载配置（大小 / 速度）", () =>
            {
                if (settingsModel != null) PingSceneObject(settingsModel.gameObject);
            });
            host.AddActionRow("定位下载缓存目录（停 Play 后手动清空可重测真实下载）", RevealCacheDir);
#endif

            // ── 6. 跨包加载 ──
            host.AddSectionTitle("跨包加载");
            var crossLabel = host.AddValueDisplay();
            host.AddActionRow("显式从包名加载（Bag.Load(package, location)）", async () =>
            {
                var sprite = await Bag.Load<Sprite>(SamplesPackage, LogoAddress);
                crossLabel.text = sprite != null ? $"从包「{SamplesPackage}」加载到：{sprite.name}" : "加载失败";
            }, CodeRef.Here("Bag.Load<Sprite>(SamplesPackage", "跨包加载"));
            host.AddNote("默认包之外，所有加载方法都有带 packageName 的重载。多包在 AssetSystemConfigModel.Packages 配置；子 Context 经 Container 父级回退共享父级 AssetUtility，不必每个 Context 各挂一套。本 demo 把框架样例资源单独分到 FrameworkSamplesPackage，与正式游戏 DefaultPackage 分开、互不污染。");

            // ── 7. 远端 CDN / Host（进阶 · 手把手）──
            host.AddSectionTitle("远端 CDN / Host 模式（进阶 · 手把手）");
            host.AddNote("一句话原理：把资源传到 HTTP 服务器，运行时按需下载并缓存。下面 ⓪–④ 是一条有序流程（带源码定位）；构建相关步骤在编辑器菜单 SSFramework/CDN 执行（需 Edit 模式——AssetBundle 构建不能在 Play 模式跑）。");

            host.AddStep("⓪", "配置（前提）：YooAsset Collector 决定「哪些资源、按什么规则、打进哪个包/组」——构建就是按它执行的，所以得先有配置。本 demo 已把样例资源分到 FrameworkSamplesPackage（与游戏 DefaultPackage 分开）。");
#if UNITY_EDITOR
            host.AddActionRow("定位 Collector 分包配置（构建按它执行）", () =>
                PingAsset("Assets/Game/Framework/Settings/AssetBundleCollectorSetting.asset"));
#endif

            host.AddStep("①", "构建：YooAsset SBP 管线把样例包打成 bundle + 版本清单（manifest）；同时往 StreamingAssets 写一份内置清单（Host 初始化要用）。菜单 SSFramework/CDN/构建并部署样例包。",
                new CodeRef("Assets/Game/Framework/Demo/Editor/DemoCdnBuilder.cs", "BuildPackage", "① 构建实现"));
            host.AddSubNote("产物里三类「清单」各管一事：① 版本清单（包_版本.bytes）= 每个 bundle 的名字/哈希/大小/依赖 + 「资源地址→bundle」映射，运行时据此知道要哪个包、从哪取、校验对不对；② 版本号文件（包.version）= 一行文本=当前最新版本号，Host 先拉它知道最新版、再拉对应版本清单；③ 内置清单（BuiltinCatalog.bytes）= 哪些 bundle 已随包内置在本地，Host 据此判断哪些无需下载。");

            host.AddStep("②", "部署：把构建产物拷到「项目根/CDN/包名」子目录。GameRemoteService 按 {CDN}/{包名}/{文件名} 取址，每个包独占一个子目录——多包互不覆盖、部署结构直接镜像构建产物。",
                new CodeRef("Assets/Game/Framework/Demo/Editor/DemoCdnBuilder.cs", "DeployToPackageDir", "② 部署实现"));
#if UNITY_EDITOR
            host.AddActionRow("定位部署目录 / 版本清单文件", RevealCdnDir);
#endif

            host.AddStep("③", "起服务：在部署目录（CDN 根）起 python -m http.server，端口取自 AssetSystemConfigModel 的 CDN URL（单一配置源）。菜单 SSFramework/CDN/启动本地服务器。",
                new CodeRef("Assets/Game/Framework/Demo/Editor/DemoCdnBuilder.cs", "StartServer", "③ 起服务实现"));

            host.AddStep("④", "切 Host 重进 Play：把 AssetSystemConfigModel.PlayMode 改 Host。运行时先查本地缓存、缺了按需从 CDN 下载并缓存——下载进度就是第 5 节那套（此时为真实），IsNeedDownload 也变真。",
                new CodeRef("Assets/Game/Framework/Scripts/Asset/Providers/YooAssetProvider.cs", "case AssetPlayMode.Host", "④ Host 初始化实现"));
#if UNITY_EDITOR
            host.AddActionRow("定位 AssetSystem 配置节点（切 Host 在这）", () =>
            {
                if (settingsModel != null) PingSceneObject(settingsModel.gameObject);
            });
#endif
            host.AddTip("两个最常踩的坑：① 平台——AssetBundle 按平台区分，且编辑器进程本身是 Windows，加载不了为 Android 等移动平台构建的 bundle。要在编辑器里测 Host，先把 Build Target 切到 Standalone Windows 再用菜单重新构建；测移动平台请上真机。② 顺序——必须先「构建+部署 CDN」再进 Play：init 只在进游戏时跑一次，先进 Play 会因 StreamingAssets 内置清单缺失而 404 失败，之后补构建也要重进 Play 才生效。");

            // ── 8. 使用路径 / 注册=生命周期 ──
            host.AddSectionTitle("使用路径");
            host.AddConcept("Bag.Load / LoadScene / LoadText", "动态加载：借来的资源进 Bag，宿主销毁自动释放，心智同 Bag.Rent / Bag.Spawn。");
            host.AddConcept("AssetReference", "Inspector 拖拽引用：MonoXxxBase 字段 Awake 自动绑定 + 入 Bag；ScriptableObject / 手动创建的 ref 需自己 Bind(utility, token)。");
            host.AddConcept("IAssetUtility", "手动入口：this.GetUtility<IAssetUtility>()——查初始化状态、CheckLocationValid / IsNeedDownload、建下载器、清下载缓存（ClearCacheAsync：All / Unused）。");

            host.AddSectionTitle("注册 = 生命周期");
            host.AddConcept("三层 Mono", "AssetSystemConfigModel + AssetUtility + AssetInitSystem 挂同一 Context 节点，Awake 顺序由 ExecutionOrder 保证（Utility -400 / Model -300 / System -200）。");
            host.AddConcept("四种 PlayMode", "EditorSimulate（编辑器免打包）/ Offline（仅内置）/ Host（内置 + 远端 CDN 缓存）/ Web（HTTP 远端，不缓存）。");
            host.AddConcept("样例分包", "框架 demo/test 资源在 FrameworkSamplesPackage，正式游戏用自己的 DefaultPackage，互不污染，可随框架保留或删除。");

            host.AddNote("框架与 YooAsset 解耦：所有 YooAsset 接触面都收口在 IAssetProvider，只有 AssetProviderFactory.CreateDefault() 里 new YooAssetProvider()。换 Addressables / 自研库只需实现一个新 IAssetProvider，AssetUtility 与业务、demo 全程只认接口、零改动。",
                new CodeRef("Assets/Game/Framework/Scripts/Asset/AssetProviderFactory.cs", "CreateDefault", "provider 工厂（换库就改这）"));

            host.AddTip("约定：动态加载优先 Bag.Load（自动释放）；Inspector 引用优先 AssetReference（自动绑定）；跨包用带 packageName 的重载。框架不提供 UnloadPackage——要释放就 Dispose handle / 整 Context 重建。");
        }

#if UNITY_EDITOR
        // 「定位」系列编辑器便利：把配置文件 / 场景节点 / 部署目录一键高亮，做出手把手教学的导览感。

        // 在 Project 窗口高亮定位一个工程资产（如 Collector 分包配置）。
        private static void PingAsset(string assetPath)
        {
            var obj = UnityEditor.AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj != null) { UnityEditor.Selection.activeObject = obj; UnityEditor.EditorGUIUtility.PingObject(obj); }
            else Debug.LogWarning("[Demo] 没找到资产：" + assetPath);
        }

        // 在 Hierarchy 高亮定位一个场景节点（如 AssetSystem 资源系统配置节点）。
        private static void PingSceneObject(GameObject go)
        {
            if (go == null) return;
            UnityEditor.Selection.activeObject = go;
            UnityEditor.EditorGUIUtility.PingObject(go);
        }

        // 在资源管理器打开 CDN 部署目录（项目根/CDN）。从 dataPath 推项目根，避开 System.IO 与 Game.Framework.System 的命名空间冲突。
        private static void RevealCdnDir()
        {
            string dataPath = Application.dataPath;                                        // .../Assets
            string projectRoot = dataPath.Substring(0, dataPath.Length - "Assets".Length); // 末尾保留 '/'
            UnityEditor.EditorUtility.RevealInFinder(projectRoot + "CDN");
        }

        // 在资源管理器打开 YooAsset 的下载缓存目录。编辑器下 YooAsset 把缓存放在「项目根/yoo」（见 .gitignore 的 /yoo/），
        // 实际下载的 bundle 落在 yoo/<包名>/BundleFiles 下。停掉 Play 后删掉这里的文件，IsNeedDownload 会重新变真，即可重测真实下载。
        // 同样用 dataPath 推项目根，避开 System.IO 与 Game.Framework.System 的命名空间冲突。
        private static void RevealCacheDir()
        {
            string dataPath = Application.dataPath;                                        // .../Assets
            string projectRoot = dataPath.Substring(0, dataPath.Length - "Assets".Length); // 末尾保留 '/'
            UnityEditor.EditorUtility.RevealInFinder(projectRoot + "yoo");
        }
#endif
    }
}
