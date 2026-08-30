using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·资源分发：聚焦位置快照、三种下载范围、进度、失败重建与磁盘缓存清理。
    /// </summary>
    public sealed class AssetDownloadCacheModule : DemoModuleBase
    {
        public override string Id => "asset-download-cache";
        public override string Title => "资源分发 · 下载与缓存";
        public override string Category => "能力";
        public override int Order => 22;
        public override string Summary =>
            "用四态快照判断资源是否在本地，再按 tag、全部或地址创建一次性下载器；缓存清理后必须重建快照，取消等待不等于强停物理操作。";

        private const string LogoAddress = "SSFramework-Logo";
        private const string DemoTag = "framework-demo";
        private readonly DemoOperationGate _downloadOperationGate = new();

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();

            host.AddPositioning("先查询位置，再按范围预下载；缓存变化后重建快照");
            host.AddNote("`Load` 关注“我要这个资源”，下载器关注“在进入某功能前把一批内容准备好”。本章把位置查询、预下载进度和磁盘缓存放在一条链上，并刻意区分内存 handle、bundle 内存与本地下载缓存三个层次。");

            // ── 1. 就绪与模式 ──
            host.AddSectionTitle("开始前：包 Ready，运行模式决定是否真的走网络");
            var stateLabel = host.AddValueDisplay();
            var defaultState = AssetInitState.Idle;
            Bag.Subscribe(asset.InitState, state =>
            {
                defaultState = state;
                stateLabel.text = $"默认包：{state}　｜　运行模式：{asset.CurrentPlayMode}";
            });
            host.AddAsyncActionRow("初始化 / 重试默认包", async ct =>
            {
                await asset.Initialize(ct: ct);
                stateLabel.text = defaultState == AssetInitState.Ready
                    ? $"默认包：Ready ✓　｜　运行模式：{asset.CurrentPlayMode}"
                    : $"初始化结束，当前状态：{defaultState}";
            }, CodeRef.Here("await asset.Initialize(ct: ct)", "本章直达初始化入口"));

            bool realDownload = asset.CurrentPlayMode == AssetPlayMode.Host || asset.CurrentPlayMode == AssetPlayMode.Web;
            var modeBadge = new Label(realDownload
                ? $"当前 {asset.CurrentPlayMode}：有效但未缓存的内容会从远端下载"
                : $"当前 {asset.CurrentPlayMode}：内容来自本地，下载器通常统计为 0");
            modeBadge.AddToClassList("demo-badge");
            modeBadge.AddToClassList(realDownload ? "demo-badge--yes" : "demo-badge--no");
            host.Content.Add(modeBadge);
            host.AddSubNote("EditorSimulate / Offline 可以验证 API 与状态编排，但看不到真实下载进度。要观察远端请求，切到 Host 并先完成资源构建、部署和本地 CDN 服务；底层目录与清单原理见「YooAsset · 底层实现」。");

            // ── 2. 查询 ──
            host.AddSectionTitle("位置查询：一个四态快照替代两个布尔猜测");
            var locationBadge = new Label("点击下方按钮查询 Logo");
            locationBadge.AddToClassList("demo-badge");
            host.Content.Add(locationBadge);

            void ShowLocation(AssetLocationState state)
            {
                locationBadge.RemoveFromClassList("demo-badge--yes");
                locationBadge.RemoveFromClassList("demo-badge--no");
                bool available = state == AssetLocationState.AvailableLocally;
                locationBadge.AddToClassList(available ? "demo-badge--yes" : "demo-badge--no");
                locationBadge.text = state switch
                {
                    AssetLocationState.PackageNotReady => $"PackageNotReady：包当前为 {defaultState}",
                    AssetLocationState.Invalid => "Invalid：地址为空或 manifest 中不存在",
                    AssetLocationState.AvailableLocally => "AvailableLocally ✓：已内置或已缓存",
                    AssetLocationState.RequiresDownload => "RequiresDownload ↓：地址有效，但需要远端下载",
                    _ => $"未知状态：{state}",
                };
            }

            host.AddActionRow("GetLocationState(Logo)", () =>
            {
                if (_downloadOperationGate.IsEntered)
                {
                    locationBadge.text = "下载 / 缓存维护仍在执行，请在终态后重新查询。";
                    return;
                }
                ShowLocation(asset.GetLocationState(LogoAddress));
            }, CodeRef.Here("ShowLocation(asset.GetLocationState(LogoAddress))", "四态位置快照"));
            host.AddTable(
                new[] { "状态", "回答的问题", "下一步" },
                new[] { "PackageNotReady", "包还不能解释这个地址", "查看 AssetInitState，初始化或重试" },
                new[] { "Invalid", "当前 manifest 没有这个地址", "修正地址 / 包名 / 收集配置" },
                new[] { "AvailableLocally", "内容已内置或缓存", "可以直接 Load" },
                new[] { "RequiresDownload", "地址有效但内容不在本地", "按需 Load，或先显式下载" });
            host.AddNote("位置和生命周期是两个正交问题：`AssetLocationState` 回答内容在哪里，`AssetInitState` 回答包为什么还不能工作。把它们合成一个不断膨胀的枚举会让高频分支更难读，因此未就绪时只返回 `PackageNotReady`，需要诊断再读取包状态。");

            // ── 3. 下载器 ──
            host.AddSectionTitle("下载器：创建时统计快照，Download 才启动物理下载");
            var progressResult = host.AddValueDisplay("先选择范围创建下载器，再启动下载。");
            var progressBar = new ProgressBar { lowValue = 0f, highValue = 1f };
            progressBar.style.marginBottom = 8;
            host.Content.Add(progressBar);
            IAssetDownloader downloader = null;

            bool CanCreateSnapshot()
            {
                if (defaultState != AssetInitState.Ready)
                {
                    progressResult.text = $"默认包尚未 Ready（当前 {defaultState}），请先初始化。";
                    return false;
                }
                if (_downloadOperationGate.IsEntered)
                {
                    progressResult.text = "下载或缓存维护仍在执行，不能从变化中的缓存生成快照。";
                    return false;
                }
                return true;
            }

            void BindDownloader(IAssetDownloader next, string scope)
            {
                downloader = next;
                Bag.Subscribe(next.Progress, report =>
                {
                    progressBar.value = report.Progress;
                    progressBar.title = $"{report.Progress:P0}　{report.CurrentSizeMB}/{report.TotalSizeMB} MB";
                });
                progressResult.text = next.TotalCount == 0
                    ? $"{scope}：无需下载（内容已内置 / 已缓存，或范围没有命中）。"
                    : $"{scope}：待下载 {next.TotalCount} 个，合计 {next.TotalBytes / 1048576f:0.00} MB。";
            }

            host.AddActionRow("创建下载器 · 按 tag", () =>
            {
                if (CanCreateSnapshot()) BindDownloader(asset.CreateTagDownloader(DemoTag), $"tag「{DemoTag}」");
            }, CodeRef.Here("asset.CreateTagDownloader(DemoTag)", "按逻辑内容组预下载"));
            host.AddActionRow("创建下载器 · 全部", () =>
            {
                if (CanCreateSnapshot()) BindDownloader(asset.CreateAllDownloader(), "默认包全部内容");
            }, CodeRef.Here("asset.CreateAllDownloader()", "整包预下载"));
            host.AddActionRow("创建下载器 · 按地址", () =>
            {
                if (CanCreateSnapshot()) BindDownloader(asset.CreateLocationDownloader(LogoAddress), $"地址「{LogoAddress}」");
            }, CodeRef.Here("asset.CreateLocationDownloader(LogoAddress)", "点名资源及依赖"));

            async UniTask RunExclusive(CancellationToken ct, Func<CancellationToken, UniTask> operation)
            {
                if (defaultState != AssetInitState.Ready)
                {
                    progressResult.text = $"默认包尚未 Ready（当前 {defaultState}），请先初始化。";
                    return;
                }
                if (!_downloadOperationGate.TryEnter(out var lease))
                {
                    progressResult.text = "另一个下载 / 缓存操作仍在执行或收尾，请稍候。";
                    return;
                }

                using (lease)
                {
                    await operation(ct);
                }
            }

            host.AddAsyncActionRow("开始下载（Download）", chapterCt => RunExclusive(chapterCt, async ct =>
            {
                var current = downloader;
                if (current == null) { progressResult.text = "请先创建下载器。"; return; }
                progressResult.text = "下载中…";
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // 底层下载一旦提交，章节取消只表示旧 View 不再等待/发布结果；模块级 gate 必须覆盖物理终态，
                    // 否则新 UI 可在旧下载仍改缓存时创建一份错误快照。
                    await current.Download(CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                    progressResult.text = $"下载完成 ✓（{current.TotalCount} 个 / {current.TotalBytes / 1048576f:0.00} MB）";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    ct.ThrowIfCancellationRequested();
                    progressResult.text = $"下载失败：{e.Message}。修复网络后要重新创建下载器；失败实例是一次性的。";
                }
            }), CodeRef.Here("await current.Download(CancellationToken.None)", "等待物理下载终态"));
            host.AddNote("下载器是“创建那一刻”的待下载快照。失败后重复调用同一个实例只会重现失败；应修复网络后重建，已经成功的分片会命中缓存，相当于断点续传。进度是状态流，View 订阅即可，不需要 `while (!IsDone)` 轮询。");

            // ── 4. 缓存维护 ──
            host.AddSectionTitle("缓存维护：范围和下载器对应，清理后旧快照作废");
            host.AddExperimentNotice(
                "删除的是玩家设备上的下载 bundle 缓存，不是项目源资源，也不等于释放内存中的 handle。",
                "Host / Web 下相关地址可能回到 RequiresDownload；EditorSimulate / Offline 通常仍然本地可用。",
                "清理完成后重新创建下载器并下载即可恢复；日常回收旧版本优先使用 Unused。");

            void ResetSnapshot(string result)
            {
                downloader = null;
                progressBar.value = 0f;
                progressBar.title = string.Empty;
                progressResult.text = result + "　旧下载器已丢弃，请重新创建快照。";
            }

            host.AddExperimentAsyncActionRow("清空全部下载缓存（All）", chapterCt => RunExclusive(chapterCt, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                await asset.ClearCache(AssetCacheClearMode.All, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                ResetSnapshot($"全部缓存已清理；Logo={asset.GetLocationState(LogoAddress)}。");
            }), CodeRef.Here("asset.ClearCache(AssetCacheClearMode.All, CancellationToken.None)", "全量修复 / 强制重下"));
            host.AddExperimentAsyncActionRow("回收旧版本缓存（Unused）", chapterCt => RunExclusive(chapterCt, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                await asset.ClearCache(AssetCacheClearMode.Unused, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                ResetSnapshot("未被当前清单引用的旧 bundle 已回收。");
            }), CodeRef.Here("asset.ClearCache(AssetCacheClearMode.Unused, CancellationToken.None)", "回收旧版本残留"));
            host.AddExperimentAsyncActionRow("按 tag 清缓存", chapterCt => RunExclusive(chapterCt, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                await asset.ClearCacheByTags(new[] { DemoTag }, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                ResetSnapshot($"tag「{DemoTag}」命中的 bundle 已清理。");
            }), CodeRef.Here("asset.ClearCacheByTags(new[] { DemoTag }, CancellationToken.None)", "按逻辑内容组清理"));
            host.AddExperimentAsyncActionRow("按地址清缓存（Logo 所在 bundle）", chapterCt => RunExclusive(chapterCt, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                await asset.ClearCacheByLocations(new[] { LogoAddress }, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                ResetSnapshot("Logo 所在 bundle 已清理；同 bundle 邻居会被连带清理。");
            }), CodeRef.Here("asset.ClearCacheByLocations(new[] { LogoAddress }, CancellationToken.None)", "按地址解析到 bundle 后清理"));
            host.AddTable(
                new[] { "范围", "下载", "清理", "典型用途" },
                new[] { "tag", "CreateTagDownloader", "ClearCacheByTags", "关卡 / DLC 内容组" },
                new[] { "全部", "CreateAllDownloader", "ClearCache(All / Unused)", "首包预下载 / 修复 / 回收旧版" },
                new[] { "地址", "CreateLocationDownloader", "ClearCacheByLocations", "点名少量资源及依赖" });
            host.AddSubNote("按 tag 的多个标签取并集；按地址清理也只能落到 bundle 粒度，同 bundle 的其他资源会被连带删除。若产品要求真正独立驱逐某项资源，应在打包阶段让它独占 bundle，而不是在运行时 API 里伪造资源级精度。");

            // ── 5. 取消与边界 ──
            host.AddSectionTitle("取消语义与产品边界");
            host.AddConcept("取消等待", "切章后当前 View 不再接收结果，并抛 OperationCanceledException；这是生命周期控制流。");
            host.AddConcept("物理终态", "底层下载 / 清理一旦提交，可能仍继续到成功或失败；Utility / provider 负责观察真实终态。");
            host.AddConcept("业务互斥", "同组下载与缓存维护共享模块级 gate，避免两个按钮同时改同一份缓存与快照。");
            host.AddNote("“我不再等待”不等于“网络已经停了”。本章清缓存一旦提交就用 `CancellationToken.None` 等到物理终态，再检查章节 token，因此 gate 不会在磁盘仍变化时提前放行。若产品需要暂停、限速或真正停止流量，那是下载器 Interface 的新能力，应显式设计，不能借 OCE 假装已经实现。");
            host.AddTip("推荐顺序：包 Ready → GetLocationState 判断 → 创建合适范围的下载器 → 展示总量 / 流量确认 → Download → 失败时重建；任何缓存维护后，都从“重新创建下载器”开始。完整启动器编排见「资源运营 · 端到端」。");
        }
    }
}
