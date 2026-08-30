using System;
using System.Collections.Generic;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.Systems;
using Game.Framework.Utility;

namespace Game.Framework.Context
{
    /// <summary>
    /// 精简容器构建器。支持层感知注册、值注册、工厂注册、父级容器、Eager 解析。
    /// 解析语义为"后注册覆盖先注册"，因此同一契约多次注册时仅保留最后一次写入。
    /// </summary>
    public sealed class ContainerBuilder : IDisposable
    {
        private readonly Dictionary<Type, ContainerBinding> _bindings = new();
        private readonly List<ContainerBinding> _eagerBindings = new();
        // Build 前 Builder 暂时拥有资源；Build 成功后同一 registry 交给 Container，不复制所有权。
        private readonly OwnedDisposables _owned = new();
        private Container _parent;
        private bool _built;
        private bool _disposed;

        /// <summary>设置父级容器，解析时未命中将回退到父级。</summary>
        public ContainerBuilder SetParent(Container parent)
        {
            ThrowIfBuilt();
            _parent = parent;
            return this;
        }

        /// <summary>
        /// 注册一个纯 C# Model，并自动登记“运行时具体类型 + 所有派生自 <see cref="IModel"/> 的 Interface”。
        /// 这是 <see cref="RegisterValue"/> 的层感知常用入口；实例所有权仍由调用方持有。
        /// </summary>
        public ContainerBuilder RegisterModel<TModel>(TModel value) where TModel : class, IModel
            => RegisterValue(value, GetLayerContracts(value, typeof(IModel)));

        /// <summary>
        /// 注册一个由 Context 拥有的纯 C# Model。契约推导与 <see cref="RegisterModel{TModel}"/> 相同，
        /// Context 结束时还会逆序释放该实例。
        /// </summary>
        public ContainerBuilder RegisterOwnedModel<TModel>(TModel value)
            where TModel : class, IModel, IDisposable
            => RegisterOwned(value, GetLayerContracts(value, typeof(IModel)));

        /// <summary>
        /// 注册一个纯 C# System，并自动登记“运行时具体类型 + 所有派生自 <see cref="ISystem"/> 的 Interface”。
        /// 这是 <see cref="RegisterValue"/> 的层感知常用入口；实例所有权仍由调用方持有。
        /// </summary>
        public ContainerBuilder RegisterSystem<TSystem>(TSystem value) where TSystem : class, ISystem
            => RegisterValue(value, GetLayerContracts(value, typeof(ISystem)));

        /// <summary>
        /// 注册一个由 Context 拥有的纯 C# System。契约推导与 <see cref="RegisterSystem{TSystem}"/> 相同，
        /// Context 结束时还会逆序释放该实例。
        /// </summary>
        public ContainerBuilder RegisterOwnedSystem<TSystem>(TSystem value)
            where TSystem : class, ISystem, IDisposable
            => RegisterOwned(value, GetLayerContracts(value, typeof(ISystem)));

        /// <summary>
        /// 注册一个纯 C# Utility，并自动登记“运行时具体类型 + 所有派生自 <see cref="IUtility"/> 的 Interface”。
        /// 这是 <see cref="RegisterValue"/> 的层感知常用入口；实例所有权仍由调用方持有。
        /// </summary>
        public ContainerBuilder RegisterUtility<TUtility>(TUtility value) where TUtility : class, IUtility
            => RegisterValue(value, GetLayerContracts(value, typeof(IUtility)));

        /// <summary>
        /// 注册一个由 Context 拥有的纯 C# Utility。契约推导与 <see cref="RegisterUtility{TUtility}"/> 相同，
        /// Context 结束时还会逆序释放该实例。
        /// </summary>
        public ContainerBuilder RegisterOwnedUtility<TUtility>(TUtility value)
            where TUtility : class, IUtility, IDisposable
            => RegisterOwned(value, GetLayerContracts(value, typeof(IUtility)));

        /// <summary>
        /// 注册一个值实例到指定的契约类型。
        /// 同一契约多次注册时，后注册覆盖先注册。
        /// </summary>
        public ContainerBuilder RegisterValue(object value, params Type[] contracts)
        {
            ThrowIfBuilt();
            if (value == null) throw new ArgumentNullException(nameof(value));
            ValidateContracts(contracts);
            ValidateValueInstance(value, contracts);

            var binding = ContainerBinding.ForValue(value);
            for (int i = 0; i < contracts.Length; i++)
                _bindings[contracts[i]] = binding;
            return this;
        }

        /// <summary>
        /// 注册一个由 Context <b>拥有</b>的 <see cref="IDisposable"/> 实例：除按契约注册（同 <see cref="RegisterValue"/>）外，
        /// 还登记为"Context 拥有"，在 <c>GameContext.Dispose()</c> 时自动 Dispose（逆序释放）。
        /// 用于生命周期应跟随 Context 的工具（如 <c>PoolUtility</c>）。普通 <see cref="RegisterValue"/> 不拥有实例
        /// （容器不替外部传入的共享实例兜底释放）。
        /// </summary>
        /// <remarks>
        /// 同一实例可一次传多个 contract，也可分次补充 contract；所有权按引用去重，Context 最多 Dispose 一次。
        /// owned 实例仍应遵守 .NET 的幂等 Dispose 约定，便于调用方安全组合。
        /// </remarks>
        public ContainerBuilder RegisterOwned(IDisposable value, params Type[] contracts)
        {
            RegisterValue(value, contracts); // 复用 null/契约校验与契约写入
            _owned.Add(value);
            return this;
        }

        /// <summary>
        /// 注册一个工厂委托。Lazy 模式下首次 Resolve 时调用并缓存结果；Eager 模式下 Build() 完成时立即调用。
        /// 工厂可以通过参数 Container 解析其他依赖，无需手工排序注册顺序。
        /// </summary>
        /// <remarks>
        /// <b>异常合约：</b>
        /// <list type="bullet">
        ///   <item>工厂不应抛异常，应返回非 null 实例或不注册该契约。</item>
        ///   <item>Lazy 工厂抛出时 → 异常透出到调用 <c>Resolve</c> 的位置；factory 不被替换为 instance，
        ///         下次 Resolve 会再次调用（业务可借此实现"延迟重试"，但通常意味着 bug）。</item>
        ///   <item>Eager 工厂抛出时 → 异常透出到 <see cref="Build"/>，容器构建失败，启动期暴露问题。</item>
        ///   <item>多契约共享场景下工厂抛出时，shared 仍为 null，下次任意契约 Resolve 会重新调用工厂；
        ///         若工厂有副作用需自身保证幂等。</item>
        ///   <item>Factory 回调期间若宿主 Context / Container 被同步释放，回调返回值不会缓存或发布，
        ///         本次 Resolve 抛 <see cref="ObjectDisposedException"/>。</item>
        /// </list>
        /// </remarks>
        public ContainerBuilder RegisterFactory(
            Func<Container, object> factory,
            Resolution resolution,
            params Type[] contracts)
            => RegisterFactoryCore(factory, resolution, ownsResult: false, contracts);

        /// <summary>
        /// 注册由 Context 拥有的懒/急切工厂。与 <see cref="RegisterFactory(Func{Container, object}, Resolution, Type[])"/>
        /// 相同地按需构造并缓存 Singleton，但要求产物实现 <see cref="IDisposable"/>，并在 Context Dispose 时逆序释放。
        /// </summary>
        /// <remarks>
        /// 用于“需要先从 Container 解析依赖、同时生命周期应跟随 Context”的服务。普通 <see cref="RegisterFactory(Func{Container, object}, Type[])"/>
        /// 不拥有产物；不能用它创建无人持有的 <see cref="IDisposable"/>。工厂产物仍不自动 Inject/Attach，依赖由工厂显式接线。
        /// 工厂一旦成功返回 <see cref="IDisposable"/>，该对象在契约校验与所有权登记完成前属于“待提交产物”；
        /// 若返回类型不符合 contract 等后续步骤失败，容器会立即回滚释放它，同时保留最初的契约异常。
        /// 工厂在返回前自行创建但未交出的资源仍由工厂负责清理；已经被同一 Container 接管的共享实例也不会因别名注册失败而提前释放。
        /// 若 Factory 回调期间 Context 结束，返回的待提交产物同样立即回滚；已经在该 Context 的释放事务中处理过的 alias
        /// 由弱所有权历史识别，不会重复 Dispose。
        /// </remarks>
        public ContainerBuilder RegisterOwnedFactory(
            Func<Container, object> factory,
            Resolution resolution,
            params Type[] contracts)
            => RegisterFactoryCore(factory, resolution, ownsResult: true, contracts);

        /// <summary><see cref="RegisterOwnedFactory(Func{Container, object}, Resolution, Type[])"/> 的 Lazy 简化重载。</summary>
        public ContainerBuilder RegisterOwnedFactory(Func<Container, object> factory, params Type[] contracts)
            => RegisterOwnedFactory(factory, Resolution.Lazy, contracts);

        private ContainerBuilder RegisterFactoryCore(
            Func<Container, object> factory,
            Resolution resolution,
            bool ownsResult,
            Type[] contracts)
        {
            ThrowIfBuilt();
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            ValidateContracts(contracts);
            if (resolution != Resolution.Lazy && resolution != Resolution.Eager)
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "未知的工厂解析模式。");

            // params 数组来自调用方，注册后可能被复用/修改；拷贝后再捕获，保证工厂契约稳定。
            var registeredContracts = (Type[])contracts.Clone();
            var binding = ContainerBinding.ForFactory(factory, registeredContracts, ownsResult);

            for (int i = 0; i < registeredContracts.Length; i++)
            {
                _bindings[registeredContracts[i]] = binding;
            }
            if (resolution == Resolution.Eager)
                _eagerBindings.Add(binding);
            return this;
        }

        /// <summary>RegisterFactory 的 Lazy 简化重载。</summary>
        public ContainerBuilder RegisterFactory(Func<Container, object> factory, params Type[] contracts)
            => RegisterFactory(factory, Resolution.Lazy, contracts);

        /// <summary>
        /// 构建容器。绑定表复制为新字典，Builder 不再可用（再次调用 RegisterXxx 会抛异常）。
        /// Build 前由 Builder 暂管的 owned 资源在此转交给 Container；若构建失败则立即回滚。
        /// 若有 Eager 工厂，会在此一并构造，启动期就暴露配置错误。
        /// </summary>
        public Container Build()
        {
            ThrowIfBuilt();
            _built = true;

            var copy = new Dictionary<Type, ContainerBinding>(_bindings);

            // 收集构建完成时仍生效的值绑定实例（同一实例多契约只收一次）：GameContext 构造时对它们统一
            // GameContext 会先对整批 Inject、全部成功后再 AttachTo，使纯 C# 路径与 Mono 路径
            // 「注册即注入」语义对称（ADR-0019）。
            // 在 Eager 工厂解析前收集——工厂产物（含 Eager）刻意不进此列表，工厂经 Func<Container, object> 显式接线。
            // 被后续注册覆盖掉的值实例不在 copy.Values 里，自然不会被注入。
            var boundValues = new List<object>();
            foreach (var binding in copy.Values)
                if (!binding.IsFactory && !ContainsReference(boundValues, binding.Instance))
                    boundValues.Add(binding.Instance);

            Container container = null;
            try
            {
                container = new Container(copy, _parent, _owned, boundValues);

                // 只启动构建完成后仍被至少一个 contract 引用的 Eager 工厂。Binding 自己缓存结果，
                // 因而多 contract 或某个 contract 被覆盖时也不会重复构造。
                for (int i = 0; i < _eagerBindings.Count; i++)
                    if (ContainsBinding(copy, _eagerBindings[i]))
                        _eagerBindings[i].Resolve(container);
            }
            catch
            {
                // Build 失败时不会产生 GameContext 接手所有权：Container 已创建则由它统一回滚；
                // 极早期失败则 Builder 仍是临时 owner。两条路径共享 registry，释放始终恰好一次。
                if (container != null) container.Dispose();
                else _owned.Dispose("ContainerBuilder");
                throw;
            }

            return container;
        }

        // 引用相等去重（不走 Equals——值实例可能重写 Equals，注入按对象身份去重才正确）。注册量级小，线性扫足够。
        private static bool ContainsReference<T>(List<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        private static bool ContainsBinding(Dictionary<Type, ContainerBinding> bindings, ContainerBinding binding)
        {
            foreach (var value in bindings.Values)
                if (ReferenceEquals(value, binding)) return true;
            return false;
        }

        private static void ValidateContracts(Type[] contracts)
        {
            if (contracts == null || contracts.Length == 0)
                throw new ArgumentException("至少需要一个契约类型。", nameof(contracts));
            for (int i = 0; i < contracts.Length; i++)
                if (contracts[i] == null)
                    throw new ArgumentException($"索引 {i} 处的契约类型为 null。", nameof(contracts));
        }

        private static void ValidateValueInstance(object instance, Type[] contracts)
        {
            for (int i = 0; i < contracts.Length; i++)
                if (!contracts[i].IsInstanceOfType(instance))
                    throw new ArgumentException(
                        $"[ContainerBuilder] 值 '{instance.GetType().Name}' 不能赋给契约 '{contracts[i].Name}'。",
                        nameof(contracts));
        }

        /// <summary>
        /// 层感知入口与 Mono 自动注册、服务安装器生成保持同一口径。低层 <c>RegisterValue/RegisterOwned</c>
        /// 仍只登记调用方显式给出的 contract，供非分层对象或选择性暴露契约的高级接线使用。
        /// </summary>
        private static Type[] GetLayerContracts(object value, Type expectedLayer)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));

            Type concreteType = value.GetType();
            LayerInterfacesCache.ValidateSingleLayer(
                concreteType,
                expectedLayer,
                nameof(ContainerBuilder),
                nameof(value));

            Type[] interfaces = LayerInterfacesCache.GetLayerInterfaces(concreteType, expectedLayer);
            var contracts = new Type[interfaces.Length + 1];
            contracts[0] = concreteType;
            Array.Copy(interfaces, 0, contracts, 1, interfaces.Length);
            return contracts;
        }

        /// <summary>
        /// 放弃尚未 Build 的构建事务并释放已登记 owned 资源。Build 已消费 Builder 后调用为 no-op，
        /// 因为所有权已经转交给 Container。生产代码应优先用 <c>using var builder</c> 覆盖异常路径。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_built)
                _owned.Dispose("ContainerBuilder");
        }

        private void ThrowIfBuilt()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ContainerBuilder),
                    "[ContainerBuilder] Builder 已释放，不能再接受注册或调用 Build()。");
            if (_built)
                throw new InvalidOperationException(
                    "[ContainerBuilder] Builder 已被 Build() 消费；如需继续注册，请创建新的 Builder。");
        }
    }
}
