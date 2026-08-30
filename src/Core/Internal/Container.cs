using System;
using System.Collections.Generic;

namespace Game.Framework.Internal
{
#if UNITY_EDITOR
    /// <summary>一次成功解析离开请求 Container 的实际路径。只用于 Editor 诊断，不进入玩家程序集。</summary>
    internal enum ContainerFallbackKind
    {
        ParentChain,
        Main,
    }

    /// <summary>
    /// 某个契约自 Context 创建以来实际发生过的回退证据。按“契约 + 来源 Container + 路径”聚合，
    /// 只记录成功解析；检查绑定或失败探测不会制造证据。
    /// </summary>
    internal readonly struct ContainerFallbackResolutionDetail
    {
        internal Type Contract { get; }
        internal Container Source { get; }
        internal ContainerFallbackKind Kind { get; }
        internal int Count { get; }

        internal ContainerFallbackResolutionDetail(
            Type contract,
            Container source,
            ContainerFallbackKind kind,
            int count)
        {
            Contract = contract;
            Source = source;
            Kind = kind;
            Count = count;
        }
    }
#endif

    /// <summary>
    /// 精简 DI 容器。支持值注册、工厂注册、类型解析、父级回退，以及运行时动态注册覆盖。
    /// 解析顺序：运行时覆盖层 → 构建时绑定 → 父级递归。
    /// 后注册覆盖先注册（同一契约多次注册时只保留最后一个）。
    /// </summary>
    /// <remarks>
    /// <b>线程契约：主线程独占。</b>所有 <see cref="Resolve"/> / <see cref="TryResolve"/> /
    /// <see cref="ReplaceOverride"/> 等方法均不加锁，业务必须在 Unity 主线程使用容器。
    /// 框架的所有 Awake / OnDestroy / Command / Event 路径都遵守这一约定，故 hot path 不付并发开销。
    /// 跨线程访问的检测在 Editor / Development Build 下由 <see cref="MainThreadGuard"/> 兜底。
    /// </remarks>
    public sealed class Container
    {
        // 构建时绑定显式建模为 ContainerBinding；值/工厂不再共用 object + runtime type tag。
        private readonly Dictionary<Type, ContainerBinding> _bindings;

        // 运行时动态注册覆盖层（MonoXxxBase OnEnable / GameContext.Register* 写入这里）
        private readonly Dictionary<Type, object> _overrides = new();

        private readonly Container _parent;

        // RegisterOwned / RegisterOwnedFactory 登记的"本容器拥有"实例。与 Builder 共享同一所有权 registry：
        // Build 提交前 Builder 负责回滚，提交后 Container 负责 Context 生命周期；懒工厂仍可安全追加。
        private readonly OwnedDisposables _owned;

        // 构建完成时仍生效的值绑定实例（去重）。GameContext 构造时逐个 Inject + AttachTo（ADR-0019），
        // 之后不再使用；工厂产物不在其中。
        private readonly IReadOnlyList<object> _boundValues;
        private bool _disposed;

#if UNITY_EDITOR
        /// <summary>一条回退聚合。来源使用弱引用，避免证据延长已替换 Main 的生命周期。</summary>
        private sealed class FallbackResolutionCounter
        {
            internal Type Contract { get; }
            internal WeakReference<Container> Source { get; }
            internal ContainerFallbackKind Kind { get; }
            internal int Count { get; set; }

            internal FallbackResolutionCounter(Type contract, Container source, ContainerFallbackKind kind)
            {
                Contract = contract;
                Source = new WeakReference<Container>(source);
                Kind = kind;
                Count = 1;
            }
        }

        // 惰性分配：只有实际发生父链 / Main 回退的 Context 才承担列表成本。
        // 回退路径通常极少，线性合并换来不强引用旧 Main，且无需为弱引用另造不可靠的哈希键。
        private List<FallbackResolutionCounter> _fallbackResolutionCounters;
#endif

        internal Container(
            Dictionary<Type, ContainerBinding> bindings,
            Container parent = null,
            OwnedDisposables owned = null,
            IReadOnlyList<object> boundValues = null)
        {
            _bindings = bindings;
            _parent = parent;
            _owned = owned ?? new OwnedDisposables();
            _boundValues = boundValues;
        }

        /// <summary>构建期值绑定的去重实例列表，供 GameContext 构造时统一注入；可能为 null（直接构造的容器）。</summary>
        internal IReadOnlyList<object> BoundValues => _boundValues;

        /// <summary>父级容器；根容器为 null。诊断面板用它还原 Context 作用域树（ADR-0026），不参与解析逻辑。</summary>
        internal Container Parent => _parent;

        /// <summary>
        /// 是否存在指定类型的绑定。
        /// recursive=true 时沿父级链递归查找（默认）；recursive=false 时只查本地。
        /// </summary>
        public bool HasBinding(Type type, bool recursive = true)
        {
            ThrowIfDisposed();
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (_overrides.ContainsKey(type) || _bindings.ContainsKey(type)) return true;
            return recursive && _parent != null && _parent.HasBinding(type, recursive: true);
        }

        /// <summary>解析指定类型。未找到抛 InvalidOperationException。</summary>
        public object Resolve(Type type)
        {
            if (TryResolve(type, out var instance)) return instance;
            throw new InvalidOperationException($"[Container] 未注册类型 '{type.Name}'。");
        }

        /// <summary>
        /// 尝试解析指定类型。查找顺序：
        /// 1. 运行时覆盖层 _overrides（动态 Register 写入）
        /// 2. 构建时绑定 _bindings（工厂在此处懒构造并缓存）
        /// 3. 父级容器（递归）
        /// </summary>
        public bool TryResolve(Type type, out object instance)
        {
            ThrowIfDisposed();
            if (type == null) throw new ArgumentNullException(nameof(type));
#if UNITY_EDITOR
            return TryResolveWithSourceValidated(type, out instance, out _);
#else
            return TryResolveCore(type, out instance);
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// 与 <see cref="TryResolve(Type,out object)"/> 相同，但同时返回最终命中的 Container。
        /// GameContext 用它区分本地 / 父链 / Main；成功的父链命中在请求 Container 上记录一次 Editor 证据。
        /// </summary>
        internal bool TryResolveWithSource(Type type, out object instance, out Container source)
        {
            ThrowIfDisposed();
            if (type == null) throw new ArgumentNullException(nameof(type));
            return TryResolveWithSourceValidated(type, out instance, out source);
        }

        private bool TryResolveWithSourceValidated(Type type, out object instance, out Container source)
        {
            bool resolved = TryResolveCore(type, out instance, out source);
            if (resolved && !ReferenceEquals(source, this))
                RecordFallback(type, source, ContainerFallbackKind.ParentChain);
            return resolved;
        }

        private bool TryResolveCore(Type type, out object instance, out Container source)
        {
            if (TryResolveLocal(type, out instance))
            {
                source = this;
                return true;
            }
            if (_parent != null)
            {
                _parent.ThrowIfDisposed();
                return _parent.TryResolveCore(type, out instance, out source);
            }
            instance = null;
            source = null;
            return false;
        }
#else
        private bool TryResolveCore(Type type, out object instance)
        {
            if (TryResolveLocal(type, out instance)) return true;
            if (_parent != null)
            {
                _parent.ThrowIfDisposed();
                return _parent.TryResolveCore(type, out instance);
            }
            instance = null;
            return false;
        }
#endif

        /// <summary>只查当前 Container；返回 false 时由调用方决定是否继续父链。</summary>
        private bool TryResolveLocal(Type type, out object instance)
        {
            if (_overrides.TryGetValue(type, out instance)) return true;
            if (!_bindings.TryGetValue(type, out var stored))
            {
                instance = null;
                return false;
            }

            if (stored.IsFactory)
                MainThreadGuard.AssertMainThread(nameof(Container));
            instance = stored.Resolve(this);

            // Factory 是用户回调，期间可能重入当前 Context 并为同一契约写入 runtime override。
            // 解析返回前重新遵守“override 优先”规则，避免本次拿到 Factory 产物、下一次却立刻切到 override。
            if (_overrides.TryGetValue(type, out var latestOverride))
                instance = latestOverride;
            if (!type.IsInstanceOfType(instance))
                throw new InvalidOperationException(
                    $"[Container] 契约 '{type.Name}' 的绑定返回了不兼容实例 '{instance.GetType().Name}'。");
            return true;
        }

#if UNITY_EDITOR
        /// <summary>本 Container 发起的成功父链 / Main 回退聚合；诊断读取不会触发解析。</summary>
        internal IEnumerable<ContainerFallbackResolutionDetail> FallbackResolutionDetails
        {
            get
            {
                if (_fallbackResolutionCounters == null) yield break;
                foreach (var counter in _fallbackResolutionCounters)
                {
                    counter.Source.TryGetTarget(out var source);
                    yield return new ContainerFallbackResolutionDetail(
                        counter.Contract,
                        source,
                        counter.Kind,
                        counter.Count);
                }
            }
        }

        /// <summary>记录一次成功的外部解析。Main 回退由 GameContext 在跨树解析成功后调用。</summary>
        internal void RecordFallback(Type contract, Container source, ContainerFallbackKind kind)
        {
            if (contract == null || source == null || ReferenceEquals(source, this)) return;
            var counters = _fallbackResolutionCounters ??= new List<FallbackResolutionCounter>();
            foreach (var counter in counters)
            {
                if (counter.Contract != contract || counter.Kind != kind ||
                    !counter.Source.TryGetTarget(out var existing) || !ReferenceEquals(existing, source))
                    continue;

                if (counter.Count < int.MaxValue) counter.Count++;
                return;
            }
            counters.Add(new FallbackResolutionCounter(contract, source, kind));
        }
#endif

        /// <summary>仅查本地覆盖层（不查 _bindings、不查父级）。供 host 做"运行时覆盖父级"检测使用。</summary>
        internal bool TryGetOverride(Type type, out object instance)
        {
            ThrowIfDisposed();
            return _overrides.TryGetValue(type, out instance);
        }

        /// <summary>写入或替换覆盖层条目。重复注册策略由 ContainerLayerExtensions.RegisterOverride 统一处理。</summary>
        internal void ReplaceOverride(Type contractType, object instance)
        {
            ThrowIfDisposed();
            _overrides[contractType] = instance;
        }

        /// <summary>运行时注销覆盖层条目。仅当当前值与传入实例匹配时才移除。</summary>
        internal void RemoveOverride(Type contractType, object instance)
        {
            ThrowIfDisposed();
            if (_overrides.TryGetValue(contractType, out var existing) && existing == instance)
                _overrides.Remove(contractType);
        }

        /// <summary>
        /// 接管一个工厂产物的生命周期。按对象引用去重，确保同一实例经多个契约解析时最多释放一次。
        /// 仅供 <c>ContainerBuilder.RegisterOwnedFactory</c> 在首次构造成功后调用。
        /// </summary>
        internal void Own(IDisposable instance)
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            _owned.Add(instance);
        }

        /// <summary>
        /// 判断实例是否已由当前 Container 的 ownership registry 持有。供 OwnedFactory 失败回滚区分
        /// “本次尚未提交的产物”与“其它有效绑定已经拥有的共享实例”，避免错误提前释放后者。
        /// </summary>
        internal bool HasEverOwned(IDisposable instance)
        {
            // 失败回滚可能发生在 Factory 重入导致 Container 已释放之后。registry 会以弱引用保留“曾经拥有”
            // 证据：已尝试释放的 alias 不能再 Dispose 第二次，同时又不让已关闭 Context 强持有服务实例。
            return _owned.ContainsCurrentOrReleased(instance);
        }

        /// <summary>
        /// 诊断用：本容器<b>本地</b>注册的契约键（不含父级回退）——运行时覆盖层 + 构建时绑定的并集（override 遮蔽同名 binding，去重）。
        /// 供 Inspector 只读展示「这个 Context 注册了什么」，不参与解析逻辑。
        /// </summary>
        internal IEnumerable<Type> LocalRegistrations
        {
            get
            {
                foreach (var k in _overrides.Keys) yield return k;
                foreach (var k in _bindings.Keys)
                    if (!_overrides.ContainsKey(k)) yield return k;
            }
        }

        /// <summary>
        /// 诊断用：本容器<b>本地</b>注册明细（不含父级回退），供诊断面板展示「契约 → 实例」。
        /// 与 <see cref="LocalRegistrations"/> 同一套键集，额外给出实例与来源：
        /// <c>Instance</c> 为 null 且 <c>IsPendingFactory</c> 为 true 表示「工厂绑定、尚未首次解析」——
        /// <b>刻意不触发工厂</b>（诊断不得改变被观察系统的状态）。<c>IsOverride</c> 区分运行时覆盖与构建时绑定。
        /// </summary>
        internal IEnumerable<(Type Contract, object Instance, bool IsOverride, bool IsPendingFactory)> LocalRegistrationDetails
        {
            get
            {
                foreach (var kv in _overrides)
                    yield return (kv.Key, kv.Value, true, false);
                foreach (var kv in _bindings)
                {
                    if (_overrides.ContainsKey(kv.Key)) continue;
                    bool pending = kv.Value.IsFactory && !kv.Value.IsResolved;
                    yield return (kv.Key, pending ? null : kv.Value.Instance, false, pending);
                }
            }
        }

        /// <summary>
        /// Dispose 本容器<b>拥有</b>的实例（经 <c>ContainerBuilder.RegisterOwned</c> 或
        /// <c>ContainerBuilder.RegisterOwnedFactory</c> 登记的）。逆序释放，单个实例抛异常不影响其余；幂等。
        /// 普通 RegisterValue / RegisterFactory 产物<b>不</b>在此释放——容器不拥有外部传入实例。
        /// 由 <c>GameContext.Dispose</c> 调用，不对外公开。
        /// </summary>
        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
#if UNITY_EDITOR
            _fallbackResolutionCounters?.Clear();
#endif
            _owned.Dispose("Container");
        }

        /// <summary>
        /// Factory 等内部提交事务在用户回调返回后复检生命周期。必须是 internal，避免只在 Resolve 入口
        /// 检查一次后，回调重入 Dispose 仍能把迟到实例发布进已关闭 Container。
        /// </summary>
        internal void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Container),
                    "[Container] 所属 Context 已释放，不能再解析或修改注册项。");
        }
    }
}
