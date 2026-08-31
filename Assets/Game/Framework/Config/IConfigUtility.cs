using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Utility;
using R3;

namespace Game.Framework
{
    /// <summary>
    /// 配置表服务：持有整套生成的表根 + 加载状态，作为基础设施 Utility 供各层（含 View）只读取用。
    ///
    /// <para>配置是静态只读引用数据，生成的 <c>Tables</c> / <c>TbXxx</c> 本身就是数据模型——框架不再为它套一层
    /// Model，而是当「提供数据的服务」放 Utility 层：已知就绪时各层经
    /// <c>this.GetConfig&lt;TTables&gt;()</c> 直读，流程门禁经 <c>await this.EnsureConfig&lt;TTables&gt;(token)</c>；
    /// 只有需要订阅 <see cref="State"/> 或注入服务时才直接获取本 Interface。无需查询 Command 绕行（View 也有 <c>ICanGetUtility</c>）。</para>
    ///
    /// <para>后端无关——框架只约定「表根 + 就绪契约」，表如何生成 / 何种格式反序列化由项目侧子类决定。</para>
    /// <para>通常继承 <see cref="MonoConfigUtilityBase{TTables}"/> 获得完整实现；直接实现本 Interface 的自定义服务必须同时保持
    /// <see cref="State"/>、<see cref="Tables"/> 与 <see cref="EnsureReady"/> 的发布顺序、失败和取消语义。</para>
    /// <para><b>线程：</b>本服务由 Unity 主线程独占；<see cref="EnsureReady"/> 从主线程调用，并保证调用方 token 即使
    /// 在 worker 取消，成功、异常或取消也回到 Unity 主线程交付。自定义实现必须保持同一契约。</para>
    /// </summary>
    /// <typeparam name="TTables">配置表根类型：加载完成后一次性构造的只读数据入口。</typeparam>
    public interface IConfigUtility<TTables> : IUtility where TTables : class
    {
        /// <summary>
        /// 配置表根实例。加载完成前为 <c>null</c>，<see cref="State"/> 到 <see cref="ConfigInitState.Ready"/> 后可用。
        /// 配置是一次性加载的只读数据、之后不变，所以这里是**普通取值**而非响应流（常用读法 <c>this.GetConfig&lt;Tables&gt;().TbItem[id]</c>，
        /// 无需 <c>.CurrentValue</c>）——响应式界面订阅 <see cref="State"/>，命令式启动流程等待 <see cref="EnsureReady"/>；
        /// 不要轮询本属性。
        /// </summary>
        TTables Tables { get; }

        /// <summary>
        /// 加载状态流。订阅即得当前值；适合驱动加载提示、禁用态与失败提示等响应式界面。
        /// 收到 <see cref="ConfigInitState.Ready"/> 时 <see cref="Tables"/> 已可用；配置组件销毁时该流完结。
        /// </summary>
        ReadOnlyReactiveProperty<ConfigInitState> State { get; }

        /// <summary>
        /// 等待当前配置服务的单次自加载完成并返回表根。已经就绪时同步完成；加载失败时重新抛出该次加载的原始异常。
        /// </summary>
        /// <remarks>
        /// <paramref name="cancellationToken"/> 只取消当前调用者的等待，不会停止由组件与 Context 生命周期共同拥有的共享加载；
        /// 其他调用者仍可继续等待同一次结果。组件或 Context 销毁会取消共享加载及尚未完成的等待。
        /// 活跃且启用的组件可在 Unity 调用 <c>Start</c> 前先收到本调用并等待同一次加载；组件仍为 Idle 且已禁用或
        /// GameObject 未激活时会立即失败，因为 Unity 不会为该组件调用 <c>Start</c>。
        /// 只有 owner token 已取消时，下游 <see cref="System.OperationCanceledException"/> 才按生命周期取消发布；
        /// Provider / Adapter 在 owner 未取消时自发抛出的取消异常会包装为 <see cref="System.InvalidOperationException"/> 并发布 Failed。
        /// 本服务是一次性自加载，不在失败后隐式重试；需要重试时应重建其所属 Context / 组件，避免一部分调用者看到旧表、另一部分看到新表。
        /// </remarks>
        /// <exception cref="System.OperationCanceledException">调用者取消等待，或拥有该加载的组件 / Context 已销毁。</exception>
        /// <exception cref="System.InvalidOperationException">服务仍为 Idle 但组件不能收到 Start，或下游在 owner 未取消时自发取消。</exception>
        /// <exception cref="System.Exception">清单、资源加载或表根构造失败时，保留并重新抛出原始异常；非 owner 取消会保留为内部异常。</exception>
        UniTask<TTables> EnsureReady(CancellationToken cancellationToken = default);
    }
}
