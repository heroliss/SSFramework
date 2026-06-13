using Game.Framework.Model;
using R3;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// 配置表 Model 基类：加载完成后持有整套配置表实例，镜像资源系统三段式里
    /// <see cref="AssetSystemConfigModel"/> 的「数据归 Model」定位。
    ///
    /// <para>项目侧用具体表根类型闭合泛型即可成为可挂场景的组件：
    /// <c>public sealed class MyConfigModel : MonoConfigModelBase&lt;Tables&gt; { }</c>。
    /// Awake 时自动注册 <c>IConfigModel&lt;TTables&gt;</c>（MonoModelBase 的接口多重注册），
    /// 业务经该接口取表，不依赖具体子类类型。</para>
    /// </summary>
    /// <typeparam name="TTables">配置表根类型。</typeparam>
    public abstract class MonoConfigModelBase<TTables> : MonoModelBase, IConfigModel<TTables> where TTables : class
    {
        // Tables 实例不是 Unity 可序列化类型，不标 SerializeField（Inspector 观察加载结果看 State 即可）。
        public RP<TTables> Tables { get; } = new(null);

        [field: SerializeField, Tooltip("配置表加载状态（由配置初始化 System 写入）。")]
        public RP<ConfigInitState> State { get; private set; } = new(ConfigInitState.Idle);
    }
}
