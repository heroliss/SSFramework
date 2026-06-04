using Game.Framework;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 资源加载章的「ScriptableObject 配置」示例：演示在 SO 里用 <see cref="AssetReference{T}"/> 当资源地址载体。
    ///
    /// 关键差异（也是本示例要讲的点）：SO 不是 MonoBehaviour，没有 MonoXxxBase 的 Awake 自动绑定，
    /// 所以它内部的 AssetReference 不会被自动 Bind——业务必须在用之前手动 <c>IconRef.Bind(utility, hostToken)</c>
    /// 再 <c>Get()</c>，并自行决定何时 <c>Unload()</c>（SO 是共享资产、长期存活，不像 Mono 那样随宿主销毁自动释放）。
    /// 对应 Assets/Game/AGENTS.md 规则 19：「ScriptableObject 或手动创建的 ref 需要调 ref.Bind(utility, hostToken)」。
    /// </summary>
    [CreateAssetMenu(fileName = "DemoAssetConfig", menuName = "SSFramework Demo/Demo Asset Config")]
    public sealed class DemoAssetConfig : ScriptableObject
    {
        [Tooltip("一张图标的资源引用（Inspector 拖入，内部存 GUID）。因为本类是 SO，这个引用不会自动绑定——需手动 Bind 后 Get。")]
        [SerializeField] private AssetReference<Sprite> _iconRef = new();

        /// <summary>SO 携带的图标引用。用前需 <c>Bind(utility, token)</c>，因为 SO 没有 Mono 的自动绑定。</summary>
        public AssetReference<Sprite> IconRef => _iconRef;
    }
}
