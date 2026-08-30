using Game.Framework;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 资源引用章的「ScriptableObject 配置」示例：演示在 SO 里用 <see cref="AssetReference{T}"/> 当资源地址载体。
    ///
    /// 关键差异（也是本示例要讲的点）：SO 不是 MonoBehaviour，没有 MonoXxxBase 的 Awake 自动绑定，
    /// 所以它内部的 AssetReference 不会被自动绑定。持有者应在使用前调用
    /// <c>Bag.BindAssetReferences(config)</c>，让内部引用与持有者的生命周期一起释放，再调用 <c>Get()</c>。
    /// SO 是被加载的数据资产，不等于 Model 层；谁加载并持有它，谁建立并拥有引用生命周期。
    /// 同一个嵌套引用实例只能有一个生命周期 owner；多个 Context 并行使用时应各自 clone 配置，
    /// 或由一个明确的长寿命 owner 独占，不能把同一 SO 反复绑定到多个 Bag。
    /// </summary>
    [CreateAssetMenu(fileName = "DemoAssetConfig", menuName = "SSFramework 演示/资源引用配置（DemoAssetConfig）")]
    public sealed class DemoAssetConfig : ScriptableObject
    {
        [Tooltip("一张图标的资源引用（Inspector 拖入，内部存 GUID）。SO 不会自动绑定；由持有者先用 Bag.BindAssetReferences 建立生命周期，再 Get。")]
        [SerializeField] private AssetReference<Sprite> _iconRef = new();

        /// <summary>
        /// SO 携带的图标引用。用前由持有者调用 <c>Bag.BindAssetReferences(config)</c>，因为 SO 没有 Mono 的自动绑定；
        /// 同一引用实例只能归一个生命周期 owner。
        /// </summary>
        public AssetReference<Sprite> IconRef => _iconRef;
    }
}
