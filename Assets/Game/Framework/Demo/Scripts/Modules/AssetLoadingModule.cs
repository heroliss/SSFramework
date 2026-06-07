using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·资源加载（<b>框架用法</b>）：只讲与底层库无关的框架资源 API——Bag.Load 借资源随宿主自动释放、
    /// AssetReference 拖拽自动绑定、ScriptableObject 配置加载 + 一键绑定、查询与下载、跨包加载、清缓存。
    /// 当前默认后端 YooAsset 的底层原理（清单 / 目录 / 构建管线 / CDN / Host 流程）在「YooAsset · 底层实现」章。
    /// </summary>
    public sealed class AssetLoadingModule : DemoModuleBase
    {
        public override string Id => "asset-loading";
        public override string Title => "资源加载";
        public override string Category => "能力";
        public override int Order => 20;
        public override string Summary =>
            "框架统一的资源入口，与底层库解耦：Bag.Load 借资源随宿主释放、AssetReference 拖拽自动绑定、SO 配置加载 + 一键绑定、查询/下载/清缓存、跨包加载。底层 YooAsset 原理见「YooAsset · 底层实现」章。";

        // demo 资源都在 FrameworkSamplesPackage（见 collector）；地址 = 文件名（AddressByFileName 规则）。
        private const string LogoAddress = "SSFramework-Logo";
        private const string ConfigAddress = "DemoAssetConfig";
        private const string SamplesPackage = "FrameworkSamplesPackage";

        // collector 给 demo 资源打的 tag（FrameworkDemoGroup 的 AssetTags=framework-demo）；tag 下载器按它统计需下载的 bundle。
        private const string DemoTag = "framework-demo";

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            var refs = UnityEngine.Object.FindFirstObjectByType<DemoAssetRefs>();
            var settingsModel = UnityEngine.Object.FindFirstObjectByType<AssetSystemConfigModel>();

            // ── 1. 初始化与状态 ──
            host.AddSectionTitle("初始化与状态");
            var stateLabel = host.AddValueDisplay("", CodeRef.Here("asset.InitState", "订阅初始化状态"));
            // InitState 是状态流，订阅即得当前值；切走本章 Bag.Dispose 时自动退订。启动 loading 界面就订阅它驱动。
            // Failed 时把它转成可操作的引导（而不是只显示 Failed）——常见于「切了 Host/Offline 但没先构建」。
            Bag.Subscribe(asset.InitState, s => stateLabel.text = s == AssetInitState.Failed
                ? $"默认包初始化失败（运行模式：{asset.CurrentPlayMode}）：Host/Offline 需先构建资源。" +
                  "请用菜单 SSFramework/资源构建 依次「1 构建资源包 → 2 部署 → 3 启动本地 CDN 服务」后重进 Play，或改回 EditorSimulate（免构建）。底层见「YooAsset · 底层实现」章；失败的正确兜底/重试见「资源容错」章。"
                : $"默认包初始化：{s}　｜　运行模式：{asset.CurrentPlayMode}");
#if UNITY_EDITOR
            host.AddActionRow("定位资源系统配置节点（AssetSystem）", () =>
            {
                if (settingsModel != null) PingSceneObject(settingsModel.gameObject);
            });
#endif
            host.AddNote("资源系统是 MVCS 三层：`AssetSystemConfigModel`（配置：默认包 / PlayMode / CDN）→ `AssetInitSystem`（进游戏逐包初始化）→ `AssetUtility`（加载 API），挂在同一 `Context` 节点（上面按钮可定位）。业务只经 `this.GetUtility<IAssetUtility>()` / `Bag.Load` 访问。");
            host.AddSubNote("无需手动等初始化：`Bag.Load` 内部会自动等就绪。只有「启动 loading 界面要等资源系统就绪再进主流程」这类场景，才订阅 `InitState` 或 await `Bag.EnsureInitialized()`。");

            // ── 2. 按地址加载（Bag.Load）──
            host.AddSectionTitle("按地址加载：Bag.Load（借资源随宿主自动释放）");
            var spritePreview = NewPreview();
            host.Content.Add(spritePreview);
            var loadLabel = host.AddValueDisplay("点下面按钮加载");
            // 用子 Bag 装本节借来的句柄：基类 Bag 要切走本章才释放，开个子 Bag 就能在「释放」按钮里随手 Dispose，
            // 既演示资源释放、又方便反复测加载（AGENTS §17 的「清理一次」子 bag 用法）。
            var logoBag = Bag.CreateChild();
            host.AddActionRow("加载 Logo（Sprite）", async () =>
            {
                var sprite = await logoBag.Load<Sprite>(LogoAddress);
                if (sprite != null)
                {
                    spritePreview.style.backgroundImage = new StyleBackground(sprite);
                    loadLabel.text = $"已加载 Sprite：{sprite.name}（{sprite.rect.width:0}×{sprite.rect.height:0}）";
                }
            }, CodeRef.Here("logoBag.Load<Sprite>(LogoAddress)", "Bag.Load 用法"));
            host.AddActionRow("释放 Logo（Dispose 子 Bag → 句柄释放）", () =>
            {
                // Dispose 子 Bag 释放它托管的全部句柄；再 CreateChild 重建，下次加载用新子 Bag（§17 既定写法）。
                logoBag.Dispose();
                logoBag = Bag.CreateChild();
                // 句柄已随 Dispose 释放；UI 上的图是另一份引用，得清掉显示元素画面才消失（同 §3 释放按钮的注释）。
                spritePreview.style.backgroundImage = StyleKeyword.None;
                loadLabel.text = "已释放本节 Logo 句柄并清空预览。再点「加载 Logo」会重新加载。";
            }, CodeRef.Here("logoBag.Dispose()", "手动释放本节句柄"));
#if UNITY_EDITOR
            host.AddActionRow("定位 Logo 资产（被加载的源资源）", () =>
                PingAsset("Assets/Game/Framework/Res/SSFramework-Logo.png"));
#endif
            host.AddNote("`Bag.Load<T>(location)` 借来的资源 handle 进 `Bag`，切走本章 `Bag.Dispose` 自动释放，业务不持有句柄。想提前释放某批句柄，就像本节这样开个 `Bag.CreateChild()` 子 Bag 装它们、需要时 `Dispose`（再 `CreateChild` 重建）。`Bag.Load` 是泛型：prefab 用 `GameObject`、场景用 `LoadScene`、文本用 `LoadText`、字节用 `LoadBytes` 同理；跨包用带 `packageName` 的重载（见下）。");

            // ── 3. AssetReference（Inspector 拖拽）──
            host.AddSectionTitle("AssetReference：Inspector 拖资源、Awake 自动绑定");
            var refLabel = host.AddValueDisplay();
            if (refs == null)
            {
                refLabel.text = "没找到 DemoAssetRefs";
                host.AddNote("请确认 DemoApp 下挂了 `DemoAssetRefs`，并在 Inspector 拖好了 Logo 引用。");
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

                host.AddActionRow("Get() 单个 Logo 引用", async () =>
                {
                    var sprite = await refs.LogoRef.Get();
                    if (sprite != null) { ShowSprites(sprite); refLabel.text = $"AssetReference.Get() 得到：{sprite.name}"; }
                    else refLabel.text = "LogoRef 未配置（Inspector 拖一张 Sprite 进去）";
                }, CodeRef.Here("refs.LogoRef.Get()", "AssetReference.Get"));
                host.AddActionRow("GetAll() 批量加载列表", async () =>
                {
                    var sprites = await refs.LogoList.GetAll();
                    ShowSprites(sprites); // 把加载到的每一张都平铺展示出来
                    refLabel.text = $"AssetReferenceList.GetAll() 并行加载了 {sprites.Length} 张";
                }, CodeRef.Here("refs.LogoList.GetAll()", "批量加载"));
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
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoAssetRefs.cs", "class DemoAssetRefs", "DemoAssetRefs 定义"));
#if UNITY_EDITOR
            host.AddActionRow("定位资源引用配置节点（DemoAssetRefs）", () =>
            {
                if (refs != null) PingSceneObject(refs.gameObject);
            });
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
            host.AddActionRow("① 加载配置 SO（Bag.Load<DemoAssetConfig>）", async () =>
            {
                // config SO 像资源一样被加载进来（真实游戏的常见形态：配置也走资源系统下发/热更）。
                loadedConfig = await configBag.Load<DemoAssetConfig>(ConfigAddress);
                soLabel.text = loadedConfig != null
                    ? $"已加载配置 {loadedConfig.name}。它内部的 AssetReference 还没绑定——点②（或故意跳过②直接点③，看不绑定的后果）。"
                    : "加载失败（地址 DemoAssetConfig 在 FrameworkSamplesPackage？）";
            }, CodeRef.Here("configBag.Load<DemoAssetConfig>(ConfigAddress)", "加载配置 SO"));
            host.AddActionRow("② 绑定它的引用（Bag.BindAssetReferences）", () =>
            {
                if (loadedConfig == null) { soLabel.text = "请先点①加载配置 SO。"; return; }
                // SO 不是 MonoXxxBase、字段不会自动绑定：一行把它内部所有 AssetReference 绑到本节子 Bag（随它一起释放）。
                configBag.BindAssetReferences(loadedConfig);
                soLabel.text = $"已把 {loadedConfig.name} 的全部 AssetReference 绑到本节子 Bag，现在可安全点③取引用。";
            }, CodeRef.Here("configBag.BindAssetReferences(loadedConfig)", "一键绑定 SO 内引用"));
            host.AddActionRow("③ 取 IconRef（IconRef.Get）", async () =>
            {
                if (loadedConfig == null) { soLabel.text = "请先点①加载配置 SO。"; return; }
                var icon = await loadedConfig.IconRef.Get();
                if (icon != null)
                {
                    soPreview.style.backgroundImage = new StyleBackground(icon);
                    soLabel.text = $"用 {loadedConfig.name} 的 IconRef 取到：{icon.name}";
                }
                else soLabel.text = "IconRef 未配置（在 DemoAssetConfig 资产里拖一张 Sprite）。";
            }, CodeRef.Here("loadedConfig.IconRef.Get()", "取 SO 内引用"));
            host.AddActionRow("释放 IconRef（Unload）", () =>
            {
                if (loadedConfig == null) { soLabel.text = "请先点①加载配置 SO。"; return; }
                loadedConfig.IconRef.Unload();
                // 同理：Unload 只放句柄，清掉显示元素画面才消失。
                soPreview.style.backgroundImage = StyleKeyword.None;
                soLabel.text = "已 IconRef.Unload()，预览清空（再点③重新取）。";
            }, CodeRef.Here("loadedConfig.IconRef.Unload()", "释放 SO 内引用"));
            host.AddActionRow("释放配置 SO（Dispose 子 Bag，重测用）", () =>
            {
                configBag.Dispose();          // 一次释放 config SO 自身 + 已绑定的内部引用
                configBag = Bag.CreateChild();
                loadedConfig = null;
                soPreview.style.backgroundImage = StyleKeyword.None;
                soLabel.text = "已释放配置 SO。可重新①②③重测——比如只点①再点③、跳过②，看「没 Bind 就取引用」会 fallback 到 GameContext.Main 并报一条 error。";
            }, CodeRef.Here("configBag.Dispose()", "释放配置 SO"));
            host.AddNote("`ScriptableObject` 配置是「被加载的数据资产」，不是 `Model` 层（它常需像资源一样异步加载，无法在启动时注册成 `Model`）。它内部的 `AssetReference` 不会自动绑定（框架刻意不递归 SO），由加载 / 持有它的宿主一行 `Bag.BindAssetReferences`(配置) 把它的全部引用绑到自身生命周期——之后随本章 `Bag` 一起释放。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/DemoAssetConfig.cs", "class DemoAssetConfig", "DemoAssetConfig 定义"));
#if UNITY_EDITOR
            host.AddActionRow("定位 DemoAssetConfig 资产（被加载的配置 SO）", () =>
                PingAsset("Assets/Game/Framework/Res/DemoAssetConfig.asset"));
#endif

            // ── 4. 查询：地址有效 / 是否需下载 ──
            host.AddSectionTitle("查询：地址有效 / 是否需下载");
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
                // 包未就绪时这两个查询都返回 false——先判就绪，否则会把"查不了"误显示成"地址无效/已在本地"。
                if (!asset.IsInitialized) { SetCheckBadge(false, "资源系统未就绪（初始化失败或未完成）——查询无意义，先看上方初始化状态。"); return; }
                bool valid = asset.CheckLocationValid(LogoAddress);
                SetCheckBadge(valid, valid ? "地址有效 ✓（manifest 里有这个地址）" : "地址无效 ✗（manifest 里没有）");
            }, CodeRef.Here("asset.CheckLocationValid", "地址有效性"));
            host.AddActionRow("IsNeedDownload(Logo)", () =>
            {
                if (!asset.IsInitialized) { SetCheckBadge(false, "资源系统未就绪（初始化失败或未完成）——查询无意义，先看上方初始化状态。"); return; }
                bool need = asset.IsNeedDownload(LogoAddress);
                SetCheckBadge(!need, need ? "需要下载 ↓（远端缺，要从 CDN 拉）" : "无需下载 ✓（已在本地）");
            }, CodeRef.Here("asset.IsNeedDownload", "是否需下载"));
            host.AddNote("`CheckLocationValid` 判断 manifest 里有没有这个地址；`IsNeedDownload` 判断该资源要不要从远端拉。⚠ 两者在「包未就绪」（未初始化 / 初始化失败）时也返回 `false`——所以 `false` ≠「地址无效 / 无需下载 / 已在本地」，得先确认初始化 `Ready`（上方状态）再读结果，本 demo 已加这层判断。`EditorSimulate` / `Offline` 下资源都在本地，`IsNeedDownload` 恒为「无需下载」；只有远端模式（`Host`）才会变真，底层见「YooAsset · 底层实现」章。");

            // ── 5. 下载与清缓存（tag 下载器）──
            host.AddSectionTitle("下载与清缓存：tag 下载器（模拟 / 真实自动切换）");
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
            var progressLabel = host.AddValueDisplay("先点「创建下载器」统计，再点「开始下载」");
            var progressBar = new ProgressBar { lowValue = 0f, highValue = 1f };
            progressBar.style.marginBottom = 8;
            host.Content.Add(progressBar);
            // 白盒：创建下载器与启动下载是两个独立操作，拆两个按钮——先创建（统计要下多少），再下载，互不打包。
            IAssetDownloader downloader = null;
            host.AddActionRow("创建下载器（统计待下载）", async () =>
            {
                await Bag.EnsureInitialized();
                downloader = Bag.CreateTagDownloader(DemoTag);
                Bag.Subscribe(downloader.Progress, r =>
                {
                    progressBar.value = r.Progress;
                    progressBar.title = $"{r.Progress:P0}　{r.CurrentSizeMB}/{r.TotalSizeMB} MB";
                });
                // 真实模式下「没有要下载的」是常态（已缓存命中 / 已内置 / 或 tag 没匹配到 bundle），TotalCount=0，下载会瞬间完成。显式交代。
                progressLabel.text = downloader.IsSimulated
                    ? $"已创建（EditorSimulate 模拟：总 {downloader.TotalBytes / 1048576f:0.00} MB）。点「开始下载」。"
                    : downloader.TotalCount == 0
                        ? $"已创建：tag「{DemoTag}」下无需下载（0 个，已缓存/已内置）。想重测先点「清空下载缓存」。"
                        : $"已创建：待下载 {downloader.TotalCount} 个 / {downloader.TotalBytes / 1048576f:0.00} MB。点「开始下载」。";
            }, CodeRef.Here("Bag.CreateTagDownloader(DemoTag)", "创建下载器"));
            host.AddActionRow("开始下载（Download）", async () =>
            {
                if (downloader == null) { progressLabel.text = "请先点「创建下载器」。"; return; }
                progressLabel.text = "下载中…";
                await downloader.Download();
                progressLabel.text = $"下载完成 ✓（{downloader.TotalCount} 个 / {downloader.TotalBytes / 1048576f:0.00} MB）";
            }, CodeRef.Here("downloader.Download()", "启动下载"));

            // 运行时清缓存：清完内存缓存记录同步更新，IsNeedDownload 立刻变真，同一次 Play 里就能再测真实下载——免去停 Play。
            host.AddActionRow("清空下载缓存（运行时，免停 Play 即可重测）", async () =>
            {
                await Bag.EnsureInitialized();
                await asset.ClearCacheAsync(AssetCacheClearMode.All);
                downloader = null;  // 老下载器的待下载列表是创建时的快照，缓存清了它不会更新；置空逼重建，否则点「开始下载」会执行旧快照（0 个）瞬间完成。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                bool need = asset.IsNeedDownload(LogoAddress);
                progressLabel.text = $"已清空下载缓存 ✓　IsNeedDownload(Logo)={need}（远端模式下应变 true）。下载器已重置——请重新点「创建下载器」再「开始下载」才会重新统计。";
            }, CodeRef.Here("asset.ClearCacheAsync", "运行时清缓存"));
            host.AddActionRow("按 tag 清缓存（只清本 demo tag 的 bundle）", async () =>
            {
                await Bag.EnsureInitialized();
                await asset.ClearCacheByTagsAsync(new[] { DemoTag });
                downloader = null;  // 同上：清缓存后下载器快照过期，置空逼重建。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                progressLabel.text = $"已按 tag「{DemoTag}」清缓存 ✓——只清这批 tag 的 bundle，正适合卸载某关卡 / DLC 的资源（其余缓存不动）。下载器已重置，重测请重新「创建下载器」。";
            }, CodeRef.Here("asset.ClearCacheByTagsAsync(new[] { DemoTag })", "按 tag 清缓存"));
            host.AddActionRow("按地址清缓存（清 Logo 所在的 bundle）", async () =>
            {
                await Bag.EnsureInitialized();
                await asset.ClearCacheByLocationsAsync(new[] { LogoAddress });
                downloader = null;  // 同上：清缓存后下载器快照过期，置空逼重建。
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                progressLabel.text = $"已按地址「{LogoAddress}」清缓存 ✓——点名清这个资源所在的 bundle；注意是 bundle 粒度，同 bundle 的邻居会被连带清。下载器已重置，重测请重新「创建下载器」。";
            }, CodeRef.Here("asset.ClearCacheByLocationsAsync(new[] { LogoAddress })", "按地址清缓存"));
            host.AddNote("`CreateTagDownloader(tags)` 统计这些 tag 下要下载的资源，订阅 `Progress`（R3 状态流）驱动进度条，`Download()` 启动；`ClearCacheAsync` 清本地已下载缓存（`All` 全清 / `Unused` 清旧版本，正式游戏也用），按某关卡 / DLC 的 tag 清则用 `ClearCacheByTagsAsync`，点名清少数已知资源用 `ClearCacheByLocationsAsync`。下载中途失败由下载器自带按 `AssetSystemConfigModel.FailedTryAgain`（默认 3）重试 + 断点续传（已下分片不重下），业务不必手写重试循环。");
            host.AddSubNote("下载器是「创建那一刻的待下载快照」，不是「下载时去看缺什么补什么」：清缓存并不会更新已建好的下载器，得重新 `CreateTagDownloader` 才会按最新缓存重新统计。所以「清缓存 → 重建下载器 → 开始下载」是固定顺序。");
            host.AddSubNote("`ClearCacheByTagsAsync` 多 tag 是并集（命中任意一个就清）；`ClearCacheByLocationsAsync` 与 tag 清一样都是 bundle 粒度——按地址清会连带同 bundle 的其他资源，想精确隔离要在打包时让该资源独占 bundle。");
            host.AddSubNote("「模拟下载器」原理、下载缓存目录在哪、各清单文件、各 `PlayMode` 的底层差异——见「YooAsset · 底层实现」章。本节只演示框架 API 用法。");

            // ── 6. 跨包加载 ──
            host.AddSectionTitle("跨包加载");
            var crossLabel = host.AddValueDisplay();
            var crossPreview = NewPreview();
            host.Content.Add(crossPreview);
            var crossBag = Bag.CreateChild(); // 用子 Bag 装本节句柄，方便就近释放（同 §2）
            host.AddActionRow("显式从包名加载（Bag.Load(package, location)）", async () =>
            {
                var sprite = await crossBag.Load<Sprite>(SamplesPackage, LogoAddress);
                if (sprite != null)
                {
                    crossPreview.style.backgroundImage = new StyleBackground(sprite);
                    crossLabel.text = $"从包「{SamplesPackage}」加载到：{sprite.name}";
                }
                else crossLabel.text = "加载失败";
            }, CodeRef.Here("crossBag.Load<Sprite>(SamplesPackage", "跨包加载"));
            host.AddActionRow("释放（Dispose 子 Bag）", () =>
            {
                crossBag.Dispose();
                crossBag = Bag.CreateChild();
                // 同理：Dispose 只放句柄，清掉显示元素画面才消失。
                crossPreview.style.backgroundImage = StyleKeyword.None;
                crossLabel.text = "已释放跨包加载的句柄并清空预览。";
            }, CodeRef.Here("crossBag.Dispose()", "释放本节句柄"));
            host.AddNote("默认包之外，所有加载方法都有带 `packageName` 的重载。多包在 `AssetSystemConfigModel.Packages` 配置；子 `Context` 经 `Container` 父级回退共享父级 `AssetUtility`，不必每个 `Context` 各挂一套。本 demo 把框架样例资源单独分到 `FrameworkSamplesPackage`，与正式游戏 `DefaultPackage` 分开、互不污染。");

            // ── 7. 使用路径 / 注册=生命周期 / 解耦 ──
            host.AddSectionTitle("使用路径");
            host.AddConcept("Bag.Load / LoadScene / LoadText", "动态加载：借来的资源进 `Bag`，宿主销毁自动释放，心智同 `Bag.Rent` / `Bag.Spawn`。");
            host.AddConcept("AssetReference", "Inspector 拖拽引用：`MonoXxxBase` 字段 `Awake` 自动绑定 + 入 `Bag`；`ScriptableObject` / 手动创建的 ref 由宿主 `Bag.BindAssetReferences`(对象) 一键绑。");
            host.AddConcept("IAssetUtility", "手动入口：`this.GetUtility<IAssetUtility>()`——查初始化状态、`CheckLocationValid` / `IsNeedDownload`、建下载器、清下载缓存（`ClearCacheAsync`）。");

            host.AddSectionTitle("注册 = 生命周期");
            host.AddConcept("三层 Mono", "`AssetSystemConfigModel` + `AssetUtility` + `AssetInitSystem` 挂同一 `Context` 节点，`Awake` 顺序由 `ExecutionOrder` 保证（`Utility` -400 / `Model` -300 / `System` -200）。");
            host.AddNote("框架与底层库解耦：所有 YooAsset 接触面都收口在 `IAssetProvider`，只有 `AssetProviderFactory.CreateDefault()` 里 new `YooAssetProvider()`。换 Addressables / 自研库只需实现一个新 `IAssetProvider`，`AssetUtility` 与业务、demo 全程只认接口、零改动。当前默认后端（YooAsset）的底层原理见「YooAsset · 底层实现」章。",
                new CodeRef("Assets/Game/Framework/Scripts/Asset/AssetProviderFactory.cs", "CreateDefault", "provider 工厂（换库就改这）"));

            host.AddTip("约定：动态加载优先 Bag.Load（自动释放）；Inspector 引用优先 AssetReference（自动绑定）；SO/手动 ref 用 Bag.BindAssetReferences；跨包用带 packageName 的重载。框架不提供 UnloadPackage——要释放就 Dispose handle / 整 Context 重建。");
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

#if UNITY_EDITOR
        // 在 Project 窗口高亮定位一个工程资产（被加载的源资源 / 配置 SO）。
        private static void PingAsset(string assetPath)
        {
            var obj = UnityEditor.AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj != null) { UnityEditor.Selection.activeObject = obj; UnityEditor.EditorGUIUtility.PingObject(obj); }
            else Debug.LogWarning("[Demo] 没找到资产：" + assetPath);
        }

        // 在 Hierarchy 高亮定位一个场景节点（AssetSystem 配置节点 / 资源引用配置节点）。
        private static void PingSceneObject(GameObject go)
        {
            if (go == null) return;
            UnityEditor.Selection.activeObject = go;
            UnityEditor.EditorGUIUtility.PingObject(go);
        }
#endif
    }
}
