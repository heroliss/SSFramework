using System;
using System.Collections.Generic;
using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.Context
{
    /// <summary>
    /// 精简容器构建器。支持值注册、工厂注册、父级容器、Eager 解析。
    /// 解析语义为"后注册覆盖先注册"，因此同一契约多次注册时仅保留最后一次写入。
    /// </summary>
    public sealed class ContainerBuilder
    {
        // 与 Container._bindings 同结构：value 为实例 OR Func<Container, object> 工厂
        private readonly Dictionary<Type, object> _bindings = new();
        private readonly List<Type> _eagerSeeds = new();
        // RegisterOwned 登记的"Context 拥有"实例：Build 时交给 Container，GameContext.Dispose 时统一 Dispose。
        private readonly List<IDisposable> _owned = new();
        private Container _parent;
        private bool _built;

        /// <summary>设置父级容器，解析时未命中将回退到父级。</summary>
        public ContainerBuilder SetParent(Container parent)
        {
            ThrowIfBuilt();
            _parent = parent;
            return this;
        }

        /// <summary>
        /// 注册一个值实例到指定的契约类型。
        /// 同一契约多次注册时，后注册覆盖先注册。
        /// </summary>
        public ContainerBuilder RegisterValue(object value, params Type[] contracts)
        {
            ThrowIfBuilt();
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (contracts == null || contracts.Length == 0)
                throw new ArgumentException("At least one contract type required", nameof(contracts));

            for (int i = 0; i < contracts.Length; i++)
            {
                var contract = contracts[i];
#if UNITY_EDITOR
                if (!contract.IsInstanceOfType(value))
                {
                    Debug.LogError(
                        $"[ContainerBuilder] '{value.GetType().Name}' is not assignable to contract '{contract.Name}'");
                }
#endif
                _bindings[contract] = value;
            }
            return this;
        }

        /// <summary>
        /// 注册一个由 Context <b>拥有</b>的 <see cref="IDisposable"/> 实例：除按契约注册（同 <see cref="RegisterValue"/>）外，
        /// 还登记为"Context 拥有"，在 <c>GameContext.Dispose()</c> 时自动 Dispose（逆序释放）。
        /// 用于生命周期应跟随 Context 的工具（如 <c>PoolUtility</c>）。普通 <see cref="RegisterValue"/> 不拥有实例
        /// （容器不替外部传入的共享实例兜底释放）。
        /// </summary>
        /// <remarks>
        /// 同一实例要注册到多个契约时<b>一次调用传多个 contract</b>（只登记一次拥有关系）；不要对同一实例多次调用本方法，
        /// 否则它会在 Dispose 时被 Dispose 多次。owned 实例应保证 <c>Dispose</c> 幂等（与 .NET 约定一致）。
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
        /// </list>
        /// </remarks>
        public ContainerBuilder RegisterFactory(
            Func<Container, object> factory,
            Resolution resolution,
            params Type[] contracts)
        {
            ThrowIfBuilt();
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (contracts == null || contracts.Length == 0)
                throw new ArgumentException("At least one contract type required", nameof(contracts));

            // 多契约共享同一实例：用闭包包装，确保工厂只调用一次，所有契约返回同一对象。
            // 单契约时不包装，避免多余闭包开销。
            if (contracts.Length > 1)
            {
                var original = factory;
                object shared = null;
                factory = c => shared ??= original(c);
            }

            for (int i = 0; i < contracts.Length; i++)
            {
                _bindings[contracts[i]] = factory;
                // Eager 模式只需 seed 第一个契约：工厂调用后 shared 已填充，后续契约直接返回缓存值。
                if (resolution == Resolution.Eager && i == 0)
                    _eagerSeeds.Add(contracts[i]);
            }
            return this;
        }

        /// <summary>RegisterFactory 的 Lazy 简化重载。</summary>
        public ContainerBuilder RegisterFactory(Func<Container, object> factory, params Type[] contracts)
            => RegisterFactory(factory, Resolution.Lazy, contracts);

        /// <summary>
        /// 构建容器。绑定表复制为新字典，Builder 不再可用（再次调用 RegisterXxx 会抛异常）。
        /// 若有 Eager 工厂，会在此一并构造，启动期就暴露配置错误。
        /// </summary>
        public Container Build()
        {
            ThrowIfBuilt();
            _built = true;

            var copy = new Dictionary<Type, object>(_bindings);

            // 收集构建完成时仍生效的值绑定实例（同一实例多契约只收一次）：GameContext 构造时对它们统一
            // Inject + AttachTo，使纯 C# 路径与 Mono 路径「注册即注入」语义对称（ADR-0019）。
            // 在 Eager 工厂解析前收集——工厂产物（含 Eager）刻意不进此列表，工厂经 Func<Container, object> 显式接线。
            // 被后续注册覆盖掉的值实例不在 copy.Values 里，自然不会被注入。
            var boundValues = new List<object>();
            foreach (var stored in copy.Values)
                if (stored is not Func<Container, object> && !ContainsReference(boundValues, stored))
                    boundValues.Add(stored);

            var container = new Container(copy, _parent, _owned, boundValues);

            // Eager 工厂在 Build 末尾立即 Resolve：失败的依赖在启动期就抛出来
            for (int i = 0; i < _eagerSeeds.Count; i++)
                container.Resolve(_eagerSeeds[i]);

            return container;
        }

        // 引用相等去重（不走 Equals——值实例可能重写 Equals，注入按对象身份去重才正确）。注册量级小，线性扫足够。
        private static bool ContainsReference(List<object> list, object item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        private void ThrowIfBuilt()
        {
            if (_built)
                throw new InvalidOperationException(
                    "[ContainerBuilder] Builder has already been consumed by Build(). Create a new builder for additional registrations.");
        }
    }
}
