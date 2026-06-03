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
            host.AddNote("写过 Stamp 再归还、然后重新租借：拿到的实例 Stamp 又是 0——归还时 IPoolable.OnReturn 把状态清了，复用不会带上一手的脏数据。",
                CodeRef.Here("class PooledBox", "PooledBox.OnReturn"));

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

                // 把控制按钮塞进左列：用 host.Into 复用统一的按钮 / 值显示 / 源码跳转样式，不必手搓 VisualElement。
                using (host.Into(controls))
                {
                    var spawnLabel = host.AddValueDisplay("已生成方块：0");

                    void ClearSpawned()
                    {
                        spawnBag.Dispose();       // 归还本轮 Bag.Spawn 出来的所有 GameObject
                        spawnBag = Bag.CreateChild();
                        spawned = 0;
                        spawnLabel.text = "已生成方块：0";
                    }

                    host.AddActionRow("生成方块（Bag.Spawn）", () =>
                    {
                        spawnBag.Spawn(assets.ChipPrefab, assets.SpawnRoot);
                        spawned++;
                        spawnLabel.text = $"已生成方块：{spawned}";
                    }, CodeRef.Here("spawnBag.Spawn", "Bag.Spawn 用法"));
                    host.AddActionRow("清理本轮方块（自动归还）", ClearSpawned,
                        CodeRef.Here("spawnBag.Dispose()", "Bag.Dispose 归还"));
#if UNITY_EDITOR
                    host.AddActionRow("选中对象池演示容器", () => SelectInInspector(assets.SpawnRoot.gameObject));
#endif
                }

                host.AddNote("Bag.Spawn 和 Bag.Rent 心智一致：借来的东西进 Bag，Bag.Dispose 时统一归还。这里是 GameObject/prefab 池——清理本轮方块或切走本章，都会自动 Despawn 归还，而不是留在场景里。右侧方块区域是 UGUI 容器通过 UI Toolkit 占位框对齐出来的“镶嵌效果”：两套 UI 不能互为子节点，但可以用占位元素同步位置。",
                    CodeRef.Here("assets.BindAnchor", "UI Toolkit 占位对齐"));
            }

            host.AddSectionTitle("两条使用路径");
            host.AddConcept("Bag.Rent / Spawn", "自动归还路径：Bag.Rent<T>() 借 C# 对象，Bag.Spawn(prefab, parent) 借 GameObject；宿主 Bag.Dispose 时统一归还，心智同 Bag.Load。");
            host.AddConcept("IPoolUtility", "手动控制路径：需要更早归还、自定义工厂/钩子、Prewarm 预热时，this.GetUtility<IPoolUtility>() 直接操作池。C# 池和 GameObject 池共用同一个工具入口。");
            host.AddTip("约定：归还后别再用那个实例（它可能已被下一个租借者取走）；状态清理放归还侧（IPoolable.OnReturn 或 GetPool 的 onReturn 委托），别指望租借者每次记得清。Editor 下重复归还 / 归还外来实例会报错帮你抓 bug。");
        }

#if UNITY_EDITOR
        // 编辑器便利：选中并高亮对象池演示容器，方便在 Hierarchy 看 Bag.Spawn 出来的 GameObject 实例。
        private static void SelectInInspector(GameObject go)
        {
            UnityEditor.Selection.activeObject = go;
            UnityEditor.EditorGUIUtility.PingObject(go);
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
