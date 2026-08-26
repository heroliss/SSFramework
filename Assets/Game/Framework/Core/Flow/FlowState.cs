using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;

namespace Game.Framework.Flow
{
    /// <summary>
    /// 流程状态基类：游戏的一个宏观阶段（启动 / 登录 / 大厅 / 战斗……）。
    /// 子类三个覆写点：<see cref="InstallBindings"/> 注册阶段私有服务、
    /// <see cref="OnEnter"/> 做进入工作（加载场景 / 预热资源 / 开 UI）、<see cref="OnExit"/> 做善后。
    /// </summary>
    /// <remarks>
    /// <b>一次性实例</b>：一个实例只能被 <see cref="IGameFlow.GoTo"/> 消费一次——进入参数走构造函数
    /// （<c>new BattleState(levelId)</c>），无跨次残留字段脏状态；重进同类状态就 new 一个新实例。<br/>
    /// <b>生命周期</b>：进入前框架以宿主 Context 为父级构建本状态的子 Context；退出（或 Enter 失败 / 被顶替 /
    /// 宿主 Dispose）时子 Context 整棵撤。因此阶段占用的一切——服务注册进 <see cref="InstallBindings"/>、
    /// 订阅与资源进 <see cref="Bag"/>——都随阶段结束自动清理，不依赖子类自觉。<br/>
    /// <b>时序约束</b>：<see cref="InstallBindings"/> 在子 Context 构建<b>前</b>被调，此时
    /// <see cref="Context"/> / <see cref="Bag"/> 尚不可用；本实例字段仍可用，框架随后会在同一实例上调用
    /// <see cref="OnEnter"/>。Context 与 Bag 从 <see cref="OnEnter"/> 起、到状态 scope 被撤除前可用；
    /// 宿主释放后的迟到 <c>OnExit</c> continuation 已越过这条边界。
    /// </remarks>
    public abstract class FlowState
    {
        private GameContext _scope;
        private DisposableBag _bag;

        /// <summary>本实例是否已被 GoTo 消费（一次性守卫）。框架内部写入。</summary>
        internal bool Consumed;

        /// <summary>
        /// 本状态的子 Context（父级 = flow 的宿主 Context，解析未命中自动回退父链）。
        /// 在 scope 存活的 <see cref="OnEnter"/> / <see cref="OnExit"/> 期间可用，其余时机为 null。
        /// 若宿主在 OnExit 挂起时释放，scope 会立即撤除，迟到 continuation 读取本属性也会得到 null。
        /// </summary>
        protected IGameContext Context => _scope;

        /// <summary>
        /// 本状态的生命周期容器：订阅（R3 / Framework Event）、资源加载、场景加载、池租借等都登记到这里，
        /// 状态退出（含 Enter 失败 / 被顶替 / 宿主 Dispose）时先于子 Context 统一释放。
        /// 仅在 scope 仍存活的 <see cref="OnEnter"/> / <see cref="OnExit"/> 期间可用。
        /// </summary>
        protected DisposableBag Bag
        {
            get
            {
                if (_scope == null)
                    throw new InvalidOperationException(
                        $"[FlowState] '{GetType().Name}' 的 Bag 仅在状态 scope 存活的 OnEnter/OnExit 期间可用——" +
                        "InstallBindings 时尚未构建，宿主释放后的迟到 OnExit 中则已经撤除。");
                return _bag ??= new DisposableBag(_scope);
            }
        }

        /// <summary>
        /// 注册本状态私有的服务（<c>RegisterOwned</c> 的实例随状态退出自动 Dispose）。
        /// 战斗内的子阶段机也在这里注册（再 RegisterOwned 一个 <see cref="GameFlow"/>，作用域树天然嵌套）。
        /// 默认不注册任何东西。
        /// </summary>
        protected internal virtual void InstallBindings(ContainerBuilder builder) { }

        /// <summary>
        /// 进入本状态：加载场景 / 预热资源 / 打开 UI / 请求服务器……
        /// <paramref name="ct"/> 在本次进入被更新的 GoTo 顶替或宿主 Dispose 时取消——
        /// 把它传给所有 await，取消后框架直接整棵撤子 Context（<b>不调 <see cref="OnExit"/></b>），
        /// 半加载的资源靠 <see cref="Bag"/> 放掉。抛异常 = 进入失败，异常从 GoTo 的 task 冒出。
        /// </summary>
        protected internal virtual UniTask OnEnter(CancellationToken ct) => UniTask.CompletedTask;

        /// <summary>
        /// 退出本状态（仅在 <see cref="OnEnter"/> 成功完成后的正常转换时被调；宿主 Context 整体 Dispose
        /// 时若尚未开始退出则<b>不会补调</b>——可靠的清理写进 <see cref="Bag"/>，这里只放“优雅告别”类工作如存档上报）。
        /// 本方法刻意没有取消 token：一旦开始，物理任务可迟到结束；但宿主释放会立即撤本状态子 Context 并取消
        /// 对应 GoTo，不会被忽略取消的退出任务卡住。迟到代码不得再依赖 Context / Bag；迟到异常仍由框架记录。
        /// 普通转换中的异常同样只记录、不阻断后续进入（离开失败不该把游戏卡死在旧阶段）。
        /// </summary>
        protected internal virtual UniTask OnExit() => UniTask.CompletedTask;

        // ── 框架内部：子 Context 装配 / 拆除（GameFlow 驱动） ─────────────────

        internal void AttachScope(GameContext scope) => _scope = scope;

        // 幂等：Enter 失败路径与 GameFlow.Dispose 可能先后各调一次。Bag 先于 Context 撤（bag 内订阅可能还要用 ctx）。
        internal void DisposeScope()
        {
            _bag?.Dispose();
            _bag = null;
            _scope?.Dispose();
            _scope = null;
        }
    }
}
