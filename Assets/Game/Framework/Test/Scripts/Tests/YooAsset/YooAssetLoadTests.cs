using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.Framework.Test
{
    /// <summary>
    /// YooAsset 资源加载测试。
    /// 覆盖：AssetUtility.Load 路径加载、AssetReference 缓存/并发/生命周期、AssetReferenceList 批量加载。
    /// 测试在内存中搭建一个最小 Context：Settings + Utility + InitSystem，等 init 完成后跑断言。
    /// </summary>
    public class YooAssetLoadTests
    {
        private YooAssetTestConfig _config;
        private GameObject _root;
        private MonoGameContextBase _context;
        private AssetUtility _utility;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _config = LoadTestConfig();
            if (_config == null)
                Assert.Fail("未找到 YooAssetTestConfig，请在 Test/Data/ 下创建配置文件");

            yield return BuildAssetEnvironment().ToCoroutine();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_config != null)
            {
                if (_config.PrefabReference != null) _config.PrefabReference.Dispose();
                _config.ImageList?.Dispose();
            }

            if (_root != null) Object.Destroy(_root);
            _root = null;
            _context = null;
            _utility = null;
            yield return null;
        }

        // ── 测试环境搭建 ──────────────────────────────────────────────

        /// <summary>
        /// 在内存中搭一棵最小的资源系统树：
        /// Root → Context → [Settings, Utility, InitSystem]。
        /// AddComponent 的顺序决定 Awake 顺序：Context 先，Settings/Utility 注册到容器，最后 InitSystem 触发 init pipeline。
        /// </summary>
        private async UniTask BuildAssetEnvironment()
        {
            _root = new GameObject("AssetTestRoot");

            var contextGo = new GameObject("Context");
            contextGo.transform.SetParent(_root.transform);
            _context = contextGo.AddComponent<MonoGameContextBase>();

            var settingsGo = new GameObject("Settings");
            settingsGo.transform.SetParent(contextGo.transform);
            var settings = settingsGo.AddComponent<AssetSettingsModel>();
            SetPrivateField(settings, "_defaultPackageName", "DefaultPackage");
            // 测试环境默认走 Editor 模拟模式；非编辑器跑测试时退到 Offline。
#if UNITY_EDITOR
            SetPrivateField(settings, "_playMode", AssetPlayMode.EditorSimulate);
#else
            SetPrivateField(settings, "_playMode", AssetPlayMode.Offline);
#endif

            var utilityGo = new GameObject("Utility");
            utilityGo.transform.SetParent(contextGo.transform);
            _utility = utilityGo.AddComponent<AssetUtility>();

            var systemGo = new GameObject("InitSystem");
            systemGo.transform.SetParent(contextGo.transform);
            systemGo.AddComponent<AssetInitSystem>();

            // 让所有 Awake 跑完（Unity 在当前帧调度）。
            await UniTask.Yield();
            await _utility.EnsureInitialized();

            // _config 是 ScriptableObject，AssetReference 字段不会被 MonoBase 自动 bind；手动绑定到本测试 utility。
            // hostToken=default：测试自己 TearDown 时 Dispose AssetReference，不依赖 token 取消。
            _config.PrefabReference?.Bind(_utility, default);
            if (_config.ImageList != null)
            {
                for (int i = 0; i < _config.ImageList.Count; i++)
                    _config.ImageList[i]?.Bind(_utility, default);
            }
        }

        // ── 路径加载测试 ──────────────────────────────────────────────

        /// <summary>通过 IAssetUtility.Load 加载资源应返回有效 handle。</summary>
        [UnityTest]
        public IEnumerator Utility_Load_ShouldReturnValidHandle()
        {
            return Utility_Load_ShouldReturnValidHandleAsync().ToCoroutine();
        }

        private async UniTask Utility_Load_ShouldReturnValidHandleAsync()
        {
            foreach (var path in _config.AssetPaths)
            {
                using var handle = await _utility.Load<GameObject>(path);
                Assert.IsNotNull(handle, $"加载失败: {path}");
                Assert.IsTrue(handle.IsValid, "handle 应有效");
                Assert.IsNotNull(handle.Asset, $"handle.Asset 为 null: {path}");
            }
        }

        /// <summary>加载不存在的路径应返回 null，不抛异常。</summary>
        [UnityTest]
        public IEnumerator Utility_Load_InvalidPath_ShouldReturnNull()
        {
            return Utility_Load_InvalidPath_ShouldReturnNullAsync().ToCoroutine();
        }

        private async UniTask Utility_Load_InvalidPath_ShouldReturnNullAsync()
        {
            LogAssert.Expect(LogType.Error, new Regex("Asset not found.*__NonExistentAsset__"));
            var handle = await _utility.Load<GameObject>("__NonExistentAsset__");
            Assert.IsNull(handle, "不存在的路径应返回 null");
        }

        // ── AssetReference 缓存测试 ───────────────────────────────────

        /// <summary>多次调用 Get 应返回同一缓存实例，不重复加载。</summary>
        [UnityTest]
        public IEnumerator AssetReference_MultipleGet_ShouldReturnCachedInstance()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) yield break;

            GameObject asset1 = null, asset2 = null, asset3 = null;
            yield return _config.PrefabReference.Get().ContinueWith(x => asset1 = x).ToCoroutine();
            yield return _config.PrefabReference.Get().ContinueWith(x => asset2 = x).ToCoroutine();
            yield return _config.PrefabReference.Get().ContinueWith(x => asset3 = x).ToCoroutine();

            Assert.IsNotNull(asset1, "第1次 Get 返回 null");
            Assert.IsNotNull(asset2, "第2次 Get 返回 null");
            Assert.IsNotNull(asset3, "第3次 Get 返回 null");

            Assert.AreSame(asset1, asset2, "第1次和第2次 Get 结果不是同一引用");
            Assert.AreSame(asset2, asset3, "第2次和第3次 Get 结果不是同一引用");

            Assert.IsTrue(_config.PrefabReference.IsLoaded, "多次 Get 后 IsLoaded 应为 true");
        }

        // ── AssetReference 并发测试 ───────────────────────────────────

        /// <summary>并发发起多个 Get 应共享同一加载任务，返回同一实例。</summary>
        [UnityTest]
        public IEnumerator AssetReference_ConcurrentGet_ShouldReturnSameInstance()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) yield break;

            _config.PrefabReference.Unload();

            GameObject asset1 = null, asset2 = null, asset3 = null;
            yield return UniTask.WhenAll(
                _config.PrefabReference.Get().ContinueWith(x => asset1 = x),
                _config.PrefabReference.Get().ContinueWith(x => asset2 = x),
                _config.PrefabReference.Get().ContinueWith(x => asset3 = x)
            ).ToCoroutine();

            Assert.IsNotNull(asset1, "并发 Get 结果1 为 null");
            Assert.IsNotNull(asset2, "并发 Get 结果2 为 null");
            Assert.IsNotNull(asset3, "并发 Get 结果3 为 null");

            Assert.AreSame(asset1, asset2, "并发 Get 结果1和2 不是同一引用");
            Assert.AreSame(asset2, asset3, "并发 Get 结果2和3 不是同一引用");

            Assert.IsTrue(_config.PrefabReference.IsLoaded, "并发 Get 完成后 IsLoaded 应为 true");
        }

        // ── AssetReference 生命周期测试 ───────────────────────────────

        /// <summary>Unload 后重新 Get 应能正常加载。</summary>
        [UnityTest]
        public IEnumerator AssetReference_UnloadThenReload_ShouldSucceed()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) yield break;

            yield return _config.PrefabReference.Get().ToCoroutine();
            Assert.IsTrue(_config.PrefabReference.IsLoaded, "首次加载后 IsLoaded 应为 true");

            _config.PrefabReference.Unload();
            Assert.IsFalse(_config.PrefabReference.IsLoaded, "Unload 后 IsLoaded 应为 false");
            Assert.IsNull(_config.PrefabReference.Asset, "Unload 后 Asset 应为 null");

            GameObject reloaded = null;
            yield return _config.PrefabReference.Get().ContinueWith(x => reloaded = x).ToCoroutine();

            Assert.IsNotNull(reloaded, "重新加载后 Get 返回 null");
            Assert.IsTrue(_config.PrefabReference.IsLoaded, "重新加载后 IsLoaded 应为 true");
        }

        /// <summary>Dispose 等效于 Unload。</summary>
        [UnityTest]
        public IEnumerator AssetReference_Dispose_ShouldUnload()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) yield break;

            yield return _config.PrefabReference.Get().ToCoroutine();
            Assert.IsTrue(_config.PrefabReference.IsLoaded);

            _config.PrefabReference.Dispose();
            Assert.IsFalse(_config.PrefabReference.IsLoaded);
            Assert.IsNull(_config.PrefabReference.Asset);
        }

        /// <summary>重复 Unload 不应抛异常。</summary>
        [Test]
        public void AssetReference_DoubleUnload_ShouldNotThrow()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) return;
            Assert.DoesNotThrow(() =>
            {
                _config.PrefabReference.Unload();
                _config.PrefabReference.Unload();
            });
        }

        [UnityTest]
        public IEnumerator SimulatedDelayMs_WhenConfigured_ShouldDelayTestLoad()
        {
            return SimulatedDelayMs_WhenConfigured_ShouldDelayTestLoadAsync().ToCoroutine();
        }

        /// <summary>重复 Dispose 不应抛异常。</summary>
        [Test]
        public void AssetReference_DoubleDispose_ShouldNotThrow()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) return;
            Assert.DoesNotThrow(() =>
            {
                _config.PrefabReference.Dispose();
                _config.PrefabReference.Dispose();
            });
        }

        /// <summary>Dispose 后再 Get 应能重新加载。</summary>
        [UnityTest]
        public IEnumerator AssetReference_GetAfterDispose_ShouldReload()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) yield break;

            yield return _config.PrefabReference.Get().ToCoroutine();
            Assert.IsTrue(_config.PrefabReference.IsLoaded);

            _config.PrefabReference.Dispose();
            Assert.IsFalse(_config.PrefabReference.IsLoaded);

            GameObject reloaded = null;
            yield return _config.PrefabReference.Get().ContinueWith(x => reloaded = x).ToCoroutine();
            Assert.IsNotNull(reloaded);
            Assert.IsTrue(_config.PrefabReference.IsLoaded);
        }

        // ── AssetReference 无效输入测试 ──────────────────────────────

        [UnityTest]
        public IEnumerator AssetReference_EmptyGUID_GetShouldReturnNull()
        {
            var invalidRef = new AssetReference<GameObject>();
            invalidRef.Bind(_utility, default); // 仍要 bind，否则会走 fallback 路径输出额外 error
            Assert.IsFalse(invalidRef.HasGuid, "空 GUID 的 HasGuid 应为 false");

            LogAssert.Expect(LogType.Warning, "[AssetReference] GUID is empty, assign asset in Inspector.");
            GameObject result = null;
            yield return invalidRef.Get().ContinueWith(x => result = x).ToCoroutine();
            Assert.IsNull(result);
        }

        // ── AssetReferenceList 批量加载测试 ──────────────────────────

        [UnityTest]
        public IEnumerator AssetReferenceList_GetAll_ShouldLoadAllItems()
        {
            if (_config.ImageList == null || _config.ImageList.Count == 0)
                Assert.Ignore("ImageList 未配置，跳过测试");

            IReadOnlyList<Sprite> assets = null;
            yield return _config.ImageList.GetAll().ContinueWith(x => assets = x).ToCoroutine();

            Assert.IsNotNull(assets);
            Assert.AreEqual(_config.ImageList.Count, assets.Count);
            for (int i = 0; i < assets.Count; i++) Assert.IsNotNull(assets[i], $"资源[{i}] 为 null");
        }

        [Test]
        public void AssetReferenceList_Indexer_ShouldReturnCorrectItem()
        {
            if (_config.ImageList == null || _config.ImageList.Count == 0)
                Assert.Ignore("ImageList 未配置，跳过测试");

            for (int i = 0; i < _config.ImageList.Count; i++)
                Assert.IsNotNull(_config.ImageList[i], $"索引 [{i}] 返回 null");
        }

        [UnityTest]
        public IEnumerator AssetReferenceList_UnloadAll_ShouldUnloadAllItems()
        {
            if (_config.ImageList == null || _config.ImageList.Count == 0)
                Assert.Ignore("ImageList 未配置，跳过测试");

            yield return _config.ImageList.GetAll().ToCoroutine();
            _config.ImageList.UnloadAll();

            for (int i = 0; i < _config.ImageList.Count; i++)
                Assert.IsFalse(_config.ImageList[i].IsLoaded, $"Item[{i}] UnloadAll 后 IsLoaded 应为 false");
        }

        // ── 工具方法 ─────────────────────────────────────────────────

        private bool IsReferenceValid<T>(AssetReference<T> reference, string name) where T : UnityEngine.Object
        {
            if (reference == null || !reference.HasGuid)
            {
                Assert.Ignore($"{name} 未配置或无效，跳过测试");
                return false;
            }
            return true;
        }

        private static void SetPrivateField<T>(AssetSettingsModel settings, string fieldName, T value)
        {
            var field = typeof(AssetSettingsModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"AssetSettingsModel field '{fieldName}' not found.");
            field.SetValue(settings, value);
        }

        private static YooAssetTestConfig LoadTestConfig()
        {
#if UNITY_EDITOR
            var guids = AssetDatabase.FindAssets("t:YooAssetTestConfig");
            if (guids == null || guids.Length == 0) return null;
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<YooAssetTestConfig>(path);
#else
            return null;
#endif
        }

        private async UniTask SimulatedDelayMs_WhenConfigured_ShouldDelayTestLoadAsync()
        {
            if (!IsReferenceValid(_config.PrefabReference, "PrefabReference")) return;

            int originalDelayMs = _config.SimulatedDelayMs;
            const int testDelayMs = 80;

            try
            {
                _config.PrefabReference.Unload();
                _config.SimulatedDelayMs = testDelayMs;

                var stopwatch = Stopwatch.StartNew();
                GameObject loaded = await LoadPrefabReferenceWithConfiguredDelay();
                stopwatch.Stop();

                Assert.IsNotNull(loaded, "SimulatedDelayMs should not prevent asset loading");
                Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, testDelayMs,
                    "SimulatedDelayMs did not delay the test load path");
            }
            finally
            {
                _config.SimulatedDelayMs = originalDelayMs;
            }
        }

        private async UniTask ApplyConfiguredDelay()
        {
            if (_config == null || _config.SimulatedDelayMs <= 0) return;
            await UniTask.Delay(_config.SimulatedDelayMs);
        }

        private async UniTask<GameObject> LoadPrefabReferenceWithConfiguredDelay()
        {
            await ApplyConfiguredDelay();
            return await _config.PrefabReference.Get();
        }
    }
}
