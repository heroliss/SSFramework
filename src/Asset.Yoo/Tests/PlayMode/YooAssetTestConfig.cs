using UnityEngine;

namespace Game.Framework.Test
{
    /// <summary>只供 EditorSimulate 测试使用的临时资源所有权桥；不进入正常玩家程序集。</summary>
    public interface IYooAssetPlayModeFixture : System.IDisposable
    {
        string PackageName { get; }
        YooAssetTestConfig Config { get; }
    }

    /// <summary>由测试夹具在内存中创建，不要求消费工程提供配置资产。</summary>
    public class YooAssetTestConfig : ScriptableObject
    {
        [Header("路径加载测试数据")]
        [Tooltip("要通过 YooAssets.LoadAssetAsync 加载的资源路径列表")]
        public string[] AssetPaths = System.Array.Empty<string>();

        [Header("AssetReference 测试数据")]
        [Tooltip("用于测试多次 Get / 并发 Get / 生命周期的 GameObject 引用")]
        public AssetReference<GameObject> PrefabReference;

        [Tooltip("用于测试 AssetReferenceList 批量加载（可拖入多个 Sprite）")]
        public AssetReferenceList<Sprite> ImageList;
    }
}
