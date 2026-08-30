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
    /// 能力·资源引用：聚焦 Inspector 拖拽引用、Mono 自动绑定，以及 ScriptableObject 配置的显式所有权。
    /// </summary>
    public sealed class AssetReferenceModule : DemoModuleBase
    {
        public override string Id => "asset-references";
        public override string Title => "资源引用 · Inspector 与配置";
        public override string Category => "能力";
        public override int Order => 21;
        public override string Summary =>
            "用 AssetReference 把 Inspector 资源拖拽转为类型安全引用；Mono 字段自动绑定，ScriptableObject 由持有者用 Bag.BindAssetReferences 显式接管。";

        private const string ConfigAddress = "DemoAssetConfig";
        private readonly DemoOperationGate _configOperationGate = new();

        public override void Build(DemoModuleHost host)
        {
            var asset = this.GetUtility<IAssetUtility>();
            // DemoAssetRefs 是当前 Context 内的场景 Adapter。这里故意不用 FindFirstObjectByType：
            // 多场景 / 多 Context 时，全局扫描无法证明拿到的是本章节所属实例。
            var refs = this.GetUtility<DemoAssetRefs>();

            host.AddPositioning("把“资源在哪”留给 Inspector，把“谁负责释放”留给持有者");
            host.AddNote("字符串地址适合动态内容，`AssetReference<T>` 适合静态可配置引用：Inspector 拖入资源，序列化 GUID，运行时仍从框架资源系统加载。关键不在“少写一个地址”，而在引用类型、加载入口和生命周期所有权被放到同一个可检查对象上。");

            // ── 1. 直达章节也能自洽 ──
            host.AddSectionTitle("开始前：确认默认包已经 Ready");
            var stateLabel = host.AddValueDisplay();
            var defaultState = AssetInitState.Idle;
            Bag.Subscribe(asset.InitState, state =>
            {
                defaultState = state;
                stateLabel.text = $"默认包：{state}　｜　{(state == AssetInitState.Ready ? "可以读取引用" : "请先初始化")}";
            });
            host.AddAsyncActionRow("初始化 / 重试默认包", async ct =>
            {
                await asset.Initialize(ct: ct);
                stateLabel.text = defaultState == AssetInitState.Ready
                    ? "默认包：Ready ✓　可以继续下面的引用实验。"
                    : $"初始化结束，当前状态：{defaultState}。";
            }, CodeRef.Here("await asset.Initialize(ct: ct)", "本章直达初始化入口"));
            host.AddSubNote("目录允许从任意章节进入，因此每个需要资源的章节都给出一个最小就绪入口；完整状态机、失败与重试原理见上一章「资源加载 · 就绪与生命周期」。");

            // ── 2. Mono 自动绑定 ──
            host.AddSectionTitle("Mono 场景 Adapter：Awake 自动绑定，销毁自动释放");
            var referenceResult = host.AddValueDisplay("DemoAssetRefs 已从当前 GameContext 解析；点击按钮读取 Inspector 中拖好的引用。");
            var referenceArea = new VisualElement();
            referenceArea.style.flexDirection = FlexDirection.Row;
            referenceArea.style.flexWrap = Wrap.Wrap;
            referenceArea.style.marginBottom = 8;
            host.Content.Add(referenceArea);

            void ShowSprites(params Sprite[] sprites)
            {
                referenceArea.Clear();
                foreach (var sprite in sprites)
                {
                    if (sprite == null) continue;
                    var preview = NewPreview();
                    preview.style.marginRight = 6;
                    preview.style.backgroundImage = new StyleBackground(sprite);
                    referenceArea.Add(preview);
                }
            }

            host.AddAsyncActionRow("Get() 单个 Logo 引用", async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    referenceResult.text = "默认包尚未 Ready，请先点击上方初始化。";
                    return;
                }

                var sprite = await refs.LogoRef.Get(ct);
                if (sprite == null)
                {
                    referenceResult.text = "LogoRef 未配置，或引用资源加载失败。";
                    return;
                }

                ShowSprites(sprite);
                referenceResult.text = $"LogoRef.Get() 得到：{sprite.name}";
            }, CodeRef.Here("refs.LogoRef.Get(ct)", "读取单个拖拽引用"));
            host.AddAsyncActionRow("GetAll() 并行读取引用列表", async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    referenceResult.text = "默认包尚未 Ready，请先点击上方初始化。";
                    return;
                }

                var sprites = await refs.LogoList.GetAll(ct);
                ShowSprites(sprites);
                referenceResult.text = $"LogoList.GetAll() 得到 {sprites.Length} 张图片。";
            }, CodeRef.Here("refs.LogoList.GetAll(ct)", "并行读取引用列表"));
            host.AddActionRow("释放本节引用（Unload / UnloadAll）", () =>
            {
                refs.LogoRef.Unload();
                refs.LogoList.UnloadAll();
                referenceArea.Clear();
                referenceResult.text = "资源 handle 已释放，预览引用也已清空；可再次 Get。";
            }, CodeRef.Here("refs.LogoList.UnloadAll()", "统一释放引用列表"));
            host.AddNote("`DemoAssetRefs` 使用 `MonoUtilityBase`，因为它是“把 Unity 序列化配置接到 Context”的基础设施 Adapter，不是业务 Model。基类在 Awake 把字段绑定到同一 Context 的 `IAssetUtility` 并登记进自己的 Bag；组件销毁时统一释放。章节也从当前 Context 解析它，避免全局对象扫描在多场景时串作用域。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoAssetRefs.cs", "class DemoAssetRefs", "场景资源引用 Adapter"));
#if UNITY_EDITOR
            host.AddActionRow("定位 DemoAssetRefs 场景节点", () => DemoEditorNav.PingSceneObject(refs.gameObject));
#endif

            // ── 3. ScriptableObject 显式所有权 ──
            host.AddSectionTitle("ScriptableObject：谁加载配置，谁绑定并拥有内部引用");
            var configResult = host.AddValueDisplay("按 ① → ② → ③ 观察 SO 与 Mono 的所有权差异。");
            var configPreview = NewPreview();
            host.Content.Add(configPreview);
            DemoAssetConfig loadedConfig = null;
            var configBag = Bag.CreateChild();
            var referencesBound = false;

            async UniTask RunConfigOperation(CancellationToken ct, Func<CancellationToken, UniTask> operation)
            {
                if (!_configOperationGate.TryEnter(out var lease))
                {
                    configResult.text = "另一个配置操作仍在执行或收尾，请稍候。";
                    return;
                }

                using (lease)
                {
                    await operation(ct);
                }
            }

            host.AddAsyncActionRow("① 加载 DemoAssetConfig", chapterCt => RunConfigOperation(chapterCt, async ct =>
            {
                if (defaultState != AssetInitState.Ready)
                {
                    configResult.text = "默认包尚未 Ready，请先点击上方初始化。";
                    return;
                }

                var requestBag = configBag;
                ct.ThrowIfCancellationRequested();
                // 请求一旦交给子 Bag，就由子 Bag / Context owner 持有到本次 owner 请求的终态；章节 token 只控制旧 UI 是否接收结果。
                // provider 的共享物理加载可能继续，但晚到 handle 会安全释放；gate 覆盖旧 Build 的 owner 收尾，避免新 Build 复用同一 SO 时交叠。
                var config = await requestBag.Load<DemoAssetConfig>(ConfigAddress, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                if (requestBag.IsDisposed) return;
                loadedConfig = config;
                referencesBound = false;
                configResult.text = config == null
                    ? "配置加载失败，请检查 DemoAssetConfig 是否进入 FrameworkSamplesPackage。"
                    : $"已加载 {config.name}；内部引用还没有交给当前子 Bag。";
            }), CodeRef.Here("requestBag.Load<DemoAssetConfig>(ConfigAddress, CancellationToken.None)", "配置本身也是资源"));
            host.AddActionRow("② Bag.BindAssetReferences(config)", () =>
            {
                if (_configOperationGate.IsEntered) { configResult.text = "配置操作仍在进行，请稍候。"; return; }
                if (loadedConfig == null) { configResult.text = "请先完成步骤①。"; return; }
                configBag.BindAssetReferences(loadedConfig);
                referencesBound = true;
                configResult.text = $"{loadedConfig.name} 的内部引用已归当前子 Bag 管理。";
            }, CodeRef.Here("configBag.BindAssetReferences(loadedConfig)", "一行建立 SO 引用所有权"));
            host.AddAsyncActionRow("③ IconRef.Get()", chapterCt => RunConfigOperation(chapterCt, async ct =>
            {
                if (loadedConfig == null) { configResult.text = "请先完成步骤①。"; return; }
                if (!referencesBound)
                {
                    configResult.text = "请先完成步骤②；未绑定就 Get 会让 handle 所有权不清晰。";
                    return;
                }

                var config = loadedConfig;
                var ownerBag = configBag;
                ct.ThrowIfCancellationRequested();
                // IconRef 是加载到的 SO 实例上的共享状态。外部等待不用章节 token 提前离开；真正的 owner token
                // 已由 BindAssetReferences 写入，Teardown 取消 owner 后本操作会到达确定终态，gate 才释放。
                var icon = await config.IconRef.Get(CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                if (ownerBag.IsDisposed || !ReferenceEquals(config, loadedConfig)) return;
                if (icon == null) { configResult.text = "IconRef 未配置或加载失败。"; return; }
                configPreview.style.backgroundImage = new StyleBackground(icon);
                configResult.text = $"从 {config.name}.IconRef 得到：{icon.name}";
            }), CodeRef.Here("var icon = await config.IconRef.Get(CancellationToken.None)", "读取已绑定的 SO 引用"));
            host.AddActionRow("释放配置与内部引用（Dispose 子 Bag）", () =>
            {
                if (_configOperationGate.IsEntered) { configResult.text = "配置操作仍在进行，请稍候。"; return; }
                configBag.Dispose();
                configBag = Bag.CreateChild();
                loadedConfig = null;
                referencesBound = false;
                configPreview.style.backgroundImage = StyleKeyword.None;
                configResult.text = "配置 handle 与已绑定的内部引用已一起释放；可从步骤①重测。";
            }, CodeRef.Here("configBag.Dispose()", "统一释放配置和内部引用"));
            host.AddNote("ScriptableObject 是可共享、可异步加载的数据资产，不会经历 `MonoLayerBase.Awake`，框架也不会递归猜测谁拥有它。由真正的持有者调用 `Bag.BindAssetReferences(config)`，就同时回答了“用哪个资源系统加载”和“何时释放”两个问题。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoAssetConfig.cs", "class DemoAssetConfig", "SO 引用配置示例"));
            host.AddCaution("“SO 可共享”不等于“同一个嵌套 AssetReference 可有多个 owner”。Bind 会写入加载器与宿主 token，任一 owner Dispose 也会释放同一 handle；多个 Context 同时使用时应各自 Instantiate / clone 一份配置，或由一个明确的长寿命 owner 独占并向外提供只读结果。");
#if UNITY_EDITOR
            host.AddActionRow("定位 DemoAssetConfig 资产", () =>
                DemoEditorNav.PingAsset("Assets/Game/Framework/Demo/Res/DemoAssetConfig.asset"));
#endif

            // ── 4. 选择指南 ──
            host.AddSectionTitle("怎么选：地址、Mono 引用还是 SO 引用");
            host.AddTable(
                new[] { "形态", "适合", "绑定者", "释放者" },
                new[] { "字符串 location", "服务器下发 / 动态选择", "不需要", "调用方 Bag" },
                new[] { "Mono 上 AssetReference", "场景 / Prefab 可视化配置", "Mono 基类 Awake", "Mono 自己的 Bag" },
                new[] { "SO 上 AssetReference", "可下发的配置资产", "加载并持有 SO 的 Bag", "同一个持有者 Bag" });
            host.AddConcept("Interface", "AssetReference<T> 是业务可见的稳定类型，不暴露 YooAsset handle 或 GUID 解析细节。");
            host.AddConcept("Implementation", "具体加载由当前 Context 的 IAssetUtility / IAssetProvider 完成，可替换后端。");
            host.AddConcept("Locality", "引用、加载入口和释放 owner 在同一宿主附近，减少“资源能加载但没人知道谁该放”的隐式状态。");
            host.AddTip("判断归属时别问“它看起来像配置，所以是不是 Model”；先问它有没有业务真值。纯 Inspector 接线通常是 Utility / View 侧 Adapter，被加载的 SO 则只是数据资产，真正的所有权属于持有它的宿主。");
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
    }
}
