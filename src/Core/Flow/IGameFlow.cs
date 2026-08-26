using Cysharp.Threading.Tasks;
using Game.Framework.Event;
using Game.Framework.Utility;

namespace Game.Framework.Flow
{
    /// <summary>
    /// 游戏流程状态机。把「游戏宏观阶段」（启动 / 登录 / 大厅 / 战斗……）显式化为 <see cref="FlowState"/> 子类，
    /// 每个状态进入时获得一个以宿主 Context 为父级的<b>子 Context</b>，退出时整棵 Dispose——
    /// 阶段私有的服务 / 订阅 / 资源随阶段结束自动撤干净，切阶段漏清理被结构性消灭。ADR-0023。
    /// </summary>
    /// <remarks>
    /// <b>注册：</b><c>builder.RegisterOwned(new GameFlow(), typeof(IGameFlow))</c>——注册即注入（ADR-0019）
    /// 自动回填宿主 Context；宿主 Context Dispose 时 flow 连同当前状态子 Context 一并撤。<br/>
    /// <b>转换语义：</b>全程串行（退旧 → 撤旧子 Context → 建新子 Context → 进新）；转换进行中再调
    /// <see cref="GoTo"/> = <b>最新意图胜</b>——排队槽只有一格、新请求顶替旧排队，在途 <c>OnEnter</c> 被协作取消。
    /// 被顶替 / 取消的 GoTo 返回的 task 以取消结束。<br/>
    /// <b>失败语义：</b><c>OnEnter</c> 抛异常 = 子 Context 立即撤、<see cref="Current"/> 为 null、
    /// 异常从 GoTo 的 task 冒出（调用方决定重试 / 进错误状态，框架不猜）。<br/>
    /// <b>不做的事：</b>转换表 / 守卫（哪些转换允许是业务 if 的事）、场景绑定（状态在 OnEnter 里自己
    /// <c>Bag.LoadScene</c>）、历史栈（UI 返回栈已归 UI 框架管）。战斗内的子阶段机 = 在状态的
    /// <c>InstallBindings</c> 里再注册一个 <see cref="GameFlow"/>，作用域树天然嵌套。<br/>
    /// <b>线程：</b>主线程独占（框架统一契约）。
    /// </remarks>
    public interface IGameFlow : IUtility
    {
        /// <summary>当前<b>完整进入</b>的状态。未启动 / 转换进行中 / 上次 Enter 失败时为 null。</summary>
        FlowState Current { get; }

        /// <summary>是否有转换正在进行（含排队待处理）。</summary>
        bool IsTransitioning { get; }

        /// <summary>当前状态是否为 <typeparamref name="TState"/>（或其子类）。转换进行中返回 false。</summary>
        bool IsIn<TState>() where TState : FlowState;

        /// <summary>
        /// 转换到新状态。<paramref name="next"/> 是<b>一次性实例</b>：每次转换 new 一个新对象
        /// （传参走构造函数；重进同类状态 = new 同类新实例），复用已消费的实例抛参数异常。
        /// 返回的 task 在该次转换完成时结束；被更新的 GoTo 顶替 / 宿主 Dispose 时以取消结束；
        /// <c>OnEnter</c> 失败时携带其异常。调用方必须 <c>await</c> 或显式观察三种结局；
        /// “不关心何时完成”不等于可以丢弃 faulted UniTask。<c>OnEnter</c> 内转向不能 await（会互等），
        /// 应交给项目的导航边界捕获取消并记录其它异常，随后直接 return。
        /// </summary>
        UniTask GoTo(FlowState next);
    }

    /// <summary>
    /// 流程状态切换完成事件，在宿主 Context 上发送（转换<b>成功</b>后）。
    /// loading 界面 / 埋点订阅这一个事件即可，不必侵入每个状态。连续转换中被取消 / 顶替 / 进入失败、
    /// 从未成为 <see cref="IGameFlow.Current"/> 的中间状态不会出现在事件链里。
    /// </summary>
    public readonly struct FlowChangedEvent : IEvent
    {
        /// <summary>
        /// 本轮连续转换开始前最后一个完整进入的状态；首次进入，或流程此前已明确进入无状态时为 null。
        /// 例如 A →（B 进入中被顶替）→ C 只发布 A → C。
        /// </summary>
        public readonly FlowState From;

        /// <summary>切换后的状态（即此刻的 <see cref="IGameFlow.Current"/>）。</summary>
        public readonly FlowState To;

        public FlowChangedEvent(FlowState from, FlowState to)
        {
            From = from;
            To = to;
        }
    }
}
