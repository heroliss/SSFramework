namespace Game.Framework
{
    /// <summary>
    /// 默认资源 provider 创建点——唯一 <c>new</c> 出具体 provider 的地方。
    ///
    /// <see cref="AssetUtility"/> 只依赖 <see cref="IAssetProvider"/>；未来换底层资源库（YooAsset → Addressables / 自研）
    /// 只改这一行，Settings / InitSystem / 业务加载 API 全都不动。
    ///
    /// <para>
    /// <b>为什么是代码工厂，而不是给 AssetUtility 加 <c>[SerializeField] IAssetProvider</c> 在 Inspector 里拖？</b>
    /// （Odin 确实能序列化接口，所以技术上拖得了——但这里不该拖。）
    /// <list type="number">
    ///   <item><b>换库本来就要写代码</b>：换 provider = 写一个新的 <see cref="IAssetProvider"/> 实现类，这一步绕不开。
    ///         Inspector 赋值顶多省掉「把 new A() 改成 new B()」这一行，却换来下面一堆代价，不划算。</item>
    ///   <item><b>provider 是有状态的运行时服务，不是数据</b>：它内部持有包字典等运行时状态，序列化它等于把一个空壳写进
    ///         scene/prefab，而你真正想表达的只有「用哪个类型」这一个 bit。语义错位。</item>
    ///   <item><b>这是全局架构决策，不是 per-instance 配置</b>：「整个项目用哪套资源库」编译期定死、全局一份。
    ///         序列化会把它散进每个挂 AssetUtility 的场景/prefab，N 处要同步、还可能指到不同/缺失的实现。</item>
    ///   <item><b>类型名脆弱 + 漏抽象</b>：Odin 多态序列化在 YAML 里存程序集限定类型名，改名/移动/换 asmdef 就断引用；
    ///         且要在 Inspector 选择器里挑 <see cref="YooAssetProvider"/>（internal 实现类）等于把实现反向暴露给编辑器。</item>
    /// </list>
    /// <b>判别标准</b>：「全局一次性的架构装配」→ 留代码（本工厂 / DI 容器注册）；
    /// 「按实例变化、由人在 Inspector 编排的数据 / 策略多态」（如技能上的 <c>IEffect</c> 列表）→ 才用 Odin 接口序列化。
    /// provider 属于前者。
    /// </para>
    /// </summary>
    internal static class AssetProviderFactory
    {
        public static IAssetProvider CreateDefault() => new YooAssetProvider();
    }
}
