using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Framework.Config.Tests
{
    /// <summary>
    /// 通过真实 Mono Context、AssetUtility 与可控 Provider 验证配置就绪契约，不依赖 YooAsset 或 Luban。
    /// </summary>
    public sealed class ConfigUtilityTests
    {
        private const string Package = "ConfigUtilityTestPackage";

        private GameObject _root;
        private ConfigAssetProvider _provider;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 保留编译器生成的外层 IEnumerator。Unity Test Framework 在全量 EditMode → PlayMode 过渡时
            // 会反射它的状态字段做续跑；直接返回 UniTask 自定义 Enumerator 会让包内 PC 恢复器空引用。
            yield return SetUpAsync().ToCoroutine();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _provider = null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator EnsureReady_CalledBeforeStart_WaitsAndReturnsStableTables()
        {
            yield return EnsureReady_CalledBeforeStart_WaitsAndReturnsStableTablesAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator EnsureReady_Failure_RethrowsOriginalExceptionThroughLoggingSeam()
        {
            yield return EnsureReady_Failure_RethrowsOriginalExceptionThroughLoggingSeamAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator EnsureReady_CallerCancellationOnlyDetachesWaiter()
        {
            yield return EnsureReady_CallerCancellationOnlyDetachesWaiterAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator Destroy_CancelsSharedLoadAndWaiter()
        {
            yield return Destroy_CancelsSharedLoadAndWaiterAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator InvalidManifest_FailsBeforeAssetProviderWork()
        {
            yield return InvalidManifest_FailsBeforeAssetProviderWorkAsync().ToCoroutine();
        }

        private async UniTask SetUpAsync()
        {
            _root = new GameObject(nameof(ConfigUtilityTests));

            var contextObject = new GameObject("Context");
            contextObject.transform.SetParent(_root.transform);
            contextObject.AddComponent<MonoGameContextBase>();

            var assetObject = new GameObject("AssetUtility");
            assetObject.transform.SetParent(contextObject.transform);
            var asset = assetObject.AddComponent<AssetUtility>();

            _provider = new ConfigAssetProvider();
            asset.ReplaceProviderForTesting(_provider);
            asset.Configure(Package, new AssetProviderConfig(), AssetPlayMode.EditorSimulate);
            await asset.Initialize(Package);
        }

        private async UniTask EnsureReady_CalledBeforeStart_WaitsAndReturnsStableTablesAsync()
        {
            byte[] expectedBytes = { 1, 2, 3 };
            _provider.SetBytes("alpha", expectedBytes);
            var config = CreateConfig(new[] { "alpha" });

            Assert.AreEqual(ConfigInitState.Idle, config.State.CurrentValue);
            var waiting = config.EnsureReady();
            var tables = await waiting;

            Assert.AreEqual(ConfigInitState.Ready, config.State.CurrentValue);
            Assert.AreSame(tables, config.Tables);
            Assert.AreSame(expectedBytes, tables.Bytes);
            Assert.AreSame(tables, await config.EnsureReady(),
                "就绪后的调用应同步返回同一份只读表根，不能隐式重载");
            Assert.AreEqual(1, _provider.LoadBytesCalls);
        }

        private async UniTask EnsureReady_Failure_RethrowsOriginalExceptionThroughLoggingSeamAsync()
        {
            var expected = new InvalidOperationException("table-construction-failed");
            _provider.SetBytes("alpha", new byte[] { 1 });
            var config = CreateConfig(new[] { "alpha" }, expected);

            LogAssert.Expect(LogType.Error,
                new Regex(@"\[ConfigUtility\] Configuration service 'TestConfigUtility' failed to load"));
            LogAssert.Expect(LogType.Exception, new Regex("table-construction-failed"));
            var sink = new CapturingSink();
            Log.AddSink(sink);
            Exception actual;
            try
            {
                actual = await CaptureFailure(config.EnsureReady());
            }
            finally
            {
                Log.RemoveSink(sink);
            }

            Assert.AreSame(expected, actual);
            Assert.AreEqual(ConfigInitState.Failed, config.State.CurrentValue);
            Assert.IsNull(config.Tables);
            Assert.AreEqual(1, sink.Entries.Count);
            var entry = sink.Entries[0];
            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual("ConfigUtility", entry.Category);
            Assert.AreSame(expected, entry.Exception);
            Assert.AreSame(config, entry.Context);

            Assert.AreSame(expected, await CaptureFailure(config.EnsureReady()),
                "失败后才加入的调用者也应收到同一次加载的原始异常");
        }

        private async UniTask EnsureReady_CallerCancellationOnlyDetachesWaiterAsync()
        {
            var gate = _provider.PlanLoad(new byte[] { 7, 8, 9 });
            var config = CreateConfig(new[] { "alpha" });
            var shared = config.EnsureReady();
            await gate.Started.Task;

            using var caller = new CancellationTokenSource();
            var detached = config.EnsureReady(caller.Token);
            caller.Cancel();
            Assert.IsTrue(await IsCanceled(detached));
            Assert.AreEqual(ConfigInitState.Loading, config.State.CurrentValue);
            Assert.IsFalse(gate.OwnerToken.IsCancellationRequested,
                "短命调用者的 token 不得传给配置共享加载 owner");

            gate.Release.TrySetResult();
            var tables = await shared;
            Assert.AreEqual(ConfigInitState.Ready, config.State.CurrentValue);
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, tables.Bytes);
        }

        private async UniTask Destroy_CancelsSharedLoadAndWaiterAsync()
        {
            var gate = _provider.PlanLoad(new byte[] { 4, 5, 6 });
            var config = CreateConfig(new[] { "alpha" });
            var waiting = config.EnsureReady();
            await gate.Started.Task;

            UnityEngine.Object.Destroy(config.gameObject);

            Assert.IsTrue(await IsCanceled(waiting));
            Assert.IsTrue(gate.OwnerToken.IsCancellationRequested,
                "配置组件销毁必须取消它拥有的物理加载，而不只是让 waiter 离开");
        }

        private async UniTask InvalidManifest_FailsBeforeAssetProviderWorkAsync()
        {
            var config = CreateConfig(new[] { "duplicate", "duplicate" });
            LogAssert.Expect(LogType.Error,
                new Regex(@"\[ConfigUtility\] Configuration service 'TestConfigUtility' failed to load"));
            LogAssert.Expect(LogType.Exception, new Regex("duplicate location 'duplicate'"));

            var failure = await CaptureFailure(config.EnsureReady());

            StringAssert.Contains("duplicate location 'duplicate'", failure.Message);
            Assert.AreEqual(ConfigInitState.Failed, config.State.CurrentValue);
            Assert.AreEqual(0, _provider.LoadBytesCalls,
                "无效清单应在进入资源 Module 前失败，避免部分表已经产生 I/O 副作用");
        }

        private TestConfigUtility CreateConfig(IReadOnlyList<string> files, Exception createFailure = null)
        {
            var configObject = new GameObject("ConfigUtility");
            configObject.transform.SetParent(_root.transform.GetChild(0));
            var config = configObject.AddComponent<TestConfigUtility>();
            config.Configure(files, createFailure);
            return config;
        }

        private static async UniTask<Exception> CaptureFailure(UniTask<TestTables> task)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                return e;
            }

            Assert.Fail("Expected configuration loading to fail.");
            return null;
        }

        private static async UniTask<bool> IsCanceled(UniTask<TestTables> task)
        {
            try
            {
                await task;
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private sealed class TestTables
        {
            public readonly byte[] Bytes;
            public TestTables(byte[] bytes) => Bytes = bytes;
        }

        private sealed class TestConfigUtility : MonoConfigUtilityBase<TestTables>
        {
            private IReadOnlyList<string> _files;
            private Exception _createFailure;

            protected override IReadOnlyList<string> TableFiles => _files;

            public void Configure(IReadOnlyList<string> files, Exception createFailure)
            {
                _files = files;
                _createFailure = createFailure;
            }

            protected override TestTables CreateTables(Func<string, byte[]> getBytes)
            {
                if (_createFailure != null) throw _createFailure;
                return new TestTables(getBytes(_files[0]));
            }
        }

        private sealed class ConfigAssetProvider : IAssetProvider
        {
            internal sealed class LoadGate
            {
                public readonly byte[] Bytes;
                public readonly UniTaskCompletionSource Started = new();
                public readonly UniTaskCompletionSource Release = new();
                public CancellationToken OwnerToken;

                public LoadGate(byte[] bytes) => Bytes = bytes;
            }

            private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
            private readonly Queue<LoadGate> _plannedLoads = new();
            private readonly HashSet<string> _readyPackages = new(StringComparer.Ordinal);

            public int LoadBytesCalls { get; private set; }

#if UNITY_EDITOR
            public Func<bool> SimulateOffline { get; set; }
#endif

            public UniTask InitializeAsync(
                string packageName, AssetPlayMode mode, AssetProviderConfig config, CancellationToken ct)
            {
                _readyPackages.Add(packageName);
                return UniTask.CompletedTask;
            }

            public bool IsPackageReady(string packageName) => _readyPackages.Contains(packageName);

            public void SetBytes(string location, byte[] bytes) => _bytes[location] = bytes;

            public LoadGate PlanLoad(byte[] bytes)
            {
                var gate = new LoadGate(bytes);
                _plannedLoads.Enqueue(gate);
                return gate;
            }

            public async UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct)
            {
                LoadBytesCalls++;
                if (_plannedLoads.Count > 0)
                {
                    var gate = _plannedLoads.Dequeue();
                    gate.OwnerToken = ct;
                    gate.Started.TrySetResult();
                    await gate.Release.Task.AttachExternalCancellation(ct);
                    return gate.Bytes;
                }

                return _bytes.TryGetValue(location, out var bytes) ? bytes : null;
            }

            public UniTask<IAssetHandle<UnityEngine.Object>> LoadAssetAsync(
                string packageName, string locationOrGuid, bool byGuid, Type type, CancellationToken ct)
                => UniTask.FromResult<IAssetHandle<UnityEngine.Object>>(null);

            public UniTask<ISceneHandle> LoadSceneAsync(
                string packageName, string location, LoadSceneMode mode, bool suspendLoad, CancellationToken ct)
                => UniTask.FromResult<ISceneHandle>(null);

            public UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct)
                => UniTask.FromResult<string>(null);

            public bool CheckLocationValid(string packageName, string location) => _bytes.ContainsKey(location);
            public bool IsNeedDownload(string packageName, string location) => false;
            public string GetPackageVersion(string packageName) => _readyPackages.Contains(packageName) ? "test" : null;
            public IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags, int maxConcurrent, int retries) => null;
            public IAssetDownloader CreateAllDownloader(string packageName, int maxConcurrent, int retries) => null;
            public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations, int maxConcurrent, int retries) => null;
            public UniTask ClearCacheAsync(string packageName, AssetCacheClearMode mode, CancellationToken ct) => UniTask.CompletedTask;
            public UniTask ClearCacheByTagsAsync(string packageName, IReadOnlyList<string> tags, CancellationToken ct) => UniTask.CompletedTask;
            public UniTask ClearCacheByLocationsAsync(string packageName, IReadOnlyList<string> locations, CancellationToken ct) => UniTask.CompletedTask;
            public UniTask UnloadUnusedAssetsAsync(string packageName, CancellationToken ct) => UniTask.CompletedTask;
            public void Dispose() { }
        }
    }
}
