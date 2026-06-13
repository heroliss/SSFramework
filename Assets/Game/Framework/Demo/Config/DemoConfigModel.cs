using DemoCfg;
using Game.Framework;

namespace Game.Framework.Demo.Config
{
    /// <summary>
    /// Demo 的配置表 Model：用生成的表根 <see cref="Tables"/> 闭合框架泛型基类，即成可挂场景的组件。
    /// 业务侧不引用本类型，统一经 <c>GetModel&lt;IConfigModel&lt;Tables&gt;&gt;()</c> 取（Awake 自动按接口注册）。
    /// </summary>
    public sealed class DemoConfigModel : MonoConfigModelBase<Tables>
    {
    }
}
