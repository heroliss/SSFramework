using Game.Framework;
using Game.Framework.Utility;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 资源引用章的场景侧 Adapter：持有 Inspector 拖入的 AssetReference，并把 Unity 序列化配置接入当前 Context。
    /// 它没有金币、库存等业务状态，因此不是 Model；需要 Inspector 配置与 Unity 生命周期，所以使用 <see cref="MonoUtilityBase"/>。
    /// 演示 AssetReference 的「拖资源进 Inspector → Awake 自动绑定 → Get() 加载」零样板路径：
    /// 基类 Awake 会把下列 <see cref="AssetReference{T}"/> / <see cref="AssetReferenceList{T}"/> 字段
    /// 自动绑定到本 Context 的 <c>IAssetUtility</c> 并登记进本层的 Bag（销毁时统一释放 handle）。
    /// 挂在 demo Context 节点下；<see cref="AssetReferenceModule"/> 只从自己的 Context 解析它，避免多场景或多 Context 时全局扫描到错误实例。
    /// </summary>
    // MonoUtilityBase 默认与 AssetUtility 同为 -400；引用绑定只在 Awake 做一次，因此这里显式晚一档，
    // 保证同 Context 的资源入口已经注册。不要依赖 Hierarchy 顺序或同 execution order 的偶然调用顺序。
    [DefaultExecutionOrder(-350)]
    public sealed class DemoAssetRefs : MonoUtilityBase
    {
        [SerializeField] private AssetReference<Sprite> _logoRef = new();
        [SerializeField] private AssetReferenceList<Sprite> _logoList = new();

        /// <summary>单个 Sprite 引用（Inspector 拖入一张 Logo）。</summary>
        public AssetReference<Sprite> LogoRef => _logoRef;

        /// <summary>Sprite 引用列表（Inspector 拖入多张图，演示 GetAll 并行批量加载）。</summary>
        public AssetReferenceList<Sprite> LogoList => _logoList;
    }
}
