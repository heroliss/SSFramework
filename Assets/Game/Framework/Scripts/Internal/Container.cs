using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 精简 DI 容器。支持值注册、工厂注册、类型解析、父级回退，以及运行时动态注册覆盖。
    /// 解析顺序：运行时覆盖层 → 构建时绑定 → 父级递归。
    /// 后注册覆盖先注册（同一契约多次注册时只保留最后一个）。
    /// </summary>
    /// <remarks>
    /// <b>线程契约：主线程独占。</b>所有 <see cref="Resolve"/> / <see cref="TryResolve"/> /
    /// <see cref="ReplaceOverride"/> 等方法均不加锁，业务必须在 Unity 主线程使用容器。
    /// 框架的所有 Awake / OnDestroy / Command / Event 路径都遵守这一约定，故 hot path 不付并发开销。
    /// 跨线程访问的检测在 Editor / Development Build 下由 <see cref="AssertMainThread"/> 兜底。
    /// </remarks>
    public sealed class Container
    {
        // 构建时绑定：value 为实例直接返回；value 为 Func<Container, object> 时是工厂，首次 Resolve 调用并缓存
        private readonly Dictionary<Type, object> _bindings;

        // 运行时动态注册覆盖层（MonoXxxBase OnEnable / GameContext.Register* 写入这里）
        private readonly Dictionary<Type, object> _overrides = new();

        private readonly Container _parent;

        // RegisterOwned 登记的"本容器拥有"实例：仅这些在 Dispose 时释放（普通 RegisterValue/工厂实例不碰）。
        private readonly IReadOnlyList<IDisposable> _owned;
        private bool _disposed;

        // 主线程 ID 捕获：static ctor 在第一次访问 Container 时跑，正常路径下都在主线程。
        // Editor / Development Build 下 AssertMainThread 用此 ID 做断言。
        private static readonly int s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

        internal Container(Dictionary<Type, object> bindings, Container parent = null, IReadOnlyList<IDisposable> owned = null)
        {
            _bindings = bindings;
            _parent = parent;
            _owned = owned;
        }

        /// <summary>
        /// 是否存在指定类型的绑定。
        /// recursive=true 时沿父级链递归查找（默认）；recursive=false 时只查本地。
        /// </summary>
        public bool HasBinding(Type type, bool recursive = true)
        {
            if (_overrides.ContainsKey(type) || _bindings.ContainsKey(type)) return true;
            return recursive && _parent != null && _parent.HasBinding(type, recursive: true);
        }

        /// <summary>解析指定类型。未找到抛 InvalidOperationException。</summary>
        public object Resolve(Type type)
        {
            if (TryResolve(type, out var instance)) return instance;
            throw new InvalidOperationException($"[Container] {type.Name} not registered");
        }

        /// <summary>
        /// 尝试解析指定类型。查找顺序：
        /// 1. 运行时覆盖层 _overrides（动态 Register 写入）
        /// 2. 构建时绑定 _bindings（工厂在此处懒构造并缓存）
        /// 3. 父级容器（递归）
        /// </summary>
        public bool TryResolve(Type type, out object instance)
        {
            if (_overrides.TryGetValue(type, out instance)) return true;
            if (_bindings.TryGetValue(type, out var stored))
            {
                if (stored is Func<Container, object> factory)
                {
                    // 工厂分支是唯一的"写入 _bindings"路径。容器是主线程独占，这里只在 Editor/Development 加断言兜底；
                    // Release Build 下 Conditional 编译消除，零开销。
                    AssertMainThread();
                    instance = factory(this);
                    if (instance == null)
                        throw new InvalidOperationException(
                            $"[Container] Factory for '{type.Name}' returned null");
                    _bindings[type] = instance; // 缓存：后续 Resolve 直接返回实例
                    return true;
                }
                instance = stored;
                return true;
            }
            if (_parent != null) return _parent.TryResolve(type, out instance);
            instance = null;
            return false;
        }

        /// <summary>
        /// 断言当前在 Unity 主线程。Editor 与 Development Build 下生效，Release 编译消除。
        /// </summary>
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        private static void AssertMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                Debug.LogError(
                    "[Container] Container is not thread-safe; must be accessed from the Unity main thread.");
        }

        /// <summary>仅查本地覆盖层（不查 _bindings、不查父级）。供 host 做"运行时覆盖父级"检测使用。</summary>
        internal bool TryGetOverride(Type type, out object instance)
            => _overrides.TryGetValue(type, out instance);

        /// <summary>写入或替换覆盖层条目。重复注册策略由 ContainerLayerExtensions.RegisterOverride 统一处理。</summary>
        internal void ReplaceOverride(Type contractType, object instance)
            => _overrides[contractType] = instance;

        /// <summary>运行时注销覆盖层条目。仅当当前值与传入实例匹配时才移除。</summary>
        internal void RemoveOverride(Type contractType, object instance)
        {
            if (_overrides.TryGetValue(contractType, out var existing) && existing == instance)
                _overrides.Remove(contractType);
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
        /// Dispose 本容器<b>拥有</b>的实例（经 <c>ContainerBuilder.RegisterOwned</c> 登记的）。逆序释放，
        /// 单个实例抛异常不影响其余；幂等。普通 RegisterValue / 工厂实例<b>不</b>在此释放——容器不拥有外部传入实例。
        /// 由 <c>GameContext.Dispose</c> 调用，不对外公开。
        /// </summary>
        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_owned == null) return;
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try { _owned[i].Dispose(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
