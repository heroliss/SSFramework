using Game.Framework.Model;
using R3;

namespace Game.Framework
{
    /// <summary>
    /// 配置表 Model 的统一访问面：持有整套配置表根对象与加载状态。
    ///
    /// <para>泛型按表根类型闭合（如代码生成器产出的 Tables 类），System / Command 经
    /// <c>GetModel&lt;IConfigModel&lt;TTables&gt;&gt;()</c> 取到后读 <see cref="Tables"/>；
    /// View 不直接访问 Model，由只读查询 Command 包装成 <c>ReadOnlyReactiveProperty</c> 返回。</para>
    ///
    /// <para>本接口与任何具体配置后端无关——框架只约定「表根对象 + 状态」，
    /// 表如何生成、何种格式反序列化由项目侧的初始化 System 子类决定。</para>
    /// </summary>
    /// <typeparam name="TTables">配置表根类型：加载完成后一次性构造的只读数据入口。</typeparam>
    public interface IConfigModel<TTables> : IModel where TTables : class
    {
        /// <summary>
        /// 配置表根实例。加载完成前为 null；完成后整体赋值一次（配置是只读数据，不做逐表增量更新）。
        /// </summary>
        RP<TTables> Tables { get; }

        /// <summary>
        /// 加载状态流。订阅即得当前值；等待配置可用时订阅它而不是轮询 <see cref="Tables"/> 是否为 null。
        /// </summary>
        RP<ConfigInitState> State { get; }
    }
}
