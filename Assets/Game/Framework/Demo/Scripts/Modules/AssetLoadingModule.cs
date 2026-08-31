using System;
using Game.Framework;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·资源加载：聚焦资源包就绪、按地址加载、句柄所有权和显式包名。
    /// Inspector 引用与 ScriptableObject 配置见「资源引用」章，查询、下载与缓存见「资源分发」章。
    /// </summary>
    public sealed class AssetLoadingModule : DemoModuleBase
    {
        public override string Id => "asset-loading";
        public override string Title => "资源加载 · 就绪与生命周期";
        public override string Category => "能力";
        public override int Order => 20;
        public override string Summary =>
            "先让资源包进入 Ready，再用 Bag.Load 借资源并随宿主释放；同时区分资源缺失与系统未就绪两套失败语义。引用配置、下载缓存已拆到相邻章节。";

        private const string LogoAddress = "SSFramework-Logo";
        private const string SamplesPackage = "FrameworkSamplesPackage";
        private const string MissingAddress = "__不存在的地址__";

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            var assetUtility = asset as AssetUtility;
            AssetRuntimeSettings settings = assetUtility?.Settings;
#if UNITY_EDITOR
            // 模拟断网属于共享 Utility 的调试状态；离章归还，不能让一个教学实验污染后续章节。
            var previousSimulateOffline = asset.SimulateOffline.CurrentValue;
            Bag.Add(Disposable.Create(() => asset.SetSimulateOffline(previousSimulateOffline)));
#endif

            host.AddPositioning("先建立就绪门，再借资源；句柄所有权跟着宿主走");
            host.AddNote("本章只回答三个基础问题：包何时可用、资源怎么加载、何时释放。`IAssetUtility` 管包状态与底层适配，业务 View / System 通常把借来的资源交给 `DisposableBag`；切章、关窗口或销毁 Context 时，Bag 会统一释放句柄。");

            // ── 1. 就绪门 ──
            host.AddSectionTitle("第一步：观察状态，再显式建立 Ready 门");
            var stateLabel = host.AddValueDisplay("", CodeRef.Here("Bag.Subscribe(asset.InitState", "订阅默认包状态"));
            var defaultState = AssetInitState.Idle;
            Bag.Subscribe(asset.InitState, state =>
            {
                defaultState = state;
                stateLabel.text = state == AssetInitState.Failed
                    ? $"默认包：Failed　｜　模式：{asset.CurrentPlayMode}。修复网络 / CDN 后可再次 Initialize。"
                    : $"默认包：{state}　｜　模式：{asset.CurrentPlayMode}";
            });

            string defaultPackage = settings?.DefaultPackageName;
            bool autoInitialize = settings != null && !string.IsNullOrWhiteSpace(defaultPackage)
                                  && settings.ShouldAutoInitialize(defaultPackage);
            var autoBadge = new Label(autoInitialize
                ? "默认包自动初始化：开 —— 进 Play 后会主动建立就绪门"
                : "默认包自动初始化：关 —— 由业务在合适时机调用 Initialize");
            autoBadge.AddToClassList("demo-badge");
            autoBadge.AddToClassList(autoInitialize ? "demo-badge--yes" : "demo-badge--no");
            host.Content.Add(autoBadge);
            host.AddNote("`Idle` 表示尚未安排初始化，直接 Load 会快速抛出“未初始化”，避免业务无期限等待。`Pending / Initializing` 表示同一次初始化正在排队或执行，Load 会等待它；`Failed` 保留本次失败；`Ready` 才进入正常加载路径。自动初始化按包配置，适合启动必需包；隐私同意后联网或 DLC 懒加载则更适合显式 `Initialize`。");
#if UNITY_EDITOR
            host.AddActionRow("定位资源入口 AssetUtility", () =>
            {
                if (assetUtility != null) DemoEditorNav.PingSceneObject(assetUtility.gameObject);
            });
            host.AddExperimentNotice(
                "切换共享 AssetUtility 的模拟断网，只影响之后新发起的 Host / Web 请求；已经 Ready 的包不会回退。",
                "用它观察初始化失败和重试；EditorSimulate / Offline 不访问远端，因此不会产生真实断网效果。",
                "再次点击可手动恢复；切离本章也会自动还原进入本章前的值。");
            Button offlineButton = null;
            offlineButton = host.AddExperimentActionRow("模拟断网", () =>
                asset.SetSimulateOffline(!asset.SimulateOffline.CurrentValue),
                CodeRef.Here("asset.SetSimulateOffline(!asset.SimulateOffline", "切换模拟断网"));
            Bag.Subscribe(asset.SimulateOffline, enabled =>
                offlineButton.text = $"教学实验 · 模拟断网：{(enabled ? "开" : "关")}（仅 Host / Web）");
#endif

            var initResult = host.AddValueDisplay("默认包为 Idle / Failed 时，从这里显式启动或重试初始化。");
            host.AddExperimentNotice(
                "仅在默认包为 Idle 时发起一次加载，验证 fail-fast 契约；不会改写资源配置。",
                "只把框架定义的“未初始化”异常当作教学预期，其他异常仍交给 Host 暴露。",
                "随后点击 Initialize，等待上方状态变为 Ready。");
            host.AddExperimentAsyncActionRow("未初始化时尝试加载（本地捕获）", async ct =>
            {
                if (defaultState != AssetInitState.Idle)
                {
                    initResult.text = $"当前为 {defaultState}，已不属于 Idle fail-fast 场景；重新进入 Play 且先不 Initialize 可复现。";
                    return;
                }

                try
                {
                    await Bag.Load<Sprite>(LogoAddress, ct);
                    initResult.text = "请求期间包已经转为 Ready，因此没有触发未初始化异常。";
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidOperationException e) when (IsExpectedUninitializedFailure(e))
                {
                    initResult.text = $"[教学预期] {e.Message}　下一步：Initialize → 等 Ready → 再 Load。";
                }
            }, CodeRef.Here("catch (InvalidOperationException e) when (IsExpectedUninitializedFailure(e))", "精确识别未初始化契约"));
            host.AddAsyncActionRow("初始化 / 失败后重试（Initialize）", async ct =>
            {
                initResult.text = "初始化中…";
                await asset.Initialize(ct: ct);
                initResult.text = defaultState == AssetInitState.Ready
                    ? "默认包已经 Ready ✓"
                    : $"初始化结束，当前状态：{defaultState}。普通失败写回状态；需要根因时再 await EnsureInitialized。";
            }, CodeRef.Here("await asset.Initialize(ct: ct)", "冷启动或重试默认包"));
            host.AddSubNote("`Initialize` 负责启动 / 重试，普通失败写回状态而不抛；命令式流程若必须得到失败根因，可在它之后 `await EnsureInitialized`。调用者取消只离开自己的等待，已经提交给 Utility 的物理初始化仍会走到真实终态。");

            // ── 2. 借用与释放 ──
            host.AddSectionTitle("第二步：Bag.Load 借资源，子 Bag 提前归还");
            var preview = NewPreview();
            host.Content.Add(preview);
            var loadResult = host.AddValueDisplay("先确认默认包 Ready，再加载 Logo。");
            var logoBag = Bag.CreateChild();
            host.AddAsyncActionRow("加载 Logo（Bag.Load<Sprite>）", async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    loadResult.text = "默认包尚未 Ready，请先点击上方 Initialize。";
                    return;
                }

                var sprite = await logoBag.Load<Sprite>(LogoAddress, ct);
                if (sprite == null)
                {
                    loadResult.text = "资源加载返回 null，请检查地址、类型与资源收集配置。";
                    return;
                }

                preview.style.backgroundImage = new StyleBackground(sprite);
                loadResult.text = $"已借到 {sprite.name}（{sprite.rect.width:0}×{sprite.rect.height:0}）；句柄由本节子 Bag 持有。";
            }, CodeRef.Here("logoBag.Load<Sprite>(LogoAddress, ct)", "资源句柄进入子 Bag"));
            host.AddActionRow("释放本节资源（Dispose 子 Bag）", () =>
            {
                logoBag.Dispose();
                logoBag = Bag.CreateChild();
                preview.style.backgroundImage = StyleKeyword.None;
                loadResult.text = "句柄已释放，预览也已清空；可以再次加载。";
            }, CodeRef.Here("logoBag.Dispose()", "提前释放一组句柄"));
            host.AddAsyncActionRow("卸载引用归零的内存 bundle", async ct =>
            {
                await asset.UnloadUnusedAssets(ct);
                ct.ThrowIfCancellationRequested();
                loadResult.text = "已卸载引用归零的 bundle。仍被其他 Bag / 引用持有的资源不会受影响。";
            }, CodeRef.Here("asset.UnloadUnusedAssets(ct)", "回收零引用内存 bundle"));
            host.AddNote("释放有三层：`Dispose / Unload` 先归还 handle；`UnloadUnusedAssets` 再把零引用 bundle 从内存卸掉；`ClearCache` 删除的是磁盘下载缓存，属于相邻的「资源分发」章。把短期资源放进子 Bag，既能随宿主兜底释放，也能在阶段结束时就近提前归还。直接持有 handle 时，其属性、Dispose、场景 Activate / UnSuspend 都从 Unity 主线程调用；显式 await 场景 Unload 后也会回到主线程。");

            // ── 3. 失败语义 ──
            host.AddSectionTitle("第三步：分清资源级缺失与系统级失败");
            var failureResult = host.AddValueDisplay("以下实验只在包 Ready 后执行，因此不会混入初始化异常。");
            host.AddExperimentNotice(
                "用不存在的地址或错误类型发起加载；不修改资源、缓存或配置。",
                "资源级失败返回 null 并记录日志，让玩家流程可以用占位资源继续，同时让开发者看到配置错误。",
                "无需恢复；换回有效地址和类型即可。");
            host.AddExperimentAsyncActionRow("加载不存在的地址（→ null）", async ct =>
            {
                if (defaultState != AssetInitState.Ready) { failureResult.text = "请先 Initialize 到 Ready。"; return; }
                var sprite = await Bag.Load<Sprite>(MissingAddress, ct);
                failureResult.text = sprite == null
                    ? "[教学预期] 地址不在 manifest：返回 null，并在 Console 暴露配置问题。"
                    : $"意外加载到：{sprite.name}";
            }, CodeRef.Here("Bag.Load<Sprite>(MissingAddress, ct)", "无效地址返回 null"));
            host.AddExperimentAsyncActionRow("把 Logo 当 AudioClip 加载（→ null）", async ct =>
            {
                if (defaultState != AssetInitState.Ready) { failureResult.text = "请先 Initialize 到 Ready。"; return; }
                var clip = await Bag.Load<AudioClip>(LogoAddress, ct);
                failureResult.text = clip == null
                    ? "[教学预期] 类型不匹配：返回 null，并在 Console 暴露错误类型。"
                    : $"意外加载到：{clip.name}";
            }, CodeRef.Here("Bag.Load<AudioClip>(LogoAddress, ct)", "类型不匹配返回 null"));
            host.AddTable(
                new[] { "失败层级", "例子", "框架行为", "调用方处理" },
                new[] { "资源级", "地址缺失 / 类型不符", "Load 返回 null + 日志", "null 检查 + 占位资源" },
                new[] { "系统级", "包未初始化 / 初始化失败", "等待或抛原始异常", "先建立 Ready 门，失败时重试 / 提示" });
            host.AddTip("记忆点：先把流程挡在 Ready 门外；进入 Ready 以后，普通加载只需处理资源级的 null。不要用一个宽泛 catch 把配置缺失、网络失败和生命周期取消混成同一种结果。");

            // ── 4. 显式包名 ──
            host.AddSectionTitle("多包项目：显式写出资源来自哪个包");
            var packagePreview = NewPreview();
            host.Content.Add(packagePreview);
            var packageResult = host.AddValueDisplay();
            var packageBag = Bag.CreateChild();
            host.AddAsyncActionRow("从 FrameworkSamplesPackage 加载 Logo", async ct =>
            {
                var packageState = asset.GetInitState(SamplesPackage).CurrentValue;
                if (packageState != AssetInitState.Ready)
                {
                    packageResult.text = $"包「{SamplesPackage}」当前为 {packageState}，请先初始化该包。";
                    return;
                }

                var sprite = await packageBag.Load<Sprite>(SamplesPackage, LogoAddress, ct);
                if (sprite != null)
                {
                    packagePreview.style.backgroundImage = new StyleBackground(sprite);
                    packageResult.text = $"已从「{SamplesPackage}」加载 {sprite.name}。";
                }
            }, CodeRef.Here("packageBag.Load<Sprite>(SamplesPackage, LogoAddress, ct)", "显式包名重载"));
            host.AddActionRow("释放显式包名加载", () =>
            {
                packageBag.Dispose();
                packageBag = Bag.CreateChild();
                packagePreview.style.backgroundImage = StyleKeyword.None;
                packageResult.text = "已释放本节句柄。";
            });
            host.AddNote("省略包名的重载落到默认包；跨包时显式传 `packageName`，让依赖来源在调用点可读。本 Demo 只有一个包，因此这里只诚实展示重载形式，不伪造跨包结果。正式项目可由资源构建配置生成包名常量，避免散落字符串。");

            // ── 5. 边界 ──
            host.AddSectionTitle("边界与下一步");
            host.AddConcept("IAssetUtility", "包初始化、版本、位置、下载与缓存的框架入口；底层 provider 可替换。");
            host.AddConcept("DisposableBag", "消费侧的资源所有权入口；Load / LoadScene 返回的 handle 随 Bag 释放。");
            host.AddConcept("AssetReference", "Inspector 拖拽引用与 SO 配置的所有权规则，见下一章「资源引用 · Inspector 与配置」。");
            host.AddConcept("下载器", "预下载、进度、缓存清理与快照失效，见「资源分发 · 下载与缓存」。");
            host.AddNote("`AssetUtility` 是场景侧基础设施入口，`IAssetProvider` 是 Core 与 YooAsset / Addressables 等实现之间的 seam。业务只依赖稳定 Interface；替换 Adapter 时不需要改这些加载调用。", new CodeRef("Assets/Game/Framework/Asset.Yoo/AssemblyInfo.cs", "DefaultAssetProvider", "默认 Provider 由 Adapter 注册"));
        }

        private static VisualElement NewPreview(int size = 120)
        {
            var preview = new VisualElement();
            preview.style.width = size;
            preview.style.height = size;
            preview.style.marginBottom = 8;
            preview.style.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
            return preview;
        }

        private static bool IsExpectedUninitializedFailure(InvalidOperationException exception)
        {
            if (exception == null) return false;
            return exception.Message.IndexOf("未初始化", StringComparison.Ordinal) >= 0 &&
                   exception.Message.IndexOf("Initialize", StringComparison.Ordinal) >= 0;
        }
    }
}
