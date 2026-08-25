using System;

namespace Game.Framework
{
    /// <summary>
    /// 默认资源 provider 创建点——全框架唯一决定「用哪个具体 provider」的地方。
    ///
    /// <see cref="AssetUtility"/> 只依赖 <see cref="IAssetProvider"/>；换底层资源库（YooAsset → Addressables / 自研）
    /// 只改本文件的 <see cref="DefaultProviderTypeName"/>（或换掉对应模块程序集），Settings / InitSystem / 业务加载 API 全都不动。
    ///
    /// <para>
    /// <b>为什么是反射而不是直接 <c>new</c>？</b>具体 provider 住在独立模块程序集（如 <c>Game.Framework.Asset.Yoo</c>，
    /// 把 YooAsset 依赖隔离在模块里，见 ADR-0008/0013），而内核「永不引用模块」（引用方向纪律：接口在内核、实现在模块）。
    /// 编译期引用被纪律禁止，唯一的默认装配通道就是按程序集限定名反射。代价是这条引用对 IL2CPP linker 不可见——
    /// 模块作 AOT 时靠模块目录下的 <c>link.xml</c> 防裁剪；模块作热更时解释器元数据齐全，无此问题。
    /// </para>
    ///
    /// <para>
    /// <b>为什么是代码工厂，而不是给 AssetUtility 加 <c>[SerializeField] IAssetProvider</c> 在 Inspector 里拖？</b>
    /// <list type="number">
    ///   <item><b>换库本来就要写代码</b>：换 provider = 写一个新的 <see cref="IAssetProvider"/> 实现模块，这一步绕不开。
    ///         Inspector 赋值顶多省掉改类型名这一处，却换来下面一堆代价，不划算。</item>
    ///   <item><b>provider 是有状态的运行时服务，不是数据</b>：它内部持有包字典等运行时状态，序列化它等于把一个空壳写进
    ///         scene/prefab，而你真正想表达的只有「用哪个类型」这一个 bit。语义错位。</item>
    ///   <item><b>这是全局架构决策，不是 per-instance 配置</b>：「整个项目用哪套资源库」编译期定死、全局一份。
    ///         序列化会把它散进每个挂 AssetUtility 的场景/prefab，N 处要同步、还可能指到不同/缺失的实现。</item>
    ///   <item><b>类型名脆弱 + 漏抽象</b>：多态序列化通常在 YAML 里存程序集限定类型名，改名/移动/换 asmdef 就断引用；
    ///         且要在 Inspector 选择器里挑具体实现类等于把实现反向暴露给编辑器。</item>
    /// </list>
    /// <b>判别标准</b>：「全局一次性的架构装配」→ 留代码（本工厂 / DI 容器注册）；
    /// 「按实例变化、由人在 Inspector 编排的数据 / 策略多态」（如技能上的 <c>[SerializeReference] IEffect</c> 列表）→ 才考虑多态序列化。
    /// provider 属于前者。
    /// </para>
    /// </summary>
    internal static class AssetProviderFactory
    {
        /// <summary>
        /// 默认 provider 的程序集限定名。换资源库后端改这里（新模块的类型全名 + 程序集名）。
        /// 对应类型须实现 <see cref="IAssetProvider"/> 且有无参构造（internal 也可，反射不受限）。
        /// </summary>
        private const string DefaultProviderTypeName = "Game.Framework.YooAssetProvider, Game.Framework.Asset.Yoo";

        public static IAssetProvider CreateDefault()
        {
            var type = Type.GetType(DefaultProviderTypeName, throwOnError: false);
            if (type == null)
                throw new InvalidOperationException(
                    $"[AssetProviderFactory] 找不到默认 provider 类型 '{DefaultProviderTypeName}'。" +
                    "检查：① 模块程序集是否在工程/构建里（热更档位下需先由引导加载）；" +
                    "② AOT 构建是否带上了模块目录下的 link.xml（防 IL2CPP 裁剪）；③ 类型名/程序集名是否改动过。");

            return (IAssetProvider)Activator.CreateInstance(type, nonPublic: true);
        }
    }
}
