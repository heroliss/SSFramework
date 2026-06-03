using Game.Framework.Common;
using Game.Framework.Demo.Core;
using Game.Framework.Pool;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·对象池（C# 对象）：从 IPoolUtility 借/还同一个实例，演示复用省 GC，IPoolable.OnReturn 归还时清状态。
    /// GameObject/Prefab 池（Bag.Spawn）需要可见实例，并入 View 章一起演示。
    /// </summary>
    public sealed class PoolDemoModule : DemoModuleBase
    {
        public override string Id => "object-pool";
        public override string Title => "对象池 · C# 对象";
        public override string Category => "能力";
        public override int Order => 10;
        public override string Summary =>
            "复用实例、省掉反复 new 与 GC。借→还→再借命中同一实例；归还时 IPoolable.OnReturn 清状态，下一个租借者拿到的是干净对象。View 里更推荐 Bag.Rent——借来随宿主自动归还。";

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
            }, CodeRef.Here("class PooledBox", "PooledBox"));
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

            host.AddSectionTitle("两条使用路径");
            host.AddConcept("Bag.Rent（View 首选）", "Bag.Rent<T>() 借来的实例随宿主 OnDestroy 自动归还，不用手动 Return，心智同 Bag.Load。");
            host.AddConcept("IPoolUtility（要手动控）", "需要更早归还、自定义工厂/钩子、Prewarm 预热时，this.GetUtility<IPoolUtility>() 直接操作池——本页用的就是这条，才好现场演示借还。");
            host.AddTip("约定：归还后别再用那个实例（它可能已被下一个租借者取走）；状态清理放归还侧（IPoolable.OnReturn 或 GetPool 的 onReturn 委托），别指望租借者每次记得清。Editor 下重复归还 / 归还外来实例会报错帮你抓 bug。GameObject/Prefab 池（Bag.Spawn）见 View 章。");
        }
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
