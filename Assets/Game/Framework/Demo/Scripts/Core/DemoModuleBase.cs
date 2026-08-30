using System;
using Game.Framework;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.View;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 专用的章节 Adapter：集中章节元数据、装配 hook、教学 Build 与清理，不是 Model / System / View 之外的新层。
    /// 章节进入运行期交互后扮演 <see cref="IView"/> 角色，用 <see cref="Bag"/> 管理订阅与资源，
    /// 因而走和真实 View 相同的受限 API 路径并验证其使用体验。
    /// </summary>
    /// <remarks>
    /// <see cref="IDemoModule"/> 描述教学目录生命周期，<see cref="IView"/> 描述运行期权限；两个 Interface 正交，
    /// 不应让目录 Interface 继承某个框架层。和 <c>MonoViewBase</c> 一样把 <see cref="IGameContext"/> 做成
    /// <b>显式接口实现</b>：完整 Context 不出现在子类的普通成员查找中，日常代码只能使用
    /// <c>ExecuteCommand</c> / <c>RegisterEvent</c> / <c>GetUtility</c>。刻意强转仍可访问 Context，
    /// 仅限装配代码选择作用域；这是编译期使用护栏，不是安全沙箱。
    /// </remarks>
    public abstract class DemoModuleBase : IDemoModule, IView, IHasGameContext
    {
        private IGameContext _context;
        private DisposableBag _bag;

        // 显式实现：子类只能用扩展方法访问框架，拿不到完整 IGameContext。
        IGameContext IHasGameContext.Context => _context;

        /// <summary>
        /// 本模块的生命周期容器：订阅（R3 / Framework Event）、资源加载、对象池租借等都登记到这里，
        /// <see cref="Teardown"/>（切走模块）时统一释放。
        /// </summary>
        protected DisposableBag Bag => _bag ??= new DisposableBag(_context ?? throw new InvalidOperationException(
            $"{GetType().Name} 尚未 Initialize，不能创建章节 Bag。"));

        public abstract string Id { get; }
        public abstract string Title { get; }
        public virtual string Category => "核心";
        public virtual int Order => 0;
        public virtual string Summary => string.Empty;
        public virtual bool IsComingSoon => false;
        public virtual DemoTeachingKind TeachingKind => DemoTeachingKind.Capability;

        /// <summary>
        /// 默认不贡献绑定；需要自己的 Model/System/Utility 时覆写。目录会在同一实例上继续 Initialize 与 Build，
        /// 但本阶段仍只应描述容器注册关系：不要启动运行时工作，也不要绕过 Context 所有权把临时对象留给 Build。
        /// </summary>
        public virtual void InstallBindings(ContainerBuilder builder) { }

        public void Initialize(IGameContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_context != null)
                throw new InvalidOperationException($"{GetType().Name} 已经 Initialize，不能注入第二个 Context。");
            _context = context;
        }

        public abstract void Build(DemoModuleHost host);

        public void Teardown()
        {
            _bag?.Dispose();
            _bag = null;
        }
    }
}
