using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 场景持有者：存「View 章」运行时要实例化的 UGUI View prefab。
    /// 反射创建的 <see cref="UGuiViewModule"/> 拿不到 Inspector 引用，靠 <c>FindFirstObjectByType</c> 找到这里取 prefab。
    /// 挂在 DemoApp（带 MonoDemoContext）下，实例化时以本节点为父，让 prefab 里的 MonoViewBase 沿父链自动找到 Context。
    /// </summary>
    public sealed class DemoUGuiAssets : MonoBehaviour
    {
        [SerializeField] private GameObject _viewPrefab;
        public GameObject ViewPrefab => _viewPrefab;
    }
}
