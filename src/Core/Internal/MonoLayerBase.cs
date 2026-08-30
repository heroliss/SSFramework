using System;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// Model / System / Utility 三个会注册到容器的 Mono 层基类的<b>共享实现</b>。
    /// 把"查目标 Context → 注入 <c>[Inject]</c> 字段 → 绑定 AssetReference → 最后注册到容器 → OnDestroy 释放并反注册"
    /// 这套对三层完全一致的样板收敛到一处，三个具体基类只保留 <c>[DefaultExecutionOrder]</c> 与层标记接口。
    /// </summary>
    /// <remarks>
    /// <b>谁该继承：</b>框架内部的 <see cref="Game.Framework.Model.MonoModelBase"/> /
    /// <see cref="Game.Framework.Systems.MonoSystemBase"/> / <see cref="Game.Framework.Utility.MonoUtilityBase"/>。
    /// 业务<b>不要</b>直接继承本类——继承对应的 <c>MonoXxxBase</c> 才能拿到正确的执行顺序与层标记。<br/>
    /// <b>为什么是泛型：</b><typeparamref name="TLayer"/> 即层标记接口（<c>IModel</c>/<c>ISystem</c>/<c>IUtility</c>），
    /// 直接驱动 <see cref="MonoLayerExtensions.ResolveLayerContext{TLayer}"/> 的查找与 <see cref="ContainerLayerExtensions.UnregisterFor{TLayer}"/> 的反注册。
    /// 具体类形如 <c>MonoModelBase : MonoLayerBase&lt;IModel&gt;, IModel</c>——基类无法 <c>: TLayer</c>，故层标记由具体类实现。<br/>
    /// <b>执行顺序：</b>本类<b>不</b>标 <c>[DefaultExecutionOrder]</c>；顺序按层在各具体类上声明（Utility -400 / Model -300 / System -200）。<br/>
    /// <b>边界：</b>子类覆写 <see cref="Awake"/> / <see cref="OnDestroy"/> 时必须调 <c>base.Xxx()</c>；
    /// OnDestroy 反注册前检查父 Context 是否已 Dispose，跳过避免 NRE（详见 <c>Assets/Game/AGENTS.md</c>
    /// 「Mono 生命周期与 Context」）。
    /// </remarks>
    /// <typeparam name="TLayer">层标记接口：<c>IModel</c> / <c>ISystem</c> / <c>IUtility</c>。</typeparam>
    public abstract class MonoLayerBase<TLayer> : MonoBehaviour, IHasGameContext where TLayer : class
    {
        /// <summary>
        /// 旧版派生 Utility 用来合并 Inspector 诊断分组的兼容常量。Framework 原生 Inspector 不再读取它；
        /// 业务侧自有 Inspector/Odin Attribute 可在迁移期继续引用。
        /// </summary>
        [System.Obsolete("Framework 原生诊断不再使用字符串分组；请把业务诊断放进自己的 Editor Adapter。")]
        protected const string DiagGroup = "运行时诊断";

        [SerializeField]
        [LockInPlayMode]
        [Tooltip(
            "显式指定要注册到的 Context。\n" +
            "• 留空：自动查找 Transform 层级中最近的父级 MonoGameContextBase\n" +
            "• 拖入 MonoGameContextBase：强制注册到指定场景 Context")]
        private MonoGameContextBase _targetContext;

        private IGameContext _contextProvider;
        private DisposableBag _bag;
        private bool _registered;
        private bool _tearingDown;

        // 显式接口实现：业务子类无法通过 this.Context 访问完整 IGameContext，
        // 只能用扩展方法（this.GetXxx<T>() 等），由 ICanXxx 权限接口约束各层能做什么。
        // 框架内部通过 IHasGameContext 拿到。
        IGameContext IHasGameContext.Context => _contextProvider;

        /// <summary>
        /// 本层生命周期容器——加载与本层同寿命的资源、订阅事件、登记任意 <see cref="System.IDisposable"/>。
        /// AssetReference 字段会在 Awake 时自动加入此 bag；本层反注册前会先 Dispose bag。
        /// 延迟创建：不访问此属性的层不会分配 bag。
        /// </summary>
        protected DisposableBag Bag => _bag ??= new DisposableBag(_contextProvider);

#if UNITY_EDITOR
        /// <summary>
        /// Editor 工具只读解析本组件明确或按 Transform 父链归属的 Context 宿主。
        /// 不回退 <see cref="GameContext.Main"/>：Edit Mode 无法可靠推断未来运行时的全局主 Context，
        /// 迁移器遇到这种无宿主接线时只能按同 GameObject 的最窄范围处理。
        /// </summary>
        internal MonoGameContextBase ResolveContextHostForEditor() =>
            _targetContext != null
                ? _targetContext
                : GetComponentInParent<MonoGameContextBase>(includeInactive: true);
#endif

        protected virtual void Awake()
        {
            var contextProvider = this.ResolveLayerContext<TLayer>(_targetContext);
            if (contextProvider == null) return;

            // 注册计划先做无副作用预检；Context 暂时写入私有字段，让 [Inject] 方法和 Bag 在初始化期间
            // 能使用当前层的合法扩展能力。对象只有通过注入、资源绑定和存活复检后才发布到 Container。
            var registration = ContextInternals.GetContainer(contextProvider)
                .PrepareRegistrationFor<TLayer>(this, $"{GetType().Name}({name})");
            _contextProvider = contextProvider;
            try
            {
                contextProvider.Inject(this);

                // AssetReference 字段自动绑定加载器并加入 Bag，由 Bag.Dispose 统一释放。
                // Bag / utility 延迟解析：没有 AssetReference 字段的层不会触发 Bag 创建或 utility 解析。
                AssetReferenceBinder.BindAll(
                    this,
                    () => _contextProvider.TryResolve(typeof(IAssetUtility), out var utility) ? (IAssetUtility)utility : null,
                    this.GetCancellationTokenOnDestroy(),
                    () => Bag);

                EnsureInitializationCanCommit(contextProvider);
                ContainerLayerExtensions.CommitRegistration(registration);
                _registered = true;
                ContainerLayerExtensions.TraceRegistration(registration);
            }
            catch
            {
                TearDownLayer();
                throw;
            }
        }

        protected virtual void OnDestroy()
        {
            TearDownLayer();
        }

        private void EnsureInitializationCanCommit(IGameContext contextProvider)
        {
            if (this == null || _contextProvider == null)
                throw new MissingReferenceException(
                    $"[{typeof(TLayer).Name}] Mono 层在初始化完成前已被销毁，不能发布到 Container。");
            if (contextProvider.IsDisposed)
                throw new ObjectDisposedException(
                    nameof(IGameContext),
                    $"[{typeof(TLayer).Name}] 目标 Context 在 Mono 层初始化期间已释放，不能提交注册。");
        }

        /// <summary>
        /// Awake 失败与 OnDestroy 共用的幂等清理。Bag 回调可能同步 DestroyImmediate，再次进入 OnDestroy；
        /// guard 保证资源与注册只撤一次，次生清理异常只记日志、不覆盖最初的初始化异常。
        /// </summary>
        private void TearDownLayer()
        {
            if (_tearingDown) return;
            _tearingDown = true;
            var contextProvider = _contextProvider;

            // 先释放资源，再从容器反注册，保证释放逻辑里 Context 仍可用。
            try
            {
                _bag?.Dispose();
            }
            catch (Exception e)
            {
                Log.Error("Mono 层 Bag 清理失败；仍会继续撤销 Container 注册。", e, nameof(MonoLayerBase<TLayer>), this);
            }
            _bag = null;

            // 父级 MonoGameContextBase 执行顺序更靠前（-1000），可能先于本组件销毁；
            // 此时 Context 已 Dispose，跳过反注册避免 NRE（Container 也已失效）。
            try
            {
                if (_registered && contextProvider != null && !contextProvider.IsDisposed)
                    ContextInternals.GetContainer(contextProvider).UnregisterFor<TLayer>(this);
            }
            catch (Exception e)
            {
                Log.Error("Mono 层撤销 Container 注册失败；对象仍会结束本地生命周期。", e, nameof(MonoLayerBase<TLayer>), this);
            }
            finally
            {
                _registered = false;
                _contextProvider = null;
                _tearingDown = false;
            }
        }
    }
}
