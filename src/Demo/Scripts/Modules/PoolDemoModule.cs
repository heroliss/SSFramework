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
    /// 能力·对象池：先用 C# 对象池演示复用省 GC + IPoolable.OnReturn 清状态；
    /// 再用 GameObject/prefab 池演示 Bag.Spawn 借实例、Bag 释放时自动 Despawn 归还。
    /// </summary>
    public sealed class PoolDemoModule : DemoModuleBase
    {
        public override string Id => "object-pool";
        public override string Title => "对象池 · C# / GameObject";
        public override string Category => "能力";
        public override int Order => 10;
        public override string Summary =>
            "复用实例、省掉反复 new / Instantiate 与 GC。C# 池演示借→还→再借命中同一实例、OnReturn 清状态；GameObject 池演示 Bag.Spawn 借 prefab 实例，Bag 释放时自动 Despawn 归还。";

        public override void Build(DemoModuleHost host)
        {
            var pool = this.GetUtility<IPoolUtility>().GetPool<PooledBox>();

            // 本次进入的局部状态：手上持有的实例 + 累计租借次数。切走再回来从头演示。
            PooledBox held = null;
            int rentCount = 0;

            host.AddSectionTitle("演示：复用同一实例、省 GC");
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
            }, CodeRef.Here("pool.Return(held)", "归还用法"));

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
                host.AddNote("没找到 DemoPoolAssets / ChipPrefab / SpawnRoot——请确认 DemoApp 下挂了 DemoPoolAssets 并接好了对象池演示 prefab 与容器。");
            }
            else
            {
                // 状态读数整行铺开、不放进左列：值文本 + 行末「查看源码」比左列宽，放进窄列会横向溢出到右侧占位框上。
                var spawnLabel = host.AddValueDisplay();
                var poolLabel = host.AddValueDisplay();
                var instLabel = host.AddValueDisplay("",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/PooledChip.cs", "class PooledChip", "PooledChip 计数"));

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
                int spawned = 0;

                // 预热/收缩直接操作这个 prefab 的 GameObject 池——它和 Bag.Spawn 取的是同一个池
                // （都走 GetUtility<IPoolUtility>().GetGameObjectPool(prefab)），所以预热出来的实例下次 Spawn 会被复用。
                var goPool = this.GetUtility<IPoolUtility>().GetGameObjectPool(assets.ChipPrefab);

                void RefreshGo()
                {
                    spawnLabel.text = $"本轮已生成：{spawned}";
                    poolLabel.text = $"池中空闲：{goPool.CountInactive}";
                    instLabel.text = $"真正实例化（Instantiate）：{PooledChip.InstantiateCount}";
                }
                RefreshGo();

                // 把控制按钮塞进左列：用 host.Into 复用统一的按钮 / 值显示 / 源码跳转样式，不必手搓 VisualElement。
                using (host.Into(controls))
                {
                    void ClearSpawned()
                    {
                        spawnBag.Dispose();       // 归还本轮 Bag.Spawn 出来的所有 GameObject（回池、不销毁）
                        spawnBag = Bag.CreateChild();
                        spawned = 0;
                        RefreshGo();
                    }

                    host.AddActionRow("生成方块（Bag.Spawn）", () =>
                    {
                        spawnBag.Spawn(assets.ChipPrefab, assets.SpawnRoot);
                        spawned++;
                        RefreshGo();
                    }, CodeRef.Here("spawnBag.Spawn", "Bag.Spawn 用法"));
                    host.AddActionRow("清理本轮方块（自动归还）", ClearSpawned,
                        CodeRef.Here("spawnBag.Dispose()", "Bag.Dispose 归还"));
                    host.AddActionRow("预热 +5（分帧 Prewarm）", async () =>
                    {
                        await goPool.Prewarm(5, perFrame: 2);   // 每帧建 2 个，把实例化开销摊到多帧（加载界面期最常用）
                        RefreshGo();
                    }, CodeRef.Here("goPool.Prewarm(5", "分帧预热用法"));
                    host.AddActionRow("收缩到 2（分帧 TrimAsync）", async () =>
                    {
                        await goPool.TrimAsync(2, perFrame: 2);  // 每帧销毁 2 个，避免一次性 Destroy 一批造成卡顿
                        RefreshGo();
                    }, CodeRef.Here("goPool.TrimAsync(2", "分帧收缩用法"));
#if UNITY_EDITOR
                    host.AddActionRow("选中对象池演示容器", () => SelectInInspector(assets.SpawnRoot.gameObject));
                    host.AddActionRow("选中池停放节点", () =>
                    {
                        var parking = FindParkingRoot();
                        if (parking != null) SelectInInspector(parking);
                    });
#endif
                }

                host.AddNote("方块颜色随 Spawn 逐格渐变 = `OnRent` 在 GameObject 上每次取出都跑（复用的旧实例也重新着色）；"
                    + "预热后「真正实例化」涨、之后反复生成 / 清理却不再涨 = 池在复用旧实例、省掉 `Instantiate`。归还时 `OnReturn` 复位颜色，复用不带上一手的脏状态——和 C# 段 `Stamp` 清零同理。",
                    new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/PooledChip.cs", "void OnRent", "PooledChip.OnRent/OnReturn"));
                host.AddNote("预热（`Prewarm`）把实例化尖峰挪到加载期、收缩（`Trim`）在内存吃紧时回收过度预热的空闲实例；两者都分帧摊开开销（每帧 `perFrame` 个），避免一次性 `Instantiate`/`Destroy` 一大批造成卡顿。C# 池开销小，用同步 `Prewarm`/`Trim` 即可。");
                host.AddNote("`Bag.Spawn` 和 `Bag.Rent` 心智一致：借来的东西进 `Bag`，`Bag.Dispose`（清理本轮 / 切走本章）统一自动 `Despawn` 归还，而不是留在场景里。右侧方块区是 UGUI 容器通过 UI Toolkit 占位框对齐出来的“镶嵌效果”：两套 UI 不能互为子节点，但可以用占位元素同步位置。",
                    CodeRef.Here("assets.BindAnchor", "UI Toolkit 占位对齐"));
                host.AddNote("归还的空闲实例停在一个停用的 DontDestroyOnLoad 节点 `[Game.Framework PooledObjects]` 下（点上面按钮可选中它看）。该节点被外部误删后，下次归还会自愈重建，实例不会散落到场景根。");
            }

            host.AddSectionTitle("使用路径");
            host.AddConcept("Bag.Rent / Spawn", "自动归还：`Bag.Rent<T>()` 借 C# 对象、`Bag.Spawn(prefab, parent)` 借 GameObject；宿主 `Bag.Dispose` 时统一归还，心智同 `Bag.Load`。");
            host.AddConcept("IPoolUtility", "手动控制：`this.GetUtility<IPoolUtility>()` 直接操作池——更早归还、`Prewarm` 预热、`Trim` 收缩、配自定义工厂/钩子。C# 池和 GameObject 池共用同一入口。");

            host.AddSectionTitle("注册 = 生命周期");
            host.AddConcept("RegisterOwned", "纯 C#、随 `Context.Dispose` 自动清池（销毁停放节点 + 空闲实例），可安全 per-Context 注册——demo 根 Context 用的就是它。");
            host.AddConcept("RegisterValue", "纯 C#、不被容器释放，适合全局唯一、随进程长存的池。");
            host.AddConcept("MonoPoolUtility", "Mono：挂 Context 子节点，可在 Inspector 针对各 prefab 配容量 / 预热，随该 GameObject / 场景销毁自动清池。底层复用同一套逻辑。");
            host.AddCodeLink(new CodeRef("Assets/Game/Framework/Demo/Scripts/Core/MonoDemoContext.cs", "RegisterOwned", "demo 注册（RegisterOwned）"));

            host.AddTip("约定：归还后别再用那个实例（可能已被下一个租借者取走）；状态清理放归还侧（IPoolable.OnReturn 或 GetPool 的 onReturn 委托）。容量上限 maxSize 超限即销毁；GameObject 池可 Prewarm(n, perFrame) / TrimAsync 分帧摊开开销。Editor / Dev 下重复归还、归还外来实例、Dispose 后误用都会报错帮你抓 bug。");
        }

#if UNITY_EDITOR
        // 编辑器便利：选中并高亮对象池演示容器，方便在 Hierarchy 看 Bag.Spawn 出来的 GameObject 实例。
        private static void SelectInInspector(GameObject go)
        {
            UnityEditor.Selection.activeObject = go;
            UnityEditor.EditorGUIUtility.PingObject(go);
        }

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
