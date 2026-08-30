using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Logging;
using NUnit.Framework;
using R3;
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
        public IEnumerator EnsureReady_DisabledWhileIdle_FailsFastWithoutPoisoningLaterStart()
        {
            yield return EnsureReady_DisabledWhileIdle_FailsFastWithoutPoisoningLaterStartAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator EnsureReady_InactiveWhileIdle_FailsFastWithoutPoisoningLaterStart()
        {
            yield return EnsureReady_InactiveWhileIdle_FailsFastWithoutPoisoningLaterStartAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator EnsureReady_ProviderCancelsWithoutOwnerIntent_PublishesWrappedFailure()
        {
            yield return EnsureReady_ProviderCancelsWithoutOwnerIntent_PublishesWrappedFailureAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator Destroy_CancelsSharedLoadAndWaiter()
        {
            yield return Destroy_CancelsSharedLoadAndWaiterAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator Destroy_WhenOwnerCancellationCallbackThrows_StillFinishesLifecycle()
        {
            yield return Destroy_WhenOwnerCancellationCallbackThrows_StillFinishesLifecycleAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator InvalidManifest_FailsBeforeAssetProviderWork()
        {
            yield return InvalidManifest_FailsBeforeAssetProviderWorkAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator ConfigAccessExtensions_PreserveContextAndStableTables()
        {
            yield return ConfigAccessExtensions_PreserveContextAndStableTablesAsync().ToCoroutine();
        }

        [Test]
        public void GetConfig_BeforeReady_FailsFastWithActionableMessage()
        {
            var config = CreateConfig(new[] { "alpha" });
            config.enabled = false; // 本用例只验证 Idle 同步读取；不要让下一帧 Start 把它推进真实加载流程。

            var error = Assert.Throws<InvalidOperationException>(() => config.GetConfig<TestTables>());

            StringAssert.Contains(typeof(TestTables).FullName, error.Message);
            StringAssert.Contains("当前状态：Idle", error.Message);
            StringAssert.Contains("EnsureConfig<TestTables>", error.Message);
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
                new Regex(@"\[ConfigUtility\] 配置服务.TestConfigUtility.加载表根失败"));
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
            Assert.AreEqual("配置服务“TestConfigUtility”加载表根失败。", entry.Message);
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

        private async UniTask EnsureReady_DisabledWhileIdle_FailsFastWithoutPoisoningLaterStartAsync()
        {
            byte[] expectedBytes = { 3, 1, 4 };
            _provider.SetBytes("alpha", expectedBytes);
            var config = CreateConfig(new[] { "alpha" });
            config.enabled = false;

            var failure = await CaptureFailure(config.EnsureReady());

            Assert.IsInstanceOf<InvalidOperationException>(failure);
            StringAssert.Contains("TestConfigUtility", failure.Message);
            StringAssert.Contains("disabled", failure.Message);
            StringAssert.Contains("启用组件", failure.Message);
            Assert.AreEqual(ConfigInitState.Idle, config.State.CurrentValue,
                "对尚未启动的 disabled 服务做无效等待，不应把一次可修复的场景配置问题发布成终态失败");
            Assert.AreEqual(0, _provider.LoadBytesCalls);

            config.enabled = true;
            var tables = await config.EnsureReady();

            Assert.AreEqual(ConfigInitState.Ready, config.State.CurrentValue);
            Assert.AreSame(expectedBytes, tables.Bytes);
            Assert.AreEqual(1, _provider.LoadBytesCalls,
                "重新启用后 Unity Start 应正常拥有第一次加载，先前的 fail-fast 不能毒化 completion");
        }

        private async UniTask EnsureReady_InactiveWhileIdle_FailsFastWithoutPoisoningLaterStartAsync()
        {
            byte[] expectedBytes = { 1, 6, 1, 8 };
            _provider.SetBytes("alpha", expectedBytes);
            var config = CreateConfig(new[] { "alpha" });
            config.gameObject.SetActive(false);

            var failure = await CaptureFailure(config.EnsureReady());

            Assert.IsInstanceOf<InvalidOperationException>(failure);
            StringAssert.Contains("TestConfigUtility", failure.Message);
            StringAssert.Contains("inactive", failure.Message);
            StringAssert.Contains("激活 GameObject", failure.Message);
            Assert.AreEqual(ConfigInitState.Idle, config.State.CurrentValue);
            Assert.AreEqual(0, _provider.LoadBytesCalls);

            config.gameObject.SetActive(true);
            var tables = await config.EnsureReady();

            Assert.AreEqual(ConfigInitState.Ready, config.State.CurrentValue);
            Assert.AreSame(expectedBytes, tables.Bytes);
            Assert.AreEqual(1, _provider.LoadBytesCalls,
                "重新激活后 Unity Start 应正常拥有第一次加载，先前的 fail-fast 不能毒化 completion");
        }

        private async UniTask EnsureReady_ProviderCancelsWithoutOwnerIntent_PublishesWrappedFailureAsync()
        {
            var providerCancellation = new OperationCanceledException("provider-canceled-without-owner-intent");
            _provider.FailNextLoad(providerCancellation);
            var config = CreateConfig(new[] { "alpha" });

            LogAssert.Expect(LogType.Error,
                new Regex(@"\[ConfigUtility\] 配置服务.TestConfigUtility.加载表根失败"));
            // UniTask.WhenAll 会把下游 OCE 归一化为取消终态；日志仍应保留异常类型与调用栈，
            // 但不把 Provider 自定义 message / 对象 identity 当成公共契约。
            LogAssert.Expect(LogType.Exception, new Regex("OperationCanceledException"));
            var failure = await CaptureFailure(config.EnsureReady());

            Assert.IsInstanceOf<InvalidOperationException>(failure,
                "owner 未请求取消时，Provider 自发 OCE 是适配器失败，不能伪装成生命周期控制流");
            Assert.IsInstanceOf<OperationCanceledException>(failure.InnerException);
            StringAssert.Contains("owner token 未请求取消", failure.Message);
            Assert.AreEqual(ConfigInitState.Failed, config.State.CurrentValue);
            Assert.IsFalse(_root.transform.GetChild(0).GetComponent<MonoGameContextBase>()
                .CancellationToken.IsCancellationRequested);
            Assert.AreSame(failure, await CaptureFailure(config.EnsureReady()),
                "稍后加入的 waiter 应收到同一个已包装根因，而不是错误的取消终态");
        }

        private async UniTask Destroy_CancelsSharedLoadAndWaiterAsync()
        {
            var gate = _provider.PlanLoad(new byte[] { 4, 5, 6 });
            var config = CreateConfig(new[] { "alpha" });
            bool stateCompleted = false;
            using var stateSubscription = config.State.Subscribe(
                _ => { },
                _ => stateCompleted = true);
            var waiting = config.EnsureReady();
            await gate.Started.Task;

            UnityEngine.Object.Destroy(config.gameObject);

            Assert.IsTrue(await IsCanceled(waiting));
            // CTS 取消可在 OnDestroy 内同步恢复本 continuation；让出一帧再观察整个 Mono 终态。
            await UniTask.Yield(PlayerLoopTiming.Update);
            Assert.IsTrue(gate.OwnerToken.IsCancellationRequested,
                "配置组件销毁必须取消它拥有的物理加载，而不只是让 waiter 离开");
            Assert.IsTrue(stateCompleted,
                "配置服务销毁时必须完结 State 源，不能让订阅继续持有已销毁的 Mono 组件");
        }

        private async UniTask Destroy_WhenOwnerCancellationCallbackThrows_StillFinishesLifecycleAsync()
        {
            var gate = _provider.PlanLoad(new byte[] { 9 });
            var config = CreateConfig(new[] { "alpha" });
            var context = _root.transform.GetChild(0).GetComponent<MonoGameContextBase>();
            bool stateCompleted = false;
            using var stateSubscription = config.State.Subscribe(
                _ => { },
                _ => stateCompleted = true);
            var waiting = config.EnsureReady();
            await gate.Started.Task;
            using var failingCancellationCallback = gate.OwnerToken.Register(
                () => throw new InvalidOperationException("config-owner-cancellation-probe"));

            LogAssert.Expect(LogType.Error,
                new Regex(@"\[ConfigUtility\].*配置共享加载的取消回调执行失败"));
            LogAssert.Expect(LogType.Exception, new Regex("config-owner-cancellation-probe"));
            UnityEngine.Object.Destroy(config.gameObject);

            Assert.IsTrue(await IsCanceled(waiting));
            await UniTask.Yield(PlayerLoopTiming.Update);
            Assert.IsTrue(stateCompleted, "坏取消回调不能截断 State 完结");
            Assert.IsFalse(context.RawContext.TryResolve(typeof(IConfigUtility<TestTables>), out _),
                "坏取消回调不能跳过 MonoUtilityBase 的 Context 反注册");
        }

        private async UniTask InvalidManifest_FailsBeforeAssetProviderWorkAsync()
        {
            var config = CreateConfig(new[] { "duplicate", "duplicate" });
            LogAssert.Expect(LogType.Error,
                new Regex(@"\[ConfigUtility\] 配置服务.TestConfigUtility.加载表根失败"));
            LogAssert.Expect(LogType.Exception, new Regex("包含重复资源地址.duplicate"));

            var failure = await CaptureFailure(config.EnsureReady());

            StringAssert.Contains("包含重复资源地址“duplicate”", failure.Message);
            Assert.AreEqual(ConfigInitState.Failed, config.State.CurrentValue);
            Assert.AreEqual(0, _provider.LoadBytesCalls,
                "无效清单应在进入资源 Module 前失败，避免部分表已经产生 I/O 副作用");
        }

        private async UniTask ConfigAccessExtensions_PreserveContextAndStableTablesAsync()
        {
            byte[] expectedBytes = { 6, 2, 8 };
            byte[] childBytes = { 1, 9, 9 };
            _provider.SetBytes("alpha", expectedBytes);
            _provider.SetBytes("child", childBytes);
            var config = CreateConfig(new[] { "alpha" });

            var ensuredFromLayer = await config.EnsureConfig<TestTables>();
            Assert.AreSame(ensuredFromLayer, config.GetConfig<TestTables>());

            var contextComponent = _root.transform.GetChild(0).GetComponent<MonoGameContextBase>();
            ICommandContext commandContext = contextComponent.RawContext;
            Assert.AreSame(ensuredFromLayer, await commandContext.EnsureConfig<TestTables>());
            Assert.AreSame(ensuredFromLayer, commandContext.GetConfig<TestTables>());
            Assert.AreSame(expectedBytes, ensuredFromLayer.Bytes);
            Assert.AreEqual(1, _provider.LoadBytesCalls,
                "快捷入口必须复用当前 Context 中的稳定配置实例，不能触发第二次加载");

            var childContextObject = new GameObject("ChildContext");
            childContextObject.transform.SetParent(contextComponent.transform);
            var childContext = childContextObject.AddComponent<MonoGameContextBase>();
            var childConfig = CreateConfig(childContextObject.transform, new[] { "child" });

            var ensuredFromChild = await childConfig.EnsureConfig<TestTables>();
            ICommandContext childCommandContext = childContext.RawContext;
            Assert.AreSame(ensuredFromChild, childConfig.GetConfig<TestTables>());
            Assert.AreSame(ensuredFromChild, childCommandContext.GetConfig<TestTables>());
            Assert.AreSame(childBytes, ensuredFromChild.Bytes);
            Assert.AreNotSame(ensuredFromLayer, ensuredFromChild,
                "子 Context 的同 contract 配置必须覆盖父 Context，快捷入口不能退化为全局 current Tables");
            Assert.AreSame(ensuredFromLayer, commandContext.GetConfig<TestTables>(),
                "子 Context 注册不得污染父 Context 的稳定表根");
            Assert.AreEqual(2, _provider.LoadBytesCalls);
        }

        private TestConfigUtility CreateConfig(IReadOnlyList<string> files, Exception createFailure = null)
            => CreateConfig(_root.transform.GetChild(0), files, createFailure);

        private static TestConfigUtility CreateConfig(
            Transform context,
            IReadOnlyList<string> files,
            Exception createFailure = null)
        {
            var configObject = new GameObject("ConfigUtility");
            configObject.transform.SetParent(context);
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
            private Exception _nextLoadFailure;

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

            public void FailNextLoad(Exception failure) =>
                _nextLoadFailure = failure ?? throw new ArgumentNullException(nameof(failure));

            public async UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct)
            {
                LoadBytesCalls++;
                if (_nextLoadFailure != null)
                {
                    var failure = _nextLoadFailure;
                    _nextLoadFailure = null;
                    throw failure;
                }

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
