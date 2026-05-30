using UnityEngine;

namespace Game.Framework.Test
{
    [CreateAssetMenu(fileName = "YooAssetTestConfig", menuName = "Framework/Test/YooAsset Test Config")]
    public class YooAssetTestConfig : ScriptableObject
    {
        [Header("路径加载测试数据")]
        [Tooltip("要通过 YooAssets.LoadAssetAsync 加载的资源路径列表")]
        public string[] AssetPaths = { "LittleHouse" };

        [Header("AssetReference 测试数据")]
        [Tooltip("用于测试多次 Get / 并发 Get / 生命周期的 GameObject 引用")]
        public AssetReference<GameObject> PrefabReference;

        [Tooltip("用于测试 AssetReferenceList 批量加载（可拖入多个 Sprite）")]
        public AssetReferenceList<Sprite> ImageList;

        [Header("模拟延迟配置")]
        [Tooltip("模拟加载前延迟（毫秒），0 表示不延迟")]
        public int SimulatedDelayMs;
    }
}
