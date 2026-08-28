using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using R3;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·资源运营流程：把「资源加载」章的原子 API 串成真实项目的启动器更新链路——
    /// 运营侧发新版本（构建 + 部署 = 覆盖 CDN 上的 <c>.version</c>）→ 客户端启动时检查（Initialize 拉版本 / 清单）→
    /// 强更下载（全量下载器 + 进度 + 失败重建重试）→ 回收旧缓存（ClearCache Unused）→ 进游戏。
    /// 核心是 <see cref="RunUpdateFlow"/>：一段注释完整、可整段搬进真实项目启动器的活样板。
    /// </summary>
    public sealed class AssetOpsFlowModule : DemoModuleBase
    {
        public override string Id => "asset-ops-flow";
        public override string Title => "资源运营 · 端到端";
        public override string Category => "进阶";
        public override int Order => 15;   // 紧跟「YooAsset · 底层实现」(10)：先懂构建/部署原理，再看运营链路怎么转
        public override DemoTeachingKind TeachingKind => DemoTeachingKind.Workflow;
        public override string Summary =>
            "启动器更新流程的活样板：运营侧发版本（构建+部署改 CDN 的 .version）→ 客户端启动检查（拉版本/清单）→ " +
            "强更下载（进度/重试/断点续传）→ 回收旧缓存 → 进游戏。EditorSimulate 可跑通流程（恒 0 下载），真实下载切 Host。";

        // 启动流程判定整体失败前，单个包的下载最多重建下载器重试几次。
        // 重试靠「重建」而不是复用：下载器一次性、失败后再调只会重抛；已下成功的分片已进缓存会被跳过（断点续传）。
        private const int DownloadAttempts = 3;

        // 启动更新与“修复资源”会操作同一批包和缓存。闸门跟模块实例而非某次 Build 走，
        // 这样 UIDocument 重建后，旧 Host 的取消尚在收尾时，新 UI 不会立刻启动第二条资源运营流程。
        private readonly DemoOperationGate _operationGate = new();

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            var settingsModel = UnityEngine.Object.FindFirstObjectByType<AssetSystemConfigModel>();
#if UNITY_EDITOR
            // 模拟断网属于共享 AssetUtility 的进程级调试状态。本章只借用它做实验，离章必须归还进入前的值，
            // 否则用户在这里开过断网后，后续资源章节会表现成无缘无故的网络故障。
            var previousSimulateOffline = asset.SimulateOffline.CurrentValue;
            Bag.Add(Disposable.Create(() => asset.SetSimulateOffline(previousSimulateOffline)));
#endif

            // 异步按钮收到 DemoModuleHost 的章节生命周期令牌：切走本章 / UIDocument 重建时取消进行中的流程。
            // 真实项目里同一令牌通常来自启动器 View 或 Context——样板方法只管逐层透传。

            // 本次流程要处理的包：demo 用配置里登记的全部包。
            var packages = new List<string>();
            if (settingsModel != null)
                foreach (var name in settingsModel.EnumeratePackageNames())
                    packages.Add(name);

            // ── 定位 ──
            host.AddPositioning("把原子 API 串成「发版 → 启动更新」的完整运营链路");
            host.AddNote("「资源加载」章给了原子 API（初始化 / 查询 / 下载器 / 清缓存），「YooAsset · 底层实现」章讲了构建与部署——本章把两侧串起来：**运营侧发一个新版本，客户端启动时检查并更到最新**。核心是一段可整段搬进真实项目的启动器流程方法（右侧跳源码），本页按钮就是在跑它。",
                CodeRef.Here("private static async UniTask<bool> RunUpdateFlow", "启动器流程活样板"));

            // ── 心智模型：更新发生在启动时 ──
            host.AddSectionTitle("心智模型：版本只在包初始化时拉取（启动时检查一次）");
            host.AddNote("框架刻意**不提供**「运行中重新拉版本」的 API：清单是加载的解析真源，运行中换清单会让已加载 / 正在加载的内容一半旧版一半新版。所以运营节奏固定为——CDN 发新版本 → 玩家**下次启动**（重进 Play）时 `Initialize` 自然拉到 → 启动器检查缺口并下载 → 进游戏。这与主流商业游戏「启动时检查更新，更新完重启生效」是同一套姿势。");
            host.AddSubNote("推论：同一次 Play 会话里已 `Ready` 的包再跑流程，检查更新一步是幂等空操作（拿不到新版本）——要看「版本切换」生效，须发新版本后**重进 Play**。");

            // 各包当前版本：GetPackageVersion 是设置页 / 客服排查「你是什么资源版本」的那个 API。
            var versionLabel = host.AddValueDisplay("", CodeRef.Here("var v = asset.GetPackageVersion(pkg)", "读包版本"));
            versionLabel.style.whiteSpace = WhiteSpace.Normal;
#if UNITY_EDITOR
            if (settingsModel != null)
                host.AddActionRow("选中资源配置节点 AssetSystemConfigModel（包列表 / CDN）",
                    () => DemoEditorNav.PingSceneObject(settingsModel.gameObject));
#endif

            void RefreshVersions()
            {
                if (packages.Count == 0) { versionLabel.text = "场景里没有 AssetSystemConfigModel / 未登记任何包。"; return; }
                var lines = new List<string>(packages.Count);
                foreach (var pkg in packages)
                {
                    var v = asset.GetPackageVersion(pkg);
                    lines.Add($"{pkg}：{v ?? "未就绪（尚未初始化 / 初始化失败）"}");
                }
                versionLabel.text = "当前资源版本　" + string.Join("　｜　", lines);
            }

            // 版本随各包初始化状态实时刷新（订阅即得当前值；Ready 后立即显示拉到的版本）。
            foreach (var pkg in packages)
                Bag.Subscribe(asset.GetInitState(pkg), _ => RefreshVersions());
            RefreshVersions();
            host.AddSubNote("`GetPackageVersion(pkg)` 返回该包当前生效清单的版本号（构建时的时间戳 / CI 传入的版本），未就绪返回 `null`。设置页显示资源版本、客服排查、更新完成后的确认都用它；`AssetUtility` 的 Inspector 诊断折叠组里同样能看到（各包状态 Ready 后附版本）。");

            // ── 运营侧：发一个新版本 ──
            host.AddSectionTitle("运营侧：发一个新版本（版本切换的本质 = 覆盖 CDN 上的 .version）");
            host.AddStep("①", "改资源（如换一张图、加一个 prefab），Play 外打开 SSFramework/构建与发布/资源构建 工作台并点“普通增量构建”——版本号默认取时间戳（CI 构建则显式传 `-version`，可追溯）。");
            host.AddStep("②", "在同一工作台点“部署到本地目录”——产物拷进本地 CDN 根目录，其中 `<包>.version` 这个一行文本文件被**覆盖成新版本号**。生产环境这步换成 CI 上传真实 CDN。");
            host.AddStep("③", "工作台的“启动本地 CDN 服务”只是启动静态文件服务器，不用因每次资源发布重启——下一个来拉 `.version` 的客户端自然读到新值。");
            host.AddStep("④", "重进 Play：客户端 `Initialize` 拉到新版本号 → 加载新版本清单 → 与本地缓存比对出缺口，进入下方客户端流程。");
            host.AddNote("「版本切换」没有任何魔法：CDN 上 `<包>.version` 的内容变了而已。bundle 文件名带哈希、新旧版本**共存**在 CDN 上，所以旧客户端继续用旧清单不受影响、可以灰度/回滚（把 `.version` 改回旧值就是回滚）；清理 CDN 上过老的版本由运维策略决定（本地构建器默认保留最近几个版本）。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkAssetBuilder.cs", "private static void CleanupOldVersions(", "旧版本清理策略"));
#if UNITY_EDITOR
            host.AddActionRow("打开本地 CDN 部署目录（看 <包>.version 文件）", () =>
                UnityEditor.EditorUtility.RevealInFinder(AssetBuildLayout.DeployRoot),
                new CodeRef("Assets/Game/Framework/Core/Asset/AssetBuildLayout.cs", "DeployRoot", "部署目录（单一真源）"));
#endif

            // ── 客户端侧：启动更新流程 ──
            host.AddSectionTitle("客户端侧：启动更新流程（强更）");
            var mode = settingsModel != null ? settingsModel.ActualPlayMode : asset.CurrentPlayMode;
            bool isReal = mode == AssetPlayMode.Host || mode == AssetPlayMode.Web;
            var modeBadge = new Label(isReal
                ? $"当前：{mode} —— 从 CDN 真实检查 / 下载，可完整观察链路"
                : $"当前：{mode} —— 资源全本地，流程照常跑通但恒为「无更新」（要看真实下载切 Host，见「YooAsset · 底层实现」章）");
            modeBadge.AddToClassList("demo-badge");
            if (isReal) modeBadge.AddToClassList("demo-badge--yes");
            host.Content.Add(modeBadge);

            var progressBar = new ProgressBar { lowValue = 0f, highValue = 1f };
            progressBar.style.marginBottom = 8;
            host.Content.Add(progressBar);
            var logLabel = host.AddValueDisplay("点「运行启动更新流程」——每一步会追加到这里。");
            logLabel.style.whiteSpace = WhiteSpace.Normal;

            var logLines = new List<string>();
            void AppendLog(string line)
            {
                logLines.Add(line);
                if (logLines.Count > 24) logLines.RemoveAt(0); // 只留最近的，防止无限长
                logLabel.text = string.Join("\n", logLines);
            }

            host.AddAsyncActionRow("▶ 运行启动更新流程（检查 → 下载 → 回收旧缓存）", async ct =>
            {
                if (packages.Count == 0) { AppendLog("没有可处理的包（场景缺 AssetSystemConfigModel）。"); return; }
                if (!_operationGate.TryEnter(out var lease)) { AppendLog("上一项资源操作仍在取消 / 收尾，请稍候。"); return; }
                using (lease)
                {
                    logLines.Clear();
                    progressBar.value = 0f;
                    progressBar.title = string.Empty;
                    bool ok = await RunUpdateFlow(asset, packages, AppendLog, r =>
                    {
                        progressBar.value = r.Progress;
                        progressBar.title = $"{r.Progress:P0}　{r.CurrentSizeMB}/{r.TotalSizeMB} MB";
                    }, ct);
                    AppendLog(ok
                        ? "★ 全部包已最新——启动器放行，进游戏。"
                        : "★ 有包未就绪——强更语义下挡在启动器，提示玩家「检查网络后重试」（重试就是再跑一遍本流程）。");
                    RefreshVersions();
                }
            }, CodeRef.Here("RunUpdateFlow(asset, packages", "按钮就是在跑活样板"));

            host.AddStep("①", "`Initialize` 拉取版本与清单；普通失败不抛异常，通过 `InitState` 判断是否就绪。");
            host.AddStep("②", "`CreateAllDownloader` 统计当前缓存缺口；`TotalCount == 0` 表示已经是最新版本。");
            host.AddStep("③", "订阅 `Progress` 驱动进度条，再执行 `Download`；整体失败时重建下载器重试，已完成分片会从缓存跳过。");
            host.AddStep("④", "下载成功后执行 `ClearCache(Unused)`，回收不再被新清单引用的旧版本 bundle。");
            host.AddSubNote("这四步只是把「资源加载」章的原子接口编排成启动流程。真实项目通常在第 ② 步后弹出“发现更新 X MB”的流量确认框，整体失败后提供“重试 / 退出”；用户拒绝下载或选择退出时，应明确结束启动流程。");
            host.AddSubNote("真实项目按需筛选进启动流程的包：启动必需的包（默认包 / 首场景包）走本流程强更；DLC / 大副本这类「按需下载」的包**不进**启动流程——进入对应玩法时再 `Initialize` + tag 下载器拉取（配合包级「自动初始化 / 按需下载」开关，见「资源加载」章）。");

            // ── 修复客户端：清空缓存全量重下 ──
            host.AddSectionTitle("修复客户端：清空缓存 → 重跑流程（= 设置页「修复资源」按钮）");
            var repairLabel = host.AddValueDisplay();
            host.AddExperimentNotice(
                "删除所有已 Ready 包的本地下载 bundle 缓存；这是跨 Play 持久化副作用，Host/Web 下会让对应内容重新变成待下载。",
                "结果区显示实际清理的包数；EditorSimulate/Offline 没有下载缓存，因此可能是安全的 no-op。",
                "随后重跑上方启动更新流程即可重新下载；正常更新只清 Unused，只有修复损坏或显式重置才用 All。");
            host.AddExperimentAsyncActionRow("清空全部包的下载缓存（ClearCache All）", async ct =>
            {
                if (!_operationGate.TryEnter(out var lease)) { repairLabel.text = "另一项资源操作进行中，稍候。"; return; }
                using (lease)
                {
                    int cleared = 0;
                    foreach (var pkg in packages)
                    {
                        // ClearCache 要求包已就绪；未就绪的包本来也没有可清的缓存，跳过即可。
                        if (asset.GetInitState(pkg).CurrentValue != AssetInitState.Ready) continue;
                        ct.ThrowIfCancellationRequested();
                        // 清理一旦交给 provider 就不能由页面取消安全中断；保持业务 gate 到物理操作结束，
                        // 再检查章节令牌，避免新流程与仍在收尾的清理重叠，也不向旧 UI 发布结果。
                        await asset.ClearCache(pkg, AssetCacheClearMode.All, CancellationToken.None);
                        ct.ThrowIfCancellationRequested();
                        cleared++;
                    }
                    repairLabel.text = $"已清空 {cleared} 个就绪包的下载缓存 ✓（Host 下对应地址重新成为 RequiresDownload）。现在点上方「运行启动更新流程」= 全量重下——这就是「修复资源 / 新装机首下」的完整路径。";
                }
            }, CodeRef.Here("asset.ClearCache(pkg, AssetCacheClearMode.All, CancellationToken.None)", "全清缓存"));
            host.AddNote("资源损坏（玩家手机存储出错 / 下载残缺）的标准恢复路径就是这两步：`ClearCache(All)` 删掉本地全部已下载 bundle → 重跑启动更新流程全量重下。设置页的「修复客户端」按钮背后就是它。`EditorSimulate` / `Offline` 下没有下载缓存，全清是 no-op。");
            host.AddSubNote("两条流程会改同一批包与缓存，必须共享一把互斥闸门；而且闸门要比一次 UI Build 活得久，避免界面重建后旧取消尚未收尾、新流程已经开跑。真实启动器通常把它建模成显式流程状态并统一置灰操作区。");

#if UNITY_EDITOR
            // ── 失败路径（仅 Editor）：断网下跑流程 ──
            host.AddSectionTitle("失败路径：断网下跑流程（仅 Editor 模拟）");
            host.AddExperimentNotice(
                "修改共享 AssetUtility 的 Editor 模拟断网开关，会影响本次 Play 的所有资源请求；已 Ready 的包不会回退。",
                "按钮文字与共享状态同步；只有之后新发起的 Host/Web 请求会被拦截，可用上方流程观察检查或下载失败。",
                "再次点击可手动还原；切离本章时也会自动恢复进入本章前的值。");
            Button offlineBtn = null;
            offlineBtn = host.AddExperimentActionRow("模拟断网", () =>
                asset.SetSimulateOffline(!asset.SimulateOffline.CurrentValue),
                CodeRef.Here("asset.SetSimulateOffline(!asset.SimulateOffline", "切换模拟断网"));
            Bag.Subscribe(asset.SimulateOffline, on =>
                offlineBtn.text = $"教学实验 · 模拟断网：{(on ? "开" : "关")}（点击切换，仅 Host/Web 生效）");
            host.AddSubNote("组合复现两条失败路径（Host 模式）：**检查失败**——进 Play 前就开断网（AssetUtility Inspector 勾选），流程第①步 `Initialize` 失败、整体报「检查网络后重试」；**下载失败**——先「清空全部包缓存」再开断网、跑流程：检查一步过（清单已在本地），下载一步三次重试全失败后报错。关掉断网再跑一遍即恢复；直接切离本章也会归还进入前的断网状态——就是玩家「修好网络点重试」的体验。");
#endif

            // ── 更新策略全景 ──
            host.AddSectionTitle("更新策略全景：本章流程在真实项目里的位置");
            host.AddConcept("资源强更（本章）", "启动器挡住：启动必需包更新完才进游戏。本章流程即此。");
            host.AddConcept("资源弱更 / 预下载", "不挡启动：只强更进主城所需 tag（`CreateTagDownloader`），其余内容进游戏后台慢慢下 / 进玩法前再下。同一套下载器 API，只是范围与时机不同。");
            host.AddConcept("代码热更", "改的是逻辑不是资源：走 HybridCLR 代码包（见「热更 · HybridCLR」章），与资源更新同套 CDN 结构、同一版本节奏发布。");
            host.AddConcept("安装包强更", "App 本体过旧须上商店更新——那是登录协议 / 服务器下发的判断（min_app_version），不归资源系统管；资源强更只管资源版本。");

            host.AddTip("串一遍完整体验：切 Host（构建→部署→起服务）→ 进 Play 跑流程（首次全量下）→ 退 Play 改资源 → 构建+部署（发新版本）→ 重进 Play 跑流程（只下增量、版本号变新、旧缓存被回收）。");
        }

        // ↓↓↓ 活样板：真实项目的「启动器资源更新流程」可整段搬走 ↓↓↓
        //
        // 入参都是业务侧现成的东西：资源入口 utility、要强更的包名列表（启动必需包，DLC 不进来）、
        // 日志与进度回调（接你的启动器 UI）、取消令牌（来自启动器 View / Context 生命周期）。
        // 返回 true = 全部包就绪且已最新，可以进游戏；false = 有包失败（弹「检查网络后重试 / 退出」，重试 = 再调一遍）。
        private static async UniTask<bool> RunUpdateFlow(
            IAssetUtility asset,
            IReadOnlyList<string> packages,
            Action<string> log,
            Action<DownloadProgressReport> progress,
            CancellationToken ct)
        {
            bool allOk = true;
            foreach (var pkg in packages)
            {
                // ① 检查更新：Initialize 拉最新版本号 + 清单（Host 联网；已 Ready 则幂等直返——版本只在启动时拉一次）。
                //    普通失败不抛、结果回写 InitState；调用者取消保持 OCE，物理初始化继续由 utility owner 持有。
                log($"[{pkg}] ① 检查更新（拉版本 / 清单）…");
                await asset.Initialize(pkg, ct);
                if (asset.GetInitState(pkg).CurrentValue != AssetInitState.Ready)
                {
                    log($"[{pkg}] ✗ 检查失败（CDN 不可达 / 断网）。");
                    allOk = false;
                    continue; // 其余包照常尝试，最后整体报失败
                }
                log($"[{pkg}] 清单就绪，版本 {asset.GetPackageVersion(pkg)}。");

                // ②③ 统计缺口并下载。下载器是「创建那一刻的待下载快照」且一次性：
                //    失败后必须重建再下（已下成功的分片已进缓存、会被跳过 = 断点续传），所以重试循环里每次都重新 Create。
                bool upToDate = false;
                for (int attempt = 1; attempt <= DownloadAttempts && !upToDate; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var dl = asset.CreateAllDownloader(pkg);
                    if (dl.TotalCount == 0)
                    {
                        log($"[{pkg}] ② 无缺口，已是最新。");
                        upToDate = true;
                        break;
                    }

                    // 真实项目在这里弹「发现更新 N 个 / X MB，是否下载」的流量 / Wi-Fi 确认框。
                    log($"[{pkg}] ② 发现更新：{dl.TotalCount} 个 / {dl.TotalBytes / 1048576f:0.00} MB，开始下载（第 {attempt}/{DownloadAttempts} 次）…");
                    using var sub = dl.Progress.Subscribe(progress); // 订阅随本次尝试走，失败重建时旧订阅一并释放
                    try
                    {
                        await dl.Download(ct); // 单文件失败下载器内部自动重试；整体最终失败才抛到这里
                        ct.ThrowIfCancellationRequested();
                        log($"[{pkg}] ③ 下载完成 ✓");
                        upToDate = true;
                    }
                    catch (OperationCanceledException) { throw; } // 取消向上传递，不当失败重试
                    catch (Exception e)
                    {
                        log($"[{pkg}] ③ 下载失败：{e.Message}");
                    }
                }
                if (!upToDate)
                {
                    log($"[{pkg}] ✗ 重试 {DownloadAttempts} 次仍失败。");
                    allOk = false;
                    continue;
                }

                // ④ 回收旧版本：清掉「不被当前清单引用」的历史 bundle（发过几次版本后才有内容，无残留时是 no-op）。
                //    放在下载成功之后：确认新版本可用了才丢旧的。
                // 维护调用的 waiter token 取消后会提前返回，但物理清理仍继续；启动器状态机必须等到真正结束才放行下一流程。
                ct.ThrowIfCancellationRequested();
                await asset.ClearCache(pkg, AssetCacheClearMode.Unused, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                log($"[{pkg}] ④ 旧版本缓存已回收 ✓");
            }
            return allOk;
        }
    }
}
