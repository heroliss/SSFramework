using Game.Framework;
using Game.Framework.Model;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 资源加载章的「资源引用配置」<see cref="MonoModelBase"/>：持有 Inspector 拖进来的 AssetReference——
    /// 这本质是一份配置，所以归 Model 层。演示 AssetReference 的「拖资源进 Inspector → Awake 自动绑定 → Get() 加载」零样板路径：
    /// 基类 Awake 会把下列 <see cref="AssetReference{T}"/> / <see cref="AssetReferenceList{T}"/> 字段
    /// 自动绑定到本 Context 的 <c>IAssetUtility</c> 并登记进本层的 Bag（销毁时统一释放 handle）。
    /// 挂在 DemoApp（demo Context 节点）下；<see cref="AssetLoadingModule"/> 经 FindFirstObjectByType 取这些引用。
    /// </summary>
    public sealed class DemoAssetRefs : MonoModelBase
    {
        [SerializeField] private AssetReference<Sprite> _logoRef = new();
        [SerializeField] private AssetReferenceList<Sprite> _logoList = new();

        /// <summary>单个 Sprite 引用（Inspector 拖入一张 Logo）。</summary>
        public AssetReference<Sprite> LogoRef => _logoRef;

        /// <summary>Sprite 引用列表（Inspector 拖入多张图，演示 GetAll 并行批量加载）。</summary>
        public AssetReferenceList<Sprite> LogoList => _logoList;
    }
}
