using Game.Framework.Utility;
using R3;

namespace Game.Framework
{
    /// <summary>
    /// 配置表服务：持有整套生成的表根 + 加载状态，作为基础设施 Utility 供各层（含 View）只读取用。
    ///
    /// <para>配置是静态只读引用数据，生成的 <c>Tables</c> / <c>TbXxx</c> 本身就是数据模型——框架不再为它套一层
    /// Model，而是当「提供数据的服务」放 Utility 层：各层经 <c>this.GetUtility&lt;IConfigUtility&lt;TTables&gt;&gt;()</c>
    /// 或 <c>[Inject] IConfigUtility&lt;TTables&gt;</c> 直读，无需查询 Command 绕行（View 也有 <c>ICanGetUtility</c>）。</para>
    ///
    /// <para>后端无关——框架只约定「表根 + 状态」，表如何生成 / 何种格式反序列化由项目侧子类决定。</para>
    /// </summary>
    /// <typeparam name="TTables">配置表根类型：加载完成后一次性构造的只读数据入口。</typeparam>
    public interface IConfigUtility<TTables> : IUtility where TTables : class
    {
        /// <summary>
        /// 配置表根实例。加载完成前为 <c>null</c>，<see cref="State"/> 到 <see cref="ConfigInitState.Ready"/> 后可用。
        /// 配置是一次性加载的只读数据、之后不变，所以这里是**普通取值**而非响应流（读法 <c>config.Tables.TbItem.Get(id)</c>，
        /// 无需 <c>.CurrentValue</c>）——要"等就绪"请订阅 <see cref="State"/>，不要订阅 / 轮询本属性。
        /// </summary>
        TTables Tables { get; }

        /// <summary>加载状态流。订阅即得当前值；等配置可用时订阅它（收到 <see cref="ConfigInitState.Ready"/> 再读 <see cref="Tables"/>）。</summary>
        ReadOnlyReactiveProperty<ConfigInitState> State { get; }
    }
}
