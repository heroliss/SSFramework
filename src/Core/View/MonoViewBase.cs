using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using UnityEngine;

namespace Game.Framework.View
{
    /// <summary>
    /// View 的 Mono 实现基类。挂在 Canvas / 场景节点上，Awake 时自动注入 <c>[Inject]</c> 字段，
    /// OnDestroy 时自动释放 <see cref="Bag"/>（含所有订阅、资源句柄、AssetReference）。
    /// </summary>
    /// <remarks>
    /// <b>谁该用：</b>所有 UI 控件、HUD、特效触发器等"用户可见可交互"的 MonoBehaviour。
    /// 不需要注册到容器（View 不被其他层依赖），Awake 只做注入。<br/>
    /// <b>执行顺序：</b><c>DefaultExecutionOrder(-100)</c>，最晚执行。此时 Model / System / Utility 都已就绪，
    /// 在 Awake 内可以直接 <c>this.ExecuteCommand(new GetXxxStateCommand())</c> 拿订阅源。<br/>
    /// <b>边界（与 IView 权限对齐）：</b>
    /// <list type="bullet">
    ///   <item>View 不能 GetModel / GetSystem / SendEvent——通过 Command 接缝隔离业务层。</item>
    ///   <item><c>IHasGameContext.Context</c> 是<b>显式接口实现</b>——业务子类拿不到 <see cref="IGameContext"/>，
    ///         只能用扩展方法（<c>this.ExecuteCommand</c> / <c>this.RegisterEvent</c> / <c>this.GetUtility</c>），由权限接口约束。</item>
    ///   <item>子类覆写 <see cref="Awake"/> / <see cref="OnDestroy"/> 时必须调 <c>base.Xxx()</c>。</item>
    ///   <item>需要更短作用域的订阅（OnDisable / 回合期间）用 <c>Bag.CreateChild()</c> 拿子 bag，父级 Dispose 自动级联。</item>
    /// </list>
    /// </remarks>
    [DefaultExecutionOrder(-100)]
    public abstract class MonoViewBase : MonoBehaviour, IView, IHasGameContext
    {
        [SerializeField]
        [LockInPlayMode]
        [Tooltip(
            "显式指定要注入的 Context。\n" +
            "• 留空：自动查找 Transform 层级中最近的父级 MonoGameContextBase\n" +
            "• 拖入 MonoGameContextBase：强制从指定场景 Context 执行 [Inject]")]
        private MonoGameContextBase _targetContext;

        private IGameContext _contextProvider;
        private DisposableBag _bag;

        // 显式接口实现：业务子类无法通过 this.Context 访问完整 IGameContext，
        // 只能用扩展方法（this.GetUtility<T>() / this.RegisterEvent<T>() 等），
        // 由 ICanXxx 权限接口约束 View 层能做什么。框架内部通过 IHasGameContext 拿到。
        IGameContext IHasGameContext.Context => _contextProvider;

        /// <summary>
        /// View 生命周期容器——订阅（Observable、Framework Event、UnityEvent、C# delegate）、
        /// 资源加载（<c>Load</c>/<c>LoadScene</c>/<c>LoadText</c>...）、任意 <see cref="System.IDisposable"/>
        /// 统一登记，OnDestroy 时自动批量清理。
        /// AssetReference 字段会在 Awake 时自动绑定加载器并加入此 bag，跟 view 同寿命。
        /// 覆写 OnDestroy 时须调 base.OnDestroy()。
        /// </summary>
        protected DisposableBag Bag => _bag ??= new DisposableBag(_contextProvider);

        /// <summary>
        /// 创建跟当前 view 同上下文的额外 bag，用于更短作用域（OnDisable、回合期间等）。
        /// 子类自行持有字段并在对应回调里 Dispose，按"清理时机"前缀命名（_enableBag / _roundBag）。
        /// </summary>
        protected DisposableBag CreateBag() => new DisposableBag(_contextProvider);

        protected virtual void Awake()
        {
            _contextProvider = this.AttachView(_targetContext);
            if (_contextProvider != null)
            {
                // AssetReference 字段自动绑定加载器并加入 Bag,由 Bag.Dispose 统一释放。
                // Bag / utility 延迟解析：没有 AssetReference 字段的 view 不会触发 Bag 创建或 utility 解析。
                AssetReferenceBinder.BindAll(
                    this,
                    () => _contextProvider.TryResolve(typeof(IAssetUtility), out var utility) ? (IAssetUtility)utility : null,
                    this.GetCancellationTokenOnDestroy(),
                    () => Bag);
            }
        }

        protected virtual void OnDestroy()
        {
            _bag?.Dispose();
            _bag = null;
        }
    }
}
