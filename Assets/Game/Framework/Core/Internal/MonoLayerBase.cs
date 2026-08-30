using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// Model / System / Utility 三个会注册到容器的 Mono 层基类的<b>共享实现</b>。
    /// 把"查目标 Context → 注册到容器 → 注入 <c>[Inject]</c> 字段 → 绑定 AssetReference → OnDestroy 释放并反注册"
    /// 这套对三层完全一致的样板收敛到一处，三个具体基类只保留 <c>[DefaultExecutionOrder]</c> 与层标记接口。
    /// </summary>
    /// <remarks>
    /// <b>谁该继承：</b>框架内部的 <see cref="Game.Framework.Model.MonoModelBase"/> /
    /// <see cref="Game.Framework.Systems.MonoSystemBase"/> / <see cref="Game.Framework.Utility.MonoUtilityBase"/>。
    /// 业务<b>不要</b>直接继承本类——继承对应的 <c>MonoXxxBase</c> 才能拿到正确的执行顺序与层标记。<br/>
    /// <b>为什么是泛型：</b><typeparamref name="TLayer"/> 即层标记接口（<c>IModel</c>/<c>ISystem</c>/<c>IUtility</c>），
    /// 直接驱动 <see cref="MonoLayerExtensions.AttachLayer{TLayer}"/> 的注册与 <see cref="ContainerLayerExtensions.UnregisterFor{TLayer}"/> 的反注册。
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
            _contextProvider = this.AttachLayer<TLayer>(_targetContext);
            if (_contextProvider != null)
            {
                // AssetReference 字段自动绑定加载器并加入 Bag，由 Bag.Dispose 统一释放。
                // Bag / utility 延迟解析：没有 AssetReference 字段的层不会触发 Bag 创建或 utility 解析。
                AssetReferenceBinder.BindAll(
                    this,
                    () => _contextProvider.TryResolve(typeof(IAssetUtility), out var utility) ? (IAssetUtility)utility : null,
                    this.GetCancellationTokenOnDestroy(),
                    () => Bag);
            }
        }

        protected virtual void OnDestroy()
        {
            // 先释放资源，再从容器反注册，保证释放逻辑里 Context 仍可用。
            _bag?.Dispose();
            _bag = null;

            // 父级 MonoGameContextBase 执行顺序更靠前（-1000），可能先于本组件销毁；
            // 此时 Context 已 Dispose，跳过反注册避免 NRE（Container 也已失效）。
            if (_contextProvider != null && !_contextProvider.IsDisposed)
                ContextInternals.GetContainer(_contextProvider).UnregisterFor<TLayer>(this);
        }
    }
}
