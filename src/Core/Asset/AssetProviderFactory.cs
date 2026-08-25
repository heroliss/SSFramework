using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Game.Framework
{
    /// <summary>
    /// 默认资源 provider 创建点。<see cref="AssetUtility"/> 只依赖 <see cref="IAssetProvider"/>；具体 Adapter
    /// 在自己的 Assembly 上通过 <see cref="DefaultAssetProviderAttribute"/> 声明默认实现，Core 不保存实现类型名。
    /// 删除 Yoo、换成 Addressables / 自研时，安装另一个带注册属性的 Adapter 即可，不必修改只读 Core 包。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么是 Assembly 注册？</b>Interface 在 Core、Implementation 在 Adapter，Core 不能反向引用 Adapter。
    /// Assembly attribute 让“我提供默认实现”与实现保持 Locality，同时没有运行时初始化顺序竞争。注册数量必须恰好为一：
    /// 未安装时解释如何接入，装了两个后端时列出冲突，不按加载顺序静默选一个。
    /// Adapter 仍必须确保自己的程序集会进入 Player 并在创建资源系统前已加载；反射注册不是 linker 根。
    /// 推荐让 Adapter 自带 <c>link.xml</c>，并对目标平台 AOT Player 做一次初始化回归。
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
    /// <b>判别标准</b>：「全局一次性的架构装配」→ 留在 Adapter 的 Assembly 注册 / 项目 Composition Root；
    /// 「按实例变化、由人在 Inspector 编排的数据 / 策略多态」（如技能上的 <c>[SerializeReference] IEffect</c> 列表）→ 才考虑多态序列化。
    /// provider 属于前者。
    /// </para>
    /// </remarks>
    internal static class AssetProviderFactory
    {
        public static IAssetProvider CreateDefault()
        {
            var registeredTypes = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                         .OrderBy(item => item.GetName().Name, StringComparer.Ordinal))
            {
                try
                {
                    registeredTypes.AddRange(assembly
                        .GetCustomAttributes(typeof(DefaultAssetProviderAttribute), inherit: false)
                        .Cast<DefaultAssetProviderAttribute>()
                        .Select(attribute => attribute.ProviderType));
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"[AssetProviderFactory] 读取程序集 {assembly.GetName().Name} 的默认 Provider 注册失败。",
                        exception);
                }
            }

            Type type = SelectDefaultProviderType(registeredTypes);
            try
            {
                return (IAssetProvider)Activator.CreateInstance(type, nonPublic: true);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"[AssetProviderFactory] 无法创建默认 Provider {type.FullName}。", exception);
            }
        }

        internal static Type SelectDefaultProviderType(IEnumerable<Type> registeredTypes)
        {
            Type[] types = (registeredTypes ?? throw new ArgumentNullException(nameof(registeredTypes))).ToArray();
            if (types.Length == 0)
                throw new InvalidOperationException(
                    "[AssetProviderFactory] 没有注册默认资源 Provider。安装一个资源 Adapter，并在其 AssemblyInfo.cs " +
                    "声明 [assembly: DefaultAssetProvider(typeof(YourProvider))]；若项目完全不用资源系统，请不要挂载 AssetUtility。");

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface ||
                    !typeof(IAssetProvider).IsAssignableFrom(type))
                    throw new InvalidOperationException(
                        $"[AssetProviderFactory] 注册类型 {type?.FullName ?? "<null>"} 不是可构造的 IAssetProvider 实现。");
                if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null, Type.EmptyTypes, modifiers: null) == null)
                    throw new InvalidOperationException(
                        $"[AssetProviderFactory] 注册类型 {type.FullName} 缺少无参构造函数。");
            }

            if (types.Length > 1)
                throw new InvalidOperationException(
                    "[AssetProviderFactory] 检测到多个默认资源 Provider，请只保留一个 Adapter 注册：" +
                    string.Join("、", types.Select(type => type.AssemblyQualifiedName)));
            return types[0];
        }
    }

    /// <summary>
    /// 在资源 Adapter 的 Assembly 上声明默认 <see cref="IAssetProvider"/> Implementation。
    /// 每个应用进程必须恰好存在一个注册；属性不等于全局启用开关，物理删除 Adapter 即移除该实现。
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class DefaultAssetProviderAttribute : Attribute
    {
        /// <summary>创建一条默认 Provider 注册。</summary>
        /// <param name="providerType">
        /// 实现 <see cref="IAssetProvider"/> 且具有 public 或 non-public 无参构造函数的具体类型。
        /// </param>
        public DefaultAssetProviderAttribute(Type providerType) => ProviderType = providerType;

        /// <summary>Adapter 提供的默认 Provider 类型。</summary>
        public Type ProviderType { get; }
    }
}
