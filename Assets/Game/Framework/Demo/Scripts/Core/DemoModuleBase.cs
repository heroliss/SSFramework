using Game.Framework;
using Game.Framework.Context;
using Game.Framework.Internal;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// 演示模块基类。扮演框架里的 <b>"View 角色"</b>：持有 demo Context、享有与真实 <c>IView</c> 一致的权限
    /// （<see cref="ICanSendCommand"/> + <see cref="ICanRegisterEvent"/> + <see cref="ICanGetUtility"/>），用 <see cref="Bag"/> 管理订阅与资源——
    /// 模块就走这条和真实 View 一样的路径调用框架，正好验证 API 手感。
    /// </summary>
    /// <remarks>
    /// 和 <c>MonoViewBase</c> 一样把 <see cref="IGameContext"/> 做成<b>显式接口实现</b>：子类拿不到完整 Context，
    /// 只能用扩展方法（<c>this.ExecuteCommand(...)</c> / <c>this.RegisterEvent(...)</c> / <c>this.GetUtility(...)</c>），
    /// 受权限接口约束——模块因此无法越权 GetModel/SendEvent，教学上示范的就是"正确的 View 写法"。
    /// </remarks>
    public abstract class DemoModuleBase : IDemoModule, IHasGameContext, ICanSendCommand, ICanRegisterEvent, ICanGetUtility
    {
        private IGameContext _context;
        private DisposableBag _bag;

        // 显式实现：子类只能用扩展方法访问框架，拿不到完整 IGameContext。
        IGameContext IHasGameContext.Context => _context;

        /// <summary>
        /// 本模块的生命周期容器：订阅（R3 / Framework Event）、资源加载、对象池租借等都登记到这里，
        /// <see cref="Teardown"/>（切走模块）时统一释放。
        /// </summary>
        protected DisposableBag Bag => _bag ??= new DisposableBag(_context);

        public abstract string Id { get; }
        public abstract string Title { get; }
        public virtual string Category => "核心";
        public virtual int Order => 0;
        public virtual string Summary => string.Empty;
        public virtual bool IsComingSoon => false;

        /// <summary>
        /// 默认不贡献绑定；需要自己的 Model/System 的模块覆写它。
        /// ⚠ 本方法在<b>临时实例</b>上被调（<c>MonoDemoContext</c> 收集绑定用；驱动 UI 的模块由外壳另行实例化）——
        /// 覆写时不要把对象存进字段留给 <see cref="Build"/> 用（UI 实例上是 null），Build 需要什么一律从 Context 解析。
        /// </summary>
        public virtual void InstallBindings(ContainerBuilder builder) { }

        public void Initialize(IGameContext context) => _context = context;

        public abstract void Build(DemoModuleHost host);

        public void Teardown()
        {
            _bag?.Dispose();
            _bag = null;
        }
    }
}
