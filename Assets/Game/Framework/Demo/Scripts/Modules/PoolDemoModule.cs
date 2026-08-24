using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.Pool;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·对象池：默认用 Bag.Rent / Bag.Spawn 借实例、随 bag 自动归还（不必先建池），
    /// 个别实例可在同一 bag 上 Return / Despawn 提前归还；要读「池中空闲」、配钩子或预热/收缩运维时才手动 GetPool。
    /// C# 段演示复用省 GC + IPoolable.OnReturn 清状态，GameObject 段把复用做成肉眼可见的着色证据。
    /// </summary>
    public sealed class PoolDemoModule : DemoModuleBase
    {
        public override string Id => "object-pool";
        public override string Title => "对象池 · C# / GameObject";
        public override string Category => "能力";
        public override int Order => 10;
        public override string Summary =>
            "对象池复用实例，减少 new、Instantiate 和 GC；默认用 Bag.Rent/Spawn 自动归还。" +
            "只有需要预热、钩子或内部指标时才手动 GetPool。";

        // Prewarm / Trim 都会分帧改同一个 GameObject 池；模块级 gate 让 UI 重建后的新按钮也必须等旧操作响应取消并收尾。
        private readonly DemoOperationGate _poolMaintenanceGate = new();

        public override void Build(DemoModuleHost host)
        {
            var pool = this.GetUtility<IPoolUtility>().GetPool<PooledBox>();

            // 本次进入的局部状态：手上持有的实例 + 累计租借次数。切走再回来从头演示。
            PooledBox held = null;
            int rentCount = 0;

            // ── 定位 ──
            host.AddSectionTitle("定位：对象复用，借还随 Bag 自动管");
            host.AddNote("高频创建 / 销毁的对象（子弹、特效、列表行）走对象池复用，免 GC 抖动。首选 `Bag.Rent<T>()` / `Bag.Spawn(prefab)`——借来的随宿主 `Bag` 自动归还，不必先建池、不必手动 Return。下面从推荐做法到手动控制逐层展开。");

            // ── 默认做法（推荐）：Bag.Rent，不必先建池 ──
            host.AddSectionTitle("默认做法（推荐）：Bag.Rent —— 不必先建池");
            var autoLabel = host.AddValueDisplay();
            var autoBag = Bag.CreateChild();   // 借到这个子 bag：Dispose 时自动归还本组借出的全部实例（演示用，平时直接用基类 Bag）
            var autoHeld = new List<PooledBox>();   // 本组借出未归还的实例，给「单个提前归还」按钮挑目标用
            void RefreshAuto() => autoLabel.text = $"本组借出（未归还）：{autoHeld.Count}";
            RefreshAuto();

            host.AddActionRow("Bag.Rent 借一个", () =>
            {
                autoHeld.Add(autoBag.Rent<PooledBox>());   // 不必先 GetPool、也不手动 Return：借来的进 bag，随 bag 自动归还
                RefreshAuto();
            }, CodeRef.Here("autoBag.Rent<PooledBox>()", "Bag.Rent 用法"));
            host.AddActionRow("Bag.Return 提前还一个（最后借的）", () =>
            {
                if (autoHeld.Count == 0) return;
                int last = autoHeld.Count - 1;
                autoBag.Return(autoHeld[last]);   // 单个提前归还：还回池的同时摘掉 bag 里的登记，Dispose 不会再还一次
                autoHeld.RemoveAt(last);
                RefreshAuto();
            }, CodeRef.Here("autoBag.Return(autoHeld[last])", "Bag.Return 单个提前归还"));
            host.AddActionRow("Dispose 这个 bag（自动归还剩余全部）", () =>
            {
                autoBag.Dispose();           // 本组还没提前归还的实例统一回到默认池
                autoBag = Bag.CreateChild();
                autoHeld.Clear();
                RefreshAuto();
            }, CodeRef.Here("autoBag.Dispose()", "Bag.Dispose 自动归还"));

            host.AddNote("最常用的借法：`Bag.Rent<T>()` 不必先 `GetPool`、也不必手动 `Return`——借来的进 bag，`bag.Dispose`（这里点按钮 / 切走本章）统一自动归还，心智同 `Bag.Load`。`GameObject` 池对应的是 `Bag.Spawn`（下面那段）。");
            host.AddNote("「整批自动 + 单个提前」是一对：个别实例要提前退场，在**借出它的同一个 bag** 上 `Return`——归还的同时摘掉该实例的自动归还登记，所以先提前还几个、再点 Dispose，每个实例都只归还一次。外来实例 / 重复归还 Editor 下会报错帮你抓 bug。");
            host.AddNote("它和下面手动段取的是**同一个默认池**：在这里借了再 Dispose，切到下面点「租借一个」会复用同一实例、构造次数不涨。");

            host.AddSectionTitle("手动控制：GetPool + Rent / Return —— 要读「池中空闲」、逐个看复用时");
            var statLabel = host.AddValueDisplay();
            var heldLabel = host.AddValueDisplay();

            void Refresh()
            {
                statLabel.text = $"构造次数（真正 new）：{PooledBox.ConstructCount}　｜　累计租借：{rentCount}　｜　池中空闲：{pool.CountInactive}";
                heldLabel.text = held == null ? "手上实例：（已归还）" : $"手上实例：Stamp = {held.Stamp}";
            }
            Refresh();

            host.AddActionRow("租借一个", () =>
            {
                if (held != null) return;     // 已持有就不重复借（演示简化为单实例）
                held = pool.Rent();           // 池里有就复用、没有才 new
                rentCount++;
                Refresh();
            }, CodeRef.Here("held = pool.Rent()", "租借用法"));
            host.AddActionRow("往实例写数据（Stamp +1）", () =>
            {
                if (held == null) return;
                held.Stamp++;
                Refresh();
            }, CodeRef.Here("held.Stamp++", "写数据用法"));
            host.AddActionRow("归还", () =>
            {
                if (held == null) return;
                pool.Return(held);            // 触发 OnReturn 清状态后入池
                held = null;
                Refresh();
            }, CodeRef.Here("pool.Return(held);            // 触发 OnReturn", "归还用法"));

            host.AddNote("这段先 `GetPool<PooledBox>()` 拿到池、再手动 `Rent` / `Return`，是为了显示「池中空闲 `CountInactive`」并逐个观察复用——**这正是少数需要先建池的场景之一**（其余：配自定义工厂 / `OnRent`·`OnReturn` 钩子、`Prewarm` / `Trim` 运维）。日常借用上面的 `Bag.Rent` 就够，不必先建池。");
            host.AddNote("反复「租借→归还→再租借」：构造次数几乎不涨——池命中复用，省掉了 new 和 GC。");
            host.AddNote("写过 `Stamp` 再归还、然后重新租借：拿到的实例 `Stamp` 又是 0——归还时 `IPoolable.OnReturn` 把状态清了，复用不会带上一手的脏数据。",
                CodeRef.Here("class PooledBox", "PooledBox.OnReturn"));
            host.AddNote("池也支持 `Prewarm`（预热）/ `Trim`（收缩）运维，但预热的真实价值在「避免实例化尖峰」、对 GameObject 才明显——放到下面 GameObject 池里演示其分帧（异步）版本。");

            // 切走本章（Teardown→Bag.Dispose）时，把还攥在手上没手动归还的实例还回池里，
            // 免得来回切章时"构造次数"虚高、混淆"复用省 GC"的演示。
            Bag.Add(Disposable.Create(() =>
            {
                if (held != null) { pool.Return(held); held = null; }
            }));

            // ── GameObject / prefab 池：可视化 Spawn / Despawn ──
            host.AddSectionTitle("GameObject 池：Bag.Spawn / 自动 Despawn");
            var assets = Object.FindFirstObjectByType<DemoPoolAssets>();
            if (assets == null || assets.ChipPrefab == null || assets.SpawnRoot == null)
            {
                host.AddNote("没找到 DemoPoolAssets / ChipPrefab / SpawnRoot——请确认 demo 根节点下挂了 DemoPoolAssets 并接好了对象池演示 prefab 与容器。");
            }
            else
            {
                // 状态读数整行铺开、不放进左列：值文本 + 行末「查看源码」比左列宽，放进窄列会横向溢出到右侧占位框上。
                var spawnLabel = host.AddValueDisplay();
                var poolLabel = host.AddValueDisplay();
                var instLabel = host.AddValueDisplay("",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/PooledChip.cs", "class PooledChip", "PooledChip 计数"));

                // 分栏骨架：左列放控制按钮，右列是 UI Toolkit 占位框；UGUI 容器按占位框 worldBound 对齐，做出"镶嵌"效果。
                var demoRow = new VisualElement();
                demoRow.AddToClassList("demo-pool-demo-row");
                var controls = new VisualElement();
                controls.AddToClassList("demo-pool-controls");
                var anchor = new VisualElement();
                anchor.AddToClassList("demo-pool-anchor");
                demoRow.Add(controls);
                demoRow.Add(anchor);
                host.Content.Add(demoRow);

                assets.BindAnchor(anchor);
                // 切走本章时松开占位引用，不再让 UGUI 容器追一个已从面板移除的元素。
                Bag.Add(Disposable.Create(assets.ClearAnchor));

                var spawnBag = Bag.CreateChild();
                // spawnBag 借出未归还的实例，给「Bag.Despawn 单个提前归还」按钮挑目标用。
                var spawnedChips = new List<GameObject>();
                // 手动 Spawn 出来的实例：不进 bag，由本模块自己掌管单个 Despawn 时机（演示「单个归还」，对应 C# 段手动 Rent/Return）。
                var manual = new List<GameObject>();

                // 预热/收缩直接操作这个 prefab 的 GameObject 池——它和 Bag.Spawn 取的是同一个池
                // （都走 GetUtility<IPoolUtility>().GetGameObjectPool(prefab)），所以预热出来的实例下次 Spawn 会被复用。
                var goPool = this.GetUtility<IPoolUtility>().GetGameObjectPool(assets.ChipPrefab);

                // 手动 Spawn 的实例不在任何 bag 里——切走本章时由本模块负责逐个归还，免得它们留在场景里、混淆复用计数。
                Bag.Add(Disposable.Create(() =>
                {
                    foreach (var go in manual) if (go != null) goPool.Despawn(go);
                    manual.Clear();
                }));

                void RefreshGo()
                {
                    spawnLabel.text = $"Bag.Spawn 在外：{spawnedChips.Count}　｜　手动持有：{manual.Count}";
                    poolLabel.text = $"池中空闲：{goPool.CountInactive}";
                    instLabel.text = $"真正实例化（Instantiate）：{PooledChip.InstantiateCount}";
                }
                RefreshGo();

                // 把控制按钮塞进左列：用 host.Into 复用统一的按钮 / 值显示 / 源码跳转样式，不必手搓 VisualElement。
                using (host.Into(controls))
                {
                    void ClearSpawned()
                    {
                        spawnBag.Dispose();       // 归还本轮 Bag.Spawn 还在外面的所有 GameObject（回池、不销毁）
                        spawnBag = Bag.CreateChild();
                        spawnedChips.Clear();
                        RefreshGo();
                    }

                    host.AddActionRow("生成方块（Bag.Spawn）", () =>
                    {
                        spawnedChips.Add(spawnBag.Spawn(assets.ChipPrefab, assets.SpawnRoot));
                        RefreshGo();
                    }, CodeRef.Here("spawnBag.Spawn", "Bag.Spawn 用法"));
                    host.AddActionRow("Bag.Despawn 提前还一个（最后生成的）", () =>
                    {
                        if (spawnedChips.Count == 0) return;
                        int last = spawnedChips.Count - 1;
                        spawnBag.Despawn(spawnedChips[last]);   // 单个提前归还：回池的同时摘掉 bag 里的登记，清理本轮时不会重复 Despawn
                        spawnedChips.RemoveAt(last);
                        RefreshGo();
                    }, CodeRef.Here("spawnBag.Despawn(spawnedChips[last])", "Bag.Despawn 单个提前归还"));
                    host.AddActionRow("清理本轮方块（自动归还）", ClearSpawned,
                        CodeRef.Here("spawnBag.Dispose()", "Bag.Dispose 归还"));
                    host.AddActionRow("手动 Spawn 一个", () =>
                    {
                        manual.Add(goPool.Spawn(assets.SpawnRoot));   // 不交给 bag：归还时机自己掌管
                        RefreshGo();
                    }, CodeRef.Here("goPool.Spawn(assets.SpawnRoot)", "手动 Spawn 用法"));
                    host.AddActionRow("Despawn 单个（归还最后一个）", () =>
                    {
                        if (manual.Count == 0) return;
                        int last = manual.Count - 1;
                        var go = manual[last];
                        manual.RemoveAt(last);
                        goPool.Despawn(go);   // 单个归还：自己掌管时机，不像 Bag.Spawn 等到 bag.Dispose 才批量归还
                        RefreshGo();
                    }, CodeRef.Here("goPool.Despawn(go);   // 单个归还", "单个 Despawn 用法"));
                    host.AddAsyncActionRow("预热 +5（分帧 Prewarm）", async ct =>
                    {
                        if (!_poolMaintenanceGate.TryEnter(out var lease)) { poolLabel.text = "另一项池维护操作仍在运行 / 收尾，请稍候。"; return; }
                        using (lease)
                        {
                            await goPool.Prewarm(5, perFrame: 2, ct: ct);   // 每帧建 2 个，把实例化开销摊到多帧（加载界面期最常用）
                            ct.ThrowIfCancellationRequested();
                            RefreshGo();
                        }
                    }, CodeRef.Here("goPool.Prewarm(5", "分帧预热用法"));
                    host.AddAsyncActionRow("收缩到 2（分帧 TrimAsync）", async ct =>
                    {
                        if (!_poolMaintenanceGate.TryEnter(out var lease)) { poolLabel.text = "另一项池维护操作仍在运行 / 收尾，请稍候。"; return; }
                        using (lease)
                        {
                            await goPool.TrimAsync(2, perFrame: 2, ct: ct);  // 每帧销毁 2 个，避免一次性 Destroy 一批造成卡顿
                            ct.ThrowIfCancellationRequested();
                            RefreshGo();
                        }
                    }, CodeRef.Here("goPool.TrimAsync(2", "分帧收缩用法"));
#if UNITY_EDITOR
                    host.AddActionRow("选中对象池演示容器", () => DemoEditorNav.PingSceneObject(assets.SpawnRoot.gameObject),
                        new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoPoolAssets.cs", "class DemoPoolAssets", "对象池演示资产引用"));
                    host.AddActionRow("选中池停放节点", () =>
                    {
                        var parking = FindParkingRoot();
                        if (parking != null) DemoEditorNav.PingSceneObject(parking);
                    });
#endif
                }

                host.AddNote("方块颜色随 Spawn 逐格渐变 = `OnRent` 在 GameObject 上每次取出都跑（复用的旧实例也重新着色）；"
                    + "预热后「真正实例化」涨、之后反复生成 / 清理却不再涨 = 池在复用旧实例、省掉 `Instantiate`。归还时 `OnReturn` 复位颜色，复用不带上一手的脏状态——和 C# 段 `Stamp` 清零同理。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/PooledChip.cs", "void OnRent", "PooledChip.OnRent/OnReturn"));
                host.AddNote("预热（`Prewarm`）把实例化尖峰挪到加载期、收缩（`Trim`）在内存吃紧时回收过度预热的空闲实例；两者都分帧摊开开销（每帧 `perFrame` 个），避免一次性 `Instantiate`/`Destroy` 一大批造成卡顿。C# 池开销小，用同步 `Prewarm`/`Trim` 即可。");
                host.AddNote("`Bag.Spawn` 和 `Bag.Rent` 心智一致：借来的东西进 `Bag`，`Bag.Dispose`（清理本轮 / 切走本章）统一自动 `Despawn` 归还，而不是留在场景里。右侧方块区是 UGUI 容器按 UI Toolkit 占位框对齐出来的“镶嵌效果”（**浮层对齐**）：两套 UI 不能互为子节点，但可用占位元素同步屏幕位置。⚠ 占位框的 `worldBound` 是**面板点**坐标、UGUI 是**屏幕像素**，两者比例随 DPI / 缩放模式而变（本机 1.5×）——`AlignToAnchor` 先按面板根尺寸把面板点换算成屏幕像素再赋值，才对齐得准、滚动也跟内容同速。容器上挂了 `RectMask2D`，方块排满溢出框会被裁掉。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoPoolAssets.cs", "void AlignToAnchor", "占位对齐 + 面板点→屏幕像素换算"));
                host.AddNote("但**浮层终究浮在面板之上**：整块框滚出 `ScrollView` 视口时，方块不会被外层 ScrollView 裁剪（浮层的固有边界）。要方块真正随 Toolkit 内容流被裁剪 / 遮挡 / 滚动，改用「UI 融合」章的 RenderTexture 桥（更重，要专用 layer + 相机）。");
                host.AddNote("「手动 Spawn / Despawn 单个」对应 C# 段的手动 `Rent` / `Return`：实例不进 bag、归还时机完全自己掌管（弹幕级高频热路径配领域 List 走这条），和 `Bag.Spawn` 取的是同一个池——`Despawn` 单个归还后下次 Spawn 会复用它。注意 `Bag.Spawn` 出来的实例别绕过 bag 直接 `goPool.Despawn`（会和 bag 的自动归还叠成重复归还）——要提前还，用上面同一 bag 的 `Bag.Despawn(go)`。");
                host.AddNote("归还的空闲实例停在一个停用的 DontDestroyOnLoad 节点 `[Game.Framework PooledObjects]` 下（点上面按钮可选中它看）。该节点被外部误删后，下次归还会自愈重建，实例不会散落到场景根。");
            }

            host.AddSectionTitle("使用路径");
            host.AddConcept("Bag.Rent / Spawn", "自动归还：`Bag.Rent<T>()` 借 C# 对象、`Bag.Spawn(prefab, parent)` 借 GameObject；宿主 `Bag.Dispose` 时统一归还，心智同 `Bag.Load`。单个提前归还在同一 bag 上 `Bag.Return(obj)` / `Bag.Despawn(go)`（自动摘登记、不重复归还）。");
            host.AddConcept("IPoolUtility", "手动控制：`this.GetUtility<IPoolUtility>().Rent<T>()` / `.Return(obj)` 一行直接借还（默认池，不必先 `GetPool`）。只有要读 `CountInactive`、反复操作同一个池、或配自定义工厂 / 钩子时，才先 `GetPool<T>(...)` 拿到池——本 demo 为显示“池中空闲”才走 `GetPool`。`Prewarm` / `Trim` 运维也在池上；C# 池与 GameObject 池共用同一入口。");

            host.AddSectionTitle("注册 = 生命周期");
            host.AddConcept("RegisterOwned", "纯 C#、随 `Context.Dispose` 自动清池（销毁停放节点 + 空闲实例），可安全 per-Context 注册——demo 根 Context 用的就是它。");
            host.AddConcept("RegisterValue", "纯 C#、不被容器释放，适合全局唯一、随进程长存的池。");
            host.AddConcept("MonoPoolUtility", "Mono：挂 Context 子节点，可在 Inspector 针对各 prefab 配容量 / 预热，随该 GameObject / 场景销毁自动清池。底层复用同一套逻辑。");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "builder.RegisterOwned(new PoolUtility", "demo 注册（RegisterOwned）"));

            host.AddTip("约定：归还后别再用那个实例（可能已被下一个租借者取走）；状态清理放归还侧（IPoolable.OnReturn 或 GetPool 的 onReturn 委托）。容量上限 maxSize 超限即销毁；GameObject 池可 Prewarm(n, perFrame) / TrimAsync 分帧摊开开销。Editor / Dev 下重复归还、归还外来实例、Dispose 后误用都会报错帮你抓 bug。");
        }

#if UNITY_EDITOR
        // 找到池内部那个停用的 DontDestroyOnLoad 停放总根。它是 SetActive(false)，GameObject.Find 找不到，
        // 故用 Resources.FindObjectsOfTypeAll（含 inactive），再用 scene.IsValid 排除 prefab/asset。
        private static GameObject FindParkingRoot()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go.name == "[Game.Framework PooledObjects]" && go.transform.parent == null && go.scene.IsValid())
                    return go;
            return null;
        }
#endif
    }

    /// <summary>演示用池化对象：静态构造计数证明复用；Stamp 演示归还时清状态。</summary>
    public sealed class PooledBox : IPoolable
    {
        public static int ConstructCount;     // 真正 new 出来的次数（构造里 ++）
        public int Stamp;                     // 借用期间写入的数据
        public PooledBox() => ConstructCount++;
        public void OnRent() { }              // 取出时（本例无需额外激活）
        public void OnReturn() => Stamp = 0;  // 归还时清状态，避免脏数据流给下一个租借者
    }
}
