using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Logging;
using NUnit.Framework;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 通过可控 provider 验证 AssetUtility 的包级异步所有权，并覆盖 AssetReference 的 Core 兼容边界；
    /// 不依赖 YooAsset 时序、真实文件系统速度或 Adapter 测试夹具。
    /// </summary>
    public sealed class AssetOperationCoordinationTests
    {
        private const string Package = "CoordinationTestPackage";

        private GameObject _root;
        private MonoGameContextBase _context;
        private AssetUtility _utility;
        private ControllableAssetProvider _provider;
        private List<string> _callerCdnUrls;
        private Dictionary<string, bool> _callerOnDemandPolicies;
        private AssetProviderConfig _callerConfig;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return SetUpAsync().ToCoroutine();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _context = null;
            _utility = null;
            _provider = null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator Initialize_CallerCancellationOnlyDetachesWaiter_AndSecondCallerJoinsOwner()
            => Initialize_CallerCancellationOnlyDetachesWaiter_AndSecondCallerJoinsOwnerAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Maintenance_CancelledWaiterDoesNotReleaseLane_AcrossClearVariantsAndUnload()
            => Maintenance_CancelledWaiterDoesNotReleaseLane_AcrossClearVariantsAndUnloadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Maintenance_ProviderFailureIsRethrown_AndNextOperationStillRuns()
            => Maintenance_ProviderFailureIsRethrown_AndNextOperationStillRunsAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Initialize_FailedStateReentrantRetry_DoesNotCrossCompleteAttempts()
            => Initialize_FailedStateReentrantRetry_DoesNotCrossCompleteAttemptsAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Initialize_SynchronousFailureReentrantRetry_DoesNotRetargetOriginalCaller()
            => Initialize_SynchronousFailureReentrantRetry_DoesNotRetargetOriginalCallerAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Destroy_CancelsInitializationOwnerAndWaiter()
            => Destroy_CancelsInitializationOwnerAndWaiterAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Destroy_CancelsRunningMaintenanceAndSkipsQueuedOperation()
            => Destroy_CancelsRunningMaintenanceAndSkipsQueuedOperationAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Destroy_WhenProviderDisposeThrows_StillCompletesStateAndUnregisters()
            => Destroy_WhenProviderDisposeThrows_StillCompletesStateAndUnregistersAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator LocationState_DistinguishesNotReadyInvalidLocalAndRemote_PerPackage()
            => LocationState_DistinguishesNotReadyInvalidLocalAndRemote_PerPackageAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator EmptyLocation_IsReportedThroughLoggingSeam_BeforeProviderWork()
            => EmptyLocation_IsReportedThroughLoggingSeam_BeforeProviderWorkAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator EnsureInitialized_BeforeUtilityStart_StartsConfiguredAutoPackage()
            => EnsureInitialized_BeforeUtilityStart_StartsConfiguredAutoPackageAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Configure_FreezesCallerConfigAndIsolatesProviderSnapshot()
            => Configure_FreezesCallerConfigAndIsolatesProviderSnapshotAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Initialize_ProviderWorkerCompletion_PublishesStateAndReturnsOnMainThread()
            => Initialize_ProviderWorkerCompletion_PublishesStateAndReturnsOnMainThreadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Initialize_WorkerCallerCancellation_ReturnsMainThreadWithoutCancelingOwner()
            => Initialize_WorkerCallerCancellation_ReturnsMainThreadWithoutCancelingOwnerAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator LoadVariants_ProviderWorkerSuccessAndFailure_ReturnOnMainThread()
            => LoadVariants_ProviderWorkerSuccessAndFailure_ReturnOnMainThreadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Downloader_ProviderWorkerSuccessFailureAndCancellation_ReturnOnMainThread()
            => Downloader_ProviderWorkerSuccessFailureAndCancellation_ReturnOnMainThreadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator DownloaderFactories_ProviderReturnsNull_FailAtAdapterBoundary()
            => DownloaderFactories_ProviderReturnsNull_FailAtAdapterBoundaryAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator SceneHandle_ProviderWorkerUnloadSuccessAndFailure_ReturnOnMainThread()
            => SceneHandle_ProviderWorkerUnloadSuccessAndFailure_ReturnOnMainThreadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator Load_ProviderIgnoresCancellation_LateHandleIsDisposedAndOceReturnsMainThread()
            => Load_ProviderIgnoresCancellation_LateHandleIsDisposedAndOceReturnsMainThreadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator AssetReference_FailedAttemptReentrantRetry_StartsNewPhysicalLoad()
            => AssetReference_FailedAttemptReentrantRetry_StartsNewPhysicalLoadAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator AssetReference_WorkerCallerCancellation_ReturnsMainThreadAndSharedLoadContinues()
            => AssetReference_WorkerCallerCancellation_ReturnsMainThreadAndSharedLoadContinuesAsync().ToCoroutine();

        [Test]
        public void DefaultPackageConvenienceMembers_NoDefault_FailWithActionableError()
        {
            _utility.Configure(string.Empty, new AssetProviderConfig(), AssetPlayMode.EditorSimulate);

            var initStateError = Assert.Throws<InvalidOperationException>(() => _ = _utility.InitState);
            var namedStateError = Assert.Throws<InvalidOperationException>(() => _utility.GetInitState(null));
            var downloaderError = Assert.Throws<InvalidOperationException>(() => _utility.CreateAllDownloader());

            foreach (var error in new[] { initStateError, namedStateError, downloaderError })
            {
                StringAssert.Contains("没有默认资源包", error.Message);
                StringAssert.Contains("packageName", error.Message);
            }
        }

        [Test]
        public void ConfigureBeforeStart_SuppressesInspectorAutoInitialization()
        {
            Assert.AreEqual(0, _provider.InitializeCalls,
                "代码引导在 Start 前 Configure 后，AssetUtility 不应再按 Inspector 默认设置额外启动一个包");
        }

        [Test]
        public void DownloadProgressReport_EmptySnapshotOnlyCompletesAfterExplicitHundredPercent()
        {
            Assert.IsFalse(new DownloadProgressReport(0f, 0, 0, 0, 0).IsDone,
                "创建时的空快照还没有执行 Download，不能提前显示完成");
            Assert.IsTrue(new DownloadProgressReport(1f, 0, 0, 0, 0).IsDone,
                "无内容可下的 Download 完成后，进度快照应与 downloader.IsDone 一致");
            Assert.IsTrue(new DownloadProgressReport(0.75f, 4, 4, 100, 100).IsDone,
                "非空任务以完成数量为真源，不依赖浮点进度恰好等于 1");
            Assert.IsFalse(new DownloadProgressReport(1f, 4, 3, 100, 75).IsDone,
                "非空任务的矛盾快照不能用浮点进度掩盖尚未完成的文件");
        }

        /// <summary>
        /// 未显式绑定的旧引用仍可从 Main 迁移回退，但必须留下所有权警告，并跟随 Main 取消信号；
        /// 这是 Core 的兼容契约，不应依赖 YooAsset fixture 或 Adapter 内部实现。
        /// </summary>
        [Test]
        public void AssetReference_UnboundMainFallback_IsVisibleAndUsesMainLifetime()
        {
            var reference = new AssetReference<GameObject>();
            var previousMain = GameContext.Main;
            var previousMinLevel = Log.MinLevel;
            try
            {
                Log.MinLevel = LogLevel.Warning;
                GameContext.Main = _context.RawContext;

                LogAssert.Expect(LogType.Warning,
                    new Regex(@"\[AssetReference\].*回退使用 GameContext\.Main.*必须手动释放"));
                var resolved = reference.ResolveUtility();

                Assert.AreSame(_utility, resolved);
                Assert.IsTrue(reference.IsBound,
                    "首次回退后应缓存 utility，避免每次 Get 重复解析和重复警告。");
                Assert.AreEqual(_context.CancellationToken, reference.HostToken,
                    "旧用法至少应跟随 Main 生命周期取消等待，但这不等于被 Bag 托管。");
            }
            finally
            {
                reference.Bind(null, default);
                GameContext.Main = previousMain;
                Log.MinLevel = previousMinLevel;
            }
        }

        [UnityTest]
        public IEnumerator AssetReferenceList_ResourceLevelNullPreservesOtherPositions()
            => UniTask.ToCoroutine(async () =>
            {
                var asset = new GameObject("LoadedListItem");
                asset.transform.SetParent(_root.transform);
                var loaded = new AssetReference<GameObject>();
                SetPrivateField(loaded, "_handle", new TestAssetHandle<GameObject>(asset));
                var missing = new AssetReference<GameObject>();
                var list = CreateReferenceList(loaded, missing);

                LogAssert.Expect(LogType.Warning, new Regex(@"\[AssetReference\].*GUID 为空"));
                var results = await list.GetAll();

                Assert.AreEqual(2, results.Length);
                Assert.AreSame(asset, results[0]);
                Assert.IsNull(results[1],
                    "单项资源级问题应保留原位置的 null，不能抹掉其它成功结果。");
                list.Dispose();
            });

        [UnityTest]
        public IEnumerator AssetReferenceList_SystemFailureFailsWholeBatchWithoutWrappingRoot()
            => UniTask.ToCoroutine(async () =>
            {
                var gate = new UniTaskCompletionSource<IAssetHandle<GameObject>>();
                var reference = CreatePendingReference(gate);
                var list = CreateReferenceList(reference);
                var expected = new InvalidOperationException("asset-list-system-failure");
                UniTask<GameObject[]> loading = list.GetAll();

                gate.TrySetException(expected);
                try
                {
                    await loading;
                    Assert.Fail("系统级故障必须终止整个 GetAll。");
                }
                catch (InvalidOperationException actual)
                {
                    Assert.AreSame(expected, actual,
                        "GetAll 不应把初始化/Adapter 根异常降级成 null 或包装成另一异常。");
                }
                finally
                {
                    ResetPendingReference(reference);
                    list.Dispose();
                }
            });

        [UnityTest]
        public IEnumerator AssetReferenceList_CallerCancellationCancelsWholeWait()
            => UniTask.ToCoroutine(async () =>
            {
                var gate = new UniTaskCompletionSource<IAssetHandle<GameObject>>();
                var reference = CreatePendingReference(gate);
                var list = CreateReferenceList(reference);
                using var caller = new CancellationTokenSource();
                UniTask<GameObject[]> loading = list.GetAll(caller.Token);

                caller.Cancel();
                try
                {
                    await loading;
                    Assert.Fail("调用方取消必须终止整个 GetAll 等待。");
                }
                catch (OperationCanceledException actual)
                {
                    Assert.AreEqual(caller.Token, actual.CancellationToken);
                }
                finally
                {
                    gate.TrySetResult(null);
                    ResetPendingReference(reference);
                    list.Dispose();
                }
            });

        private async UniTask EnsureInitialized_BeforeUtilityStart_StartsConfiguredAutoPackageAsync()
        {
            var contextObject = new GameObject("EarlyAssetContext");
            contextObject.transform.SetParent(_root.transform);
            contextObject.AddComponent<MonoGameContextBase>();

            var utilityObject = new GameObject("EarlyAssetUtility");
            utilityObject.SetActive(false);
            utilityObject.transform.SetParent(contextObject.transform);
            var utility = utilityObject.AddComponent<AssetUtility>();
            var settings = new AssetRuntimeSettings(
                new[] { new AssetPackageConfig(Package, autoInitialize: true) },
                Package,
                AssetPlayMode.EditorSimulate,
                AssetPlayMode.Offline,
                Array.Empty<string>(),
                downloadingMaxNumber: 1,
                failedTryAgain: 0,
                fileOffset: 0);
            var settingsField = typeof(AssetUtility).GetField(
                "_settings",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(settingsField);
            settingsField.SetValue(utility, settings);

            utilityObject.SetActive(true); // Awake 同步应用 Settings；Start 尚未获得本帧执行机会。
            var provider = new ControllableAssetProvider();
            utility.ReplaceProviderForTesting(provider);
            var initialization = provider.PlanInitialization();

            UniTask waiting = utility.EnsureInitialized(Package);
            await initialization.Started.Task;
            Assert.AreEqual(1, provider.InitializeCalls,
                "已配置自动初始化的包不应因调用早于 AssetUtility.Start 而误报 Idle");
            initialization.Release.TrySetResult();
            await waiting;
            Assert.AreEqual(AssetInitState.Ready, utility.GetInitState(Package).CurrentValue);

            UnityEngine.Object.Destroy(contextObject);
            await UniTask.Yield();
        }

        private async UniTask SetUpAsync()
        {
            _root = new GameObject(nameof(AssetOperationCoordinationTests));

            var contextObject = new GameObject("Context");
            contextObject.transform.SetParent(_root.transform);
            _context = contextObject.AddComponent<MonoGameContextBase>();

            var utilityObject = new GameObject("AssetUtility");
            utilityObject.transform.SetParent(contextObject.transform);
            _utility = utilityObject.AddComponent<AssetUtility>();

            _provider = new ControllableAssetProvider();
            _utility.ReplaceProviderForTesting(_provider);
            _callerCdnUrls = new List<string> { "https://original.example/" };
            _callerOnDemandPolicies = new Dictionary<string, bool> { [Package] = false };
            _callerConfig = new AssetProviderConfig
            {
                CdnUrls = _callerCdnUrls,
                EnableOnDemandDownloadByPackage = _callerOnDemandPolicies,
                FileOffset = 16,
                DownloadingMaxNumber = 4,
                FailedTryAgain = 2,
            };
            _utility.Configure(Package, _callerConfig, AssetPlayMode.EditorSimulate);
            await UniTask.Yield();
        }

        private AssetReference<GameObject> CreatePendingReference(
            UniTaskCompletionSource<IAssetHandle<GameObject>> gate)
        {
            var reference = new AssetReference<GameObject>();
            SetPrivateField((AssetReferenceBase)reference, "_assetGUID", "test-guid");
            reference.Bind(_utility, default);
            SetPrivateField(reference, "<IsLoading>k__BackingField", true);
            SetPrivateField(reference, "_loadTcs", gate);
            return reference;
        }

        private static void ResetPendingReference(AssetReference<GameObject> reference)
        {
            SetPrivateField(reference, "<IsLoading>k__BackingField", false);
            SetPrivateField<UniTaskCompletionSource<IAssetHandle<GameObject>>>(reference, "_loadTcs", null);
        }

        private static AssetReferenceList<T> CreateReferenceList<T>(params AssetReference<T>[] items)
            where T : UnityEngine.Object
        {
            var list = new AssetReferenceList<T>();
            SetPrivateField(list, "_items", new List<AssetReference<T>>(items));
            return list;
        }

        private static void SetPrivateField<TValue>(object target, string name, TValue value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? target.GetType().BaseType?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"测试夹具找不到字段 {target.GetType().Name}.{name}");
            field.SetValue(target, value);
        }

        private sealed class TestAssetHandle<T> : IAssetHandle<T> where T : UnityEngine.Object
        {
            private bool _valid = true;

            public TestAssetHandle(T asset) => Asset = asset;

            public T Asset { get; }
            public bool IsValid => _valid;
            public void Dispose() => _valid = false;
        }

        private sealed class TestSceneHandle : ISceneHandle
        {
            public Scene Scene => default;
            public bool IsValid { get; private set; } = true;
            public bool CompleteUnloadOnThreadPool { get; set; }
            public Exception UnloadFailure { get; set; }
            public int UnloadCompletionThread { get; private set; } = -1;
            public bool Activate() => IsValid;
            public bool UnSuspend() => IsValid;
            public async UniTask Unload()
            {
                if (CompleteUnloadOnThreadPool)
                    await UniTask.SwitchToThreadPool();
                UnloadCompletionThread = Thread.CurrentThread.ManagedThreadId;
                if (UnloadFailure != null) throw UnloadFailure;
                IsValid = false;
            }
            public void Dispose() => IsValid = false;
        }

        private sealed class TestAssetDownloader : IAssetDownloader
        {
            private readonly ReactiveProperty<DownloadProgressReport> _progress =
                new(new DownloadProgressReport(0f, 1, 0, 10, 0));

            public readonly UniTaskCompletionSource Started = new();
            public readonly UniTaskCompletionSource Release = new();
            public bool CompleteOnThreadPool { get; set; }
            public bool WaitForRelease { get; set; }
            public Exception Failure { get; set; }
            public int CompletionThread { get; private set; } = -1;
            public int TotalCount => 1;
            public long TotalBytes => 10;
            public bool IsDone { get; private set; }
            public ReadOnlyReactiveProperty<DownloadProgressReport> Progress => _progress;

            public async UniTask Download(CancellationToken ct = default)
            {
                Started.TrySetResult();
                if (WaitForRelease)
                    await Release.Task.AttachExternalCancellation(ct);
                else
                    ct.ThrowIfCancellationRequested();

                if (Failure == null)
                {
                    IsDone = true;
                    _progress.Value = new DownloadProgressReport(1f, 1, 1, 10, 10);
                }

                if (CompleteOnThreadPool)
                    await UniTask.SwitchToThreadPool();
                CompletionThread = Thread.CurrentThread.ManagedThreadId;
                if (Failure != null) throw Failure;
            }
        }

        private async UniTask Initialize_ProviderWorkerCompletion_PublishesStateAndReturnsOnMainThreadAsync()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            int readyThread = -1;
            var gate = _provider.PlanInitialization();
            using var subscription = _utility.GetInitState(Package).Subscribe(state =>
            {
                if (state == AssetInitState.Ready)
                    readyThread = Thread.CurrentThread.ManagedThreadId;
            });

            UniTask initializing = _utility.Initialize(Package);
            await gate.Started.Task;
            CompleteOnThreadPool(gate.Release).Forget();
            await initializing;

            Assert.AreNotEqual(mainThread, gate.CompletionThread,
                "测试 Provider 必须真实在 worker 物理完成，才能证明 Core 边界有效");
            Assert.AreEqual(mainThread, readyThread,
                "AssetInitState 不得从 Provider worker 发布");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                "Initialize 的成功终态必须回到 Unity 主线程");
        }

        private async UniTask Initialize_WorkerCallerCancellation_ReturnsMainThreadWithoutCancelingOwnerAsync()
        {
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            var gate = _provider.PlanInitialization();
            using var caller = new CancellationTokenSource();
            UniTask waiting = _utility.Initialize(Package, caller.Token);
            await gate.Started.Task;

            CancelOnThreadPool(caller).Forget();
            try
            {
                await waiting;
                Assert.Fail("worker 发出的调用方取消应结束当前等待。");
            }
            catch (OperationCanceledException)
            {
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "调用方 token 在 worker 取消也必须从主线程交付 OCE");
            }

            Assert.IsFalse(gate.OwnerToken.IsCancellationRequested,
                "短命 waiter 取消不能传染物理初始化 owner");
            gate.Release.TrySetResult();
            await _utility.EnsureInitialized(Package);
        }

        private async UniTask LoadVariants_ProviderWorkerSuccessAndFailure_ReturnOnMainThreadAsync()
        {
            await MakeReady();
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            var asset = new GameObject("WorkerLoadedAsset");
            asset.transform.SetParent(_root.transform);
            var sceneHandle = new TestSceneHandle();
            _provider.AssetResult = new TestAssetHandle<GameObject>(asset);
            _provider.SceneResult = sceneHandle;
            _provider.TextResult = "worker-text";
            _provider.BytesResult = new byte[] { 4, 2 };
            _provider.CompleteLoadsOnThreadPool = true;

            var assetHandle = await _utility.Load<GameObject>(Package, "worker-asset");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
            Assert.AreSame(asset, assetHandle.Asset);

            var loadedScene = await _utility.LoadScene(Package, "worker-scene", LoadSceneMode.Additive);
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
            Assert.AreNotSame(sceneHandle, loadedScene,
                "Core 应包装 Provider 场景句柄，统一后续 Unload 的主线程终态");
            Assert.IsTrue(loadedScene.IsValid);

            Assert.AreEqual("worker-text", await _utility.LoadText(Package, "worker-text"));
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
            CollectionAssert.AreEqual(new byte[] { 4, 2 }, await _utility.LoadBytes(Package, "worker-bytes"));
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
            Assert.That(_provider.LoadCompletionThreads, Has.All.Not.EqualTo(mainThread),
                "测试 Provider 的四种加载必须都真实结束在 worker");

            var expected = new InvalidOperationException("worker-load-failed");
            _provider.NextAssetLoadFailure = expected;
            try
            {
                await _utility.Load<GameObject>(Package, "worker-failure");
                Assert.Fail("Provider 失败应原样传播。");
            }
            catch (InvalidOperationException actual)
            {
                Assert.AreSame(expected, actual);
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "Provider worker failure 也必须从主线程交付");
            }

            assetHandle.Dispose();
            loadedScene.Dispose();
            Assert.IsFalse(sceneHandle.IsValid, "包装句柄 Dispose 必须委托到底层 handle");
        }

        private async UniTask Downloader_ProviderWorkerSuccessFailureAndCancellation_ReturnOnMainThreadAsync()
        {
            await MakeReady();
            int mainThread = Thread.CurrentThread.ManagedThreadId;

            var success = new TestAssetDownloader { CompleteOnThreadPool = true };
            _provider.DownloaderResult = success;
            IAssetDownloader publicSuccess = _utility.CreateAllDownloader(Package);
            Assert.AreSame(success.Progress, publicSuccess.Progress,
                "Core 不应复制进度流；Provider 仍负责在主线程发布原状态流");
            await publicSuccess.Download();
            Assert.AreNotEqual(mainThread, success.CompletionThread,
                "测试 Provider 必须真实在 worker 结束物理下载");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                "下载成功终态必须回到 Unity 主线程");

            var expected = new InvalidOperationException("worker-download-failed");
            var failure = new TestAssetDownloader
            {
                CompleteOnThreadPool = true,
                Failure = expected,
            };
            _provider.DownloaderResult = failure;
            try
            {
                await _utility.CreateAllDownloader(Package).Download();
                Assert.Fail("Provider 下载失败应原样传播。");
            }
            catch (InvalidOperationException actual)
            {
                Assert.AreSame(expected, actual);
                Assert.AreNotEqual(mainThread, failure.CompletionThread);
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "下载异常终态必须回到 Unity 主线程");
            }

            var cancellation = new TestAssetDownloader { WaitForRelease = true };
            _provider.DownloaderResult = cancellation;
            using var caller = new CancellationTokenSource();
            UniTask waiting = _utility.CreateAllDownloader(Package).Download(caller.Token);
            await cancellation.Started.Task;
            CancelOnThreadPool(caller).Forget();
            try
            {
                await waiting;
                Assert.Fail("worker 发出的取消应保留 OperationCanceledException。");
            }
            catch (OperationCanceledException)
            {
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "下载取消终态也必须回到 Unity 主线程");
            }
        }

        private async UniTask DownloaderFactories_ProviderReturnsNull_FailAtAdapterBoundaryAsync()
        {
            await MakeReady();
            _provider.DownloaderResult = null;

            var tagError = Assert.Throws<InvalidOperationException>(() =>
                _utility.CreateTagDownloader(Package, new[] { "core" }));
            var allError = Assert.Throws<InvalidOperationException>(() =>
                _utility.CreateAllDownloader(Package));
            var locationError = Assert.Throws<InvalidOperationException>(() =>
                _utility.CreateLocationDownloader(Package, new[] { "ui/logo" }));

            AssertDownloaderContractError(tagError, nameof(IAssetProvider.CreateTagDownloader));
            AssertDownloaderContractError(allError, nameof(IAssetProvider.CreateAllDownloader));
            AssertDownloaderContractError(locationError, nameof(IAssetProvider.CreateLocationDownloader));
        }

        private static void AssertDownloaderContractError(InvalidOperationException error, string operation)
        {
            StringAssert.Contains(nameof(ControllableAssetProvider), error.Message);
            StringAssert.Contains(operation, error.Message);
            StringAssert.Contains("TotalCount == 0", error.Message);
        }

        private async UniTask SceneHandle_ProviderWorkerUnloadSuccessAndFailure_ReturnOnMainThreadAsync()
        {
            await MakeReady();
            int mainThread = Thread.CurrentThread.ManagedThreadId;

            var success = new TestSceneHandle { CompleteUnloadOnThreadPool = true };
            _provider.SceneResult = success;
            ISceneHandle publicSuccess = await _utility.LoadScene(Package, "worker-unload-success");
            await publicSuccess.Unload();
            Assert.AreNotEqual(mainThread, success.UnloadCompletionThread,
                "测试 Provider 必须真实在 worker 结束物理卸载");
            Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                "场景卸载成功终态必须回到 Unity 主线程");
            Assert.IsFalse(publicSuccess.IsValid);

            var expected = new InvalidOperationException("worker-unload-failed");
            var failure = new TestSceneHandle
            {
                CompleteUnloadOnThreadPool = true,
                UnloadFailure = expected,
            };
            _provider.SceneResult = failure;
            ISceneHandle publicFailure = await _utility.LoadScene(Package, "worker-unload-failure");
            try
            {
                await publicFailure.Unload();
                Assert.Fail("Provider 场景卸载失败应原样传播。");
            }
            catch (InvalidOperationException actual)
            {
                Assert.AreSame(expected, actual);
                Assert.AreNotEqual(mainThread, failure.UnloadCompletionThread);
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "场景卸载异常终态必须回到 Unity 主线程");
            }
            finally
            {
                publicFailure.Dispose();
            }
        }

        private async UniTask Load_ProviderIgnoresCancellation_LateHandleIsDisposedAndOceReturnsMainThreadAsync()
        {
            await MakeReady();
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            var asset = new GameObject("LateCanceledAsset");
            asset.transform.SetParent(_root.transform);
            var lateHandle = new TestAssetHandle<GameObject>(asset);
            var gate = _provider.PlanAssetLoad(lateHandle, ignoreCancellation: true);
            using var caller = new CancellationTokenSource();

            UniTask<IAssetHandle<GameObject>> waiting =
                _utility.Load<GameObject>(Package, "late-canceled-asset", caller.Token);
            await gate.Started.Task;
            CancelOnThreadPool(caller).Forget();
            await UniTask.WaitUntil(() => caller.IsCancellationRequested);
            Assert.IsFalse(waiting.GetAwaiter().IsCompleted,
                "Provider 忽略 token 时，Core 仍需等物理结果到达后才能回收句柄并交付取消");

            gate.Release.TrySetResult();
            try
            {
                await waiting;
                Assert.Fail("迟到的成功结果不能覆盖已经发生的调用方取消。对外应保留 OCE。");
            }
            catch (OperationCanceledException)
            {
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "迟到结果的取消终态也必须从 Unity 主线程交付");
            }

            Assert.IsFalse(lateHandle.IsValid,
                "已经失去调用方 owner 的迟到 handle 必须由 Core 回收，不能泄漏");
        }

        private async UniTask AssetReference_FailedAttemptReentrantRetry_StartsNewPhysicalLoadAsync()
        {
            await MakeReady();
            var asset = new GameObject("AssetReferenceRetryResult");
            asset.transform.SetParent(_root.transform);
            var expected = new InvalidOperationException("asset-reference-first-failure");
            var firstGate = _provider.PlanAssetLoad(failure: expected);
            var retryGate = _provider.PlanAssetLoad(new TestAssetHandle<GameObject>(asset));
            var reference = new AssetReference<GameObject>();
            SetPrivateField((AssetReferenceBase)reference, "_assetGUID", "retry-guid");
            reference.Bind(_utility, default);

            UniTask<GameObject> retry = default;
            async UniTask ObserveFailureAndRetry()
            {
                try
                {
                    await reference.Get();
                    Assert.Fail("第一次资源加载应失败。");
                }
                catch (InvalidOperationException actual)
                {
                    Assert.AreSame(expected, actual);
                    // UniTaskCompletionSource 同步恢复本 continuation；这里就是旧 owner 发布终态时的重入窗口。
                    retry = reference.Get();
                }
            }

            UniTask observer = ObserveFailureAndRetry();
            await firstGate.Started.Task;
            firstGate.Release.TrySetResult();
            await observer;

            Assert.AreEqual(2, _provider.AssetLoadCalls,
                "失败 task 的 continuation 内立即重试必须建立新 owner，不能加入已完成的旧 TCS");
            Assert.IsFalse(retry.GetAwaiter().IsCompleted);
            retryGate.Release.TrySetResult();
            Assert.AreSame(asset, await retry);
            reference.Dispose();
        }

        private async UniTask AssetReference_WorkerCallerCancellation_ReturnsMainThreadAndSharedLoadContinuesAsync()
        {
            await MakeReady();
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            var asset = new GameObject("AssetReferenceSharedResult");
            asset.transform.SetParent(_root.transform);
            var gate = _provider.PlanAssetLoad(new TestAssetHandle<GameObject>(asset));
            var reference = new AssetReference<GameObject>();
            SetPrivateField((AssetReferenceBase)reference, "_assetGUID", "shared-guid");
            reference.Bind(_utility, default);
            using var caller = new CancellationTokenSource();

            UniTask<GameObject> waiting = reference.Get(caller.Token);
            await gate.Started.Task;
            CancelOnThreadPool(caller).Forget();
            try
            {
                await waiting;
                Assert.Fail("调用方等待应取消。");
            }
            catch (OperationCanceledException)
            {
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "AssetReference 的局部 waiter 取消也必须回主线程");
            }

            Assert.AreEqual(1, _provider.AssetLoadCalls,
                "局部 waiter 取消不能重启或中断引用级共享加载");
            gate.Release.TrySetResult();
            await UniTask.WaitUntil(() => reference.IsLoaded);
            Assert.AreSame(asset, reference.Asset);
            reference.Dispose();
        }

        private static async UniTask CompleteOnThreadPool(UniTaskCompletionSource completion)
        {
            await UniTask.SwitchToThreadPool();
            completion.TrySetResult();
        }

        private static async UniTask CancelOnThreadPool(CancellationTokenSource source)
        {
            await UniTask.SwitchToThreadPool();
            source.Cancel();
        }

        private async UniTask Configure_FreezesCallerConfigAndIsolatesProviderSnapshotAsync()
        {
            _callerCdnUrls.Clear();
            _callerCdnUrls.Add("https://mutated.example/");
            _callerOnDemandPolicies[Package] = true;
            _callerConfig.FileOffset = 999;
            _callerConfig.DownloadingMaxNumber = 99;
            _callerConfig.FailedTryAgain = 88;

            var gate = _provider.PlanInitialization();
            UniTask initializing = _utility.Initialize(Package);
            await gate.Started.Task;

            AssetProviderConfig received = _provider.ReceivedInitializationConfig;
            Assert.That(received, Is.Not.Null);
            Assert.That(received, Is.Not.SameAs(_callerConfig),
                "Utility 必须接管一份配置快照，不能把调用方仍可修改的 DTO 直接交给 Adapter。");
            CollectionAssert.AreEqual(new[] { "https://original.example/" }, received.CdnUrls);
            Assert.That(received.ShouldEnableOnDemandDownload(Package), Is.False);
            Assert.That(received.FileOffset, Is.EqualTo(16));
            Assert.That(received.DownloadingMaxNumber, Is.EqualTo(4));
            Assert.That(received.FailedTryAgain, Is.EqualTo(2));

            received.CdnUrls = new[] { "https://provider-mutated.example/" };
            received.EnableOnDemandDownloadByPackage = new Dictionary<string, bool> { [Package] = true };
            received.FileOffset = 777;
            received.DownloadingMaxNumber = 77;
            received.FailedTryAgain = 66;

            gate.Release.TrySetResult();
            await initializing;
            _provider.DownloaderResult = new TestAssetDownloader();
            _utility.CreateAllDownloader(Package);

            Assert.That(_provider.LastDownloaderMaxConcurrent, Is.EqualTo(4),
                "Adapter 收到的 DTO 不能反向改写 Utility-owned 下载参数。");
            Assert.That(_provider.LastDownloaderRetries, Is.EqualTo(2));
        }

        private async UniTask Initialize_CallerCancellationOnlyDetachesWaiter_AndSecondCallerJoinsOwnerAsync()
        {
            var initialization = _provider.PlanInitialization();
            using var caller = new CancellationTokenSource();
            var first = _utility.Initialize(Package, caller.Token);
            await initialization.Started.Task;

            Assert.AreEqual(AssetInitState.Initializing, _utility.GetInitState(Package).CurrentValue);
            Assert.AreEqual(1, _provider.InitializeCalls);

            caller.Cancel();
            await ExpectCanceled(first);

            Assert.AreEqual(AssetInitState.Initializing, _utility.GetInitState(Package).CurrentValue,
                "调用者离开不能把仍运行的物理初始化误标成 Failed");
            Assert.IsFalse(_provider.InitializeOwnerToken.IsCancellationRequested,
                "短命调用者 token 不应传给 provider 作为物理操作 owner");

            var second = _utility.Initialize(Package);
            await UniTask.Yield();
            Assert.AreEqual(1, _provider.InitializeCalls, "第二个调用者必须加入既有 owner，不能重启原生初始化");

            initialization.Release.TrySetResult();
            await second;
            Assert.AreEqual(AssetInitState.Ready, _utility.GetInitState(Package).CurrentValue);
        }

        private async UniTask Maintenance_CancelledWaiterDoesNotReleaseLane_AcrossClearVariantsAndUnloadAsync()
        {
            await MakeReady();

            var clear = _provider.PlanMaintenance("clear");
            var tags = _provider.PlanMaintenance("tags");
            var locations = _provider.PlanMaintenance("locations");
            var unload = _provider.PlanMaintenance("unload");
            using var firstCaller = new CancellationTokenSource();

            var first = _utility.ClearCache(Package, AssetCacheClearMode.All, firstCaller.Token);
            await clear.Started.Task;
            firstCaller.Cancel();
            await ExpectCanceled(first);

            var mutableTags = new List<string> { "chapter-a" };
            var mutableLocations = new List<string> { "Assets/A.prefab" };
            var second = _utility.ClearCacheByTags(Package, mutableTags);
            var third = _utility.ClearCacheByLocations(Package, mutableLocations);
            var fourth = _utility.UnloadUnusedAssets(Package);
            mutableTags[0] = "mutated-after-enqueue";
            mutableLocations[0] = "Assets/Mutated.prefab";

            await UniTask.Yield();
            CollectionAssert.AreEqual(new[] { "clear" }, _provider.StartedMaintenanceKinds,
                "首个物理操作未结束前，取消 waiter 不能释放 lane");

            clear.Release.TrySetResult();
            await tags.Started.Task;
            CollectionAssert.AreEqual(new[] { "clear", "tags" }, _provider.StartedMaintenanceKinds);

            tags.Release.TrySetResult();
            await locations.Started.Task;
            CollectionAssert.AreEqual(new[] { "clear", "tags", "locations" }, _provider.StartedMaintenanceKinds);

            locations.Release.TrySetResult();
            await unload.Started.Task;
            CollectionAssert.AreEqual(new[] { "clear", "tags", "locations", "unload" },
                _provider.StartedMaintenanceKinds);

            unload.Release.TrySetResult();
            await UniTask.WhenAll(second, third, fourth);

            Assert.AreEqual(1, _provider.MaxActiveMaintenance);
            CollectionAssert.AreEqual(new[] { "chapter-a" }, _provider.ReceivedTags,
                "排队维护操作必须冻结调用参数");
            CollectionAssert.AreEqual(new[] { "Assets/A.prefab" }, _provider.ReceivedLocations);
        }

        private async UniTask Maintenance_ProviderFailureIsRethrown_AndNextOperationStillRunsAsync()
        {
            await MakeReady();
            var expected = new InvalidOperationException("physical-clear-failed");
            var clear = _provider.PlanMaintenance("clear", expected);
            var unload = _provider.PlanMaintenance("unload");

            var first = _utility.ClearCache(Package, AssetCacheClearMode.Unused);
            await clear.Started.Task;
            var second = _utility.UnloadUnusedAssets(Package);

            clear.Release.TrySetResult();
            Exception actual = null;
            try
            {
                await first;
            }
            catch (Exception ex)
            {
                actual = ex;
            }

            await unload.Started.Task;
            unload.Release.TrySetResult();
            await second;

            Assert.AreSame(expected, actual);
            CollectionAssert.AreEqual(new[] { "clear", "unload" }, _provider.StartedMaintenanceKinds);
        }

        private async UniTask Initialize_FailedStateReentrantRetry_DoesNotCrossCompleteAttemptsAsync()
        {
            var expected = new InvalidOperationException("first-init-failed");
            var firstGate = _provider.PlanInitialization(expected);
            var retryGate = _provider.PlanInitialization();
            UniTask retry = default;
            bool retryRequested = false;

            using var subscription = _utility.GetInitState(Package).Subscribe(state =>
            {
                if (state != AssetInitState.Failed || retryRequested) return;
                retryRequested = true;
                // R3 同步发布 Failed；这里刻意在回调内立即重试，锁定 attempt 不能被旧 owner 串台完成。
                retry = _utility.Initialize(Package);
            });

            var first = _utility.Initialize(Package);
            await firstGate.Started.Task;
            var ensureFirstAttempt = _utility.EnsureInitialized(Package);
            LogAssert.Expect(LogType.Error,
                new Regex("Package 'CoordinationTestPackage'.*初始化失败", RegexOptions.Singleline));
            LogAssert.Expect(LogType.Exception, new Regex("first-init-failed"));
            var sink = new CapturingSink();
            Log.AddSink(sink);
            try
            {
                firstGate.Release.TrySetResult();
                await first; // Initialize 的普通物理失败仍以状态表达，不直接抛。
            }
            finally
            {
                Log.RemoveSink(sink);
            }

            AssertInitializationFailureEntry(sink, expected);
            Exception ensureError = null;
            try
            {
                await ensureFirstAttempt;
            }
            catch (Exception ex)
            {
                ensureError = ex;
            }

            await retryGate.Started.Task;
            Assert.AreSame(expected, ensureError, "加入旧 attempt 的 EnsureInitialized 必须收到旧失败");
            Assert.AreEqual(2, _provider.InitializeCalls);
            Assert.AreEqual(AssetInitState.Initializing, _utility.GetInitState(Package).CurrentValue);
            Assert.IsFalse(retry.GetAwaiter().IsCompleted,
                "旧 owner 完成自己的 attempt 时，不能提前完成同步回调中新建的重试 attempt");

            retryGate.Release.TrySetResult();
            await retry;
            Assert.AreEqual(AssetInitState.Ready, _utility.GetInitState(Package).CurrentValue);
        }

        private async UniTask Initialize_SynchronousFailureReentrantRetry_DoesNotRetargetOriginalCallerAsync()
        {
            var expected = new InvalidOperationException("sync-init-failed");
            var firstGate = _provider.PlanInitialization(expected);
            var retryGate = _provider.PlanInitialization();
            firstGate.Release.TrySetResult(); // 让 provider 在 Initialize 调用栈内同步失败。
            UniTask retry = default;
            bool retryRequested = false;

            using var subscription = _utility.GetInitState(Package).Subscribe(state =>
            {
                if (state != AssetInitState.Failed || retryRequested) return;
                retryRequested = true;
                retry = _utility.Initialize(Package);
            });

            LogAssert.Expect(LogType.Error,
                new Regex("Package 'CoordinationTestPackage'.*初始化失败", RegexOptions.Singleline));
            LogAssert.Expect(LogType.Exception, new Regex("sync-init-failed"));
            var sink = new CapturingSink();
            Log.AddSink(sink);
            UniTask first;
            try
            {
                first = _utility.Initialize(Package);
            }
            finally
            {
                Log.RemoveSink(sink);
            }

            AssertInitializationFailureEntry(sink, expected);
            Assert.IsTrue(retryRequested);
            Assert.IsTrue(first.GetAwaiter().IsCompleted,
                "同步失败触发重试后，原调用必须完成自己的 attempt，不能被重定向到新 attempt");
            await first;
            await retryGate.Started.Task;
            Assert.IsFalse(retry.GetAwaiter().IsCompleted);

            retryGate.Release.TrySetResult();
            await retry;
            Assert.AreEqual(AssetInitState.Ready, _utility.GetInitState(Package).CurrentValue);
        }

        private async UniTask Destroy_CancelsInitializationOwnerAndWaiterAsync()
        {
            var gate = _provider.PlanInitialization();
            var waiting = _utility.Initialize(Package);
            await gate.Started.Task;
            var ownerToken = _provider.InitializeOwnerToken;

            UnityEngine.Object.Destroy(_root);
            _root = null;
            await UniTask.Yield();

            await ExpectCanceled(waiting);
            Assert.IsTrue(ownerToken.IsCancellationRequested);
            Assert.IsTrue(_provider.Disposed, "AssetUtility 应在取消 owner 后释放 provider");
        }

        private async UniTask Destroy_CancelsRunningMaintenanceAndSkipsQueuedOperationAsync()
        {
            await MakeReady();
            var clear = _provider.PlanMaintenance("clear");
            _provider.PlanMaintenance("unload");

            var running = _utility.ClearCache(Package, AssetCacheClearMode.All);
            await clear.Started.Task;
            var queued = _utility.UnloadUnusedAssets(Package);

            UnityEngine.Object.Destroy(_root);
            _root = null;
            await UniTask.Yield();

            await ExpectCanceled(running);
            await ExpectCanceled(queued);
            CollectionAssert.AreEqual(new[] { "clear" }, _provider.StartedMaintenanceKinds,
                "utility 销毁后，排队维护项不得再进入 provider");
            Assert.IsTrue(_provider.Disposed);
        }

        private async UniTask Destroy_WhenProviderDisposeThrows_StillCompletesStateAndUnregistersAsync()
        {
            bool stateCompleted = false;
            using var stateSubscription = _utility.GetInitState(Package).Subscribe(
                _ => { },
                _ => stateCompleted = true);
            _provider.ThrowOnDispose = true;

            LogAssert.Expect(LogType.Error,
                new Regex(@"\[AssetUtility\].*资源 Provider 在释放期间抛出异常"));
            LogAssert.Expect(LogType.Exception, new Regex("asset-provider-dispose-probe"));
            UnityEngine.Object.Destroy(_utility.gameObject);
            await UniTask.Yield(PlayerLoopTiming.Update);

            Assert.IsTrue(_provider.Disposed, "即使 Provider 抛错也应只执行一次释放尝试");
            Assert.IsTrue(stateCompleted, "Provider 释放异常不能截断已发布状态流的完结");
            Assert.IsFalse(_context.RawContext.TryResolve(typeof(IAssetUtility), out _),
                "Provider 释放异常不能跳过 MonoUtilityBase 的 Context 反注册");
            Assert.Throws<ObjectDisposedException>(() => _utility.GetInitState(Package),
                "销毁后的旧 Utility 引用不得重新创建脱离 Context 的状态流");
            Assert.Throws<ObjectDisposedException>(() => _ = _utility.InitState);
            Assert.Throws<ObjectDisposedException>(() => _ = _utility.IsInitialized);
        }

        private async UniTask LocationState_DistinguishesNotReadyInvalidLocalAndRemote_PerPackageAsync()
        {
            const string otherPackage = "OtherPackage";

            Assert.AreEqual(AssetLocationState.Invalid, _utility.GetLocationState(" \t"),
                "调用方已经给出空白地址时，无须初始化清单也能确定它无效");
            Assert.AreEqual(AssetLocationState.PackageNotReady, _utility.GetLocationState("logo"));
            Assert.AreEqual(0, _provider.CheckLocationCalls,
                "Core 状态未 Ready 时不应把不可查询的请求下沉到 Adapter");

            _utility.MarkPackagesPending(new[] { Package });
            Assert.AreEqual(AssetLocationState.PackageNotReady, _utility.GetLocationState("logo"));
            _utility.AbandonPendingPackages();
            Assert.AreEqual(AssetInitState.Failed, _utility.GetInitState(Package).CurrentValue);
            Assert.AreEqual(AssetLocationState.PackageNotReady, _utility.GetLocationState("logo"));

            await MakeReady();
            _provider.LocationValid = false;
            _provider.NeedDownload = true; // 无效地址不应继续读取下载缓存。
            Assert.AreEqual(AssetLocationState.Invalid, _utility.GetLocationState("logo"));
            Assert.AreEqual(1, _provider.CheckLocationCalls);
            Assert.AreEqual(0, _provider.NeedDownloadCalls);

            _provider.LocationValid = true;
            _provider.NeedDownload = false;
            Assert.AreEqual(AssetLocationState.AvailableLocally, _utility.GetLocationState("logo"));

            _provider.NeedDownload = true;
            Assert.AreEqual(AssetLocationState.RequiresDownload, _utility.GetLocationState("logo"));
            Assert.AreEqual(Package, _provider.LastQueriedPackage);

            int checksBeforeOtherPackage = _provider.CheckLocationCalls;
            Assert.AreEqual(
                AssetLocationState.PackageNotReady,
                _utility.GetLocationState(otherPackage, "logo"),
                "默认包 Ready 不能让尚未初始化的另一个包看起来可查询");
            Assert.AreEqual(checksBeforeOtherPackage, _provider.CheckLocationCalls);

            var otherInitialization = _provider.PlanInitialization();
            var initializing = _utility.Initialize(otherPackage);
            await otherInitialization.Started.Task;
            Assert.AreEqual(AssetLocationState.PackageNotReady, _utility.GetLocationState(otherPackage, "logo"));
            otherInitialization.Release.TrySetResult();
            await initializing;
            Assert.AreEqual(AssetLocationState.RequiresDownload, _utility.GetLocationState(otherPackage, "logo"));
            Assert.AreEqual(otherPackage, _provider.LastQueriedPackage);
        }

        private async UniTask EmptyLocation_IsReportedThroughLoggingSeam_BeforeProviderWorkAsync()
        {
            LogAssert.Expect(LogType.Warning, "[AssetUtility] 资源地址（location）为空。");
            var sink = new CapturingSink();
            Log.AddSink(sink);
            IAssetHandle<GameObject> handle;
            try
            {
                handle = await _utility.Load<GameObject>(string.Empty);
            }
            finally
            {
                Log.RemoveSink(sink);
            }

            Assert.IsNull(handle);
            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, sink.Entries[0].Level);
            Assert.AreEqual(nameof(AssetUtility), sink.Entries[0].Category);
            Assert.AreEqual("资源地址（location）为空。", sink.Entries[0].Message);
            Assert.AreSame(_utility, sink.Entries[0].Context,
                "资源输入守卫应携带产生诊断的 Utility，便于 Console 定位并让外部 sink 保留上下文");
            Assert.AreEqual(0, _provider.InitializeCalls,
                "空地址应在 Core Interface 边界 fail-fast，不应触发包初始化或下沉到 Adapter");
        }

        private void AssertInitializationFailureEntry(CapturingSink sink, Exception expected)
        {
            Assert.AreEqual(1, sink.Entries.Count);
            var entry = sink.Entries[0];
            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual(nameof(AssetUtility), entry.Category);
            Assert.AreSame(expected, entry.Exception,
                "初始化失败的根异常必须穿过日志 Seam，不能只剩格式化后的 message");
            Assert.AreSame(_utility, entry.Context);
        }

        private async UniTask MakeReady()
        {
            var gate = _provider.PlanInitialization();
            var initializing = _utility.Initialize(Package);
            await gate.Started.Task;
            gate.Release.TrySetResult();
            await initializing;
            Assert.AreEqual(AssetInitState.Ready, _utility.GetInitState(Package).CurrentValue);
        }

        private static async UniTask ExpectCanceled(UniTask task)
        {
            bool canceled = false;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert.IsTrue(canceled, "应向取消等待的调用者保留 OperationCanceledException");
        }

        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private sealed class ControllableAssetProvider : IAssetProvider
        {
            internal sealed class InitializationGate
            {
                public readonly Exception Failure;
                public readonly UniTaskCompletionSource Started = new();
                public readonly UniTaskCompletionSource Release = new();
                public CancellationToken OwnerToken;
                public int CompletionThread = -1;

                public InitializationGate(Exception failure) => Failure = failure;
            }

            internal sealed class AssetLoadGate
            {
                public readonly IAssetHandle<UnityEngine.Object> Result;
                public readonly Exception Failure;
                public readonly bool IgnoreCancellation;
                public readonly UniTaskCompletionSource Started = new();
                public readonly UniTaskCompletionSource Release = new();

                public AssetLoadGate(
                    IAssetHandle<UnityEngine.Object> result,
                    Exception failure,
                    bool ignoreCancellation)
                {
                    Result = result;
                    Failure = failure;
                    IgnoreCancellation = ignoreCancellation;
                }
            }

            internal sealed class MaintenanceGate
            {
                public readonly string Kind;
                public readonly Exception Failure;
                public readonly UniTaskCompletionSource Started = new();
                public readonly UniTaskCompletionSource Release = new();

                public MaintenanceGate(string kind, Exception failure)
                {
                    Kind = kind;
                    Failure = failure;
                }
            }

            private readonly HashSet<string> _readyPackages = new();
            private readonly Queue<InitializationGate> _initializations = new();
            private readonly Queue<MaintenanceGate> _maintenance = new();
            private readonly Queue<AssetLoadGate> _assetLoads = new();
            private int _activeMaintenance;

            public readonly List<string> StartedMaintenanceKinds = new();
            public int InitializeCalls { get; private set; }
            public CancellationToken InitializeOwnerToken { get; private set; }
            public int MaxActiveMaintenance { get; private set; }
            public IReadOnlyList<string> ReceivedTags { get; private set; }
            public IReadOnlyList<string> ReceivedLocations { get; private set; }
            public bool Disposed { get; private set; }
            public bool ThrowOnDispose { get; set; }
            public bool LocationValid { get; set; }
            public bool NeedDownload { get; set; }
            public int CheckLocationCalls { get; private set; }
            public int NeedDownloadCalls { get; private set; }
            public string LastQueriedPackage { get; private set; }
            public AssetProviderConfig ReceivedInitializationConfig { get; private set; }
            public int LastDownloaderMaxConcurrent { get; private set; }
            public int LastDownloaderRetries { get; private set; }
            public int AssetLoadCalls { get; private set; }
            public IAssetHandle<UnityEngine.Object> AssetResult { get; set; }
            public ISceneHandle SceneResult { get; set; }
            public IAssetDownloader DownloaderResult { get; set; }
            public string TextResult { get; set; }
            public byte[] BytesResult { get; set; }
            public Exception NextAssetLoadFailure { get; set; }
            public bool CompleteLoadsOnThreadPool { get; set; }
            public List<int> LoadCompletionThreads { get; } = new();

#if UNITY_EDITOR
            public Func<bool> SimulateOffline { get; set; }
#endif

            public async UniTask InitializeAsync(
                string packageName,
                AssetPlayMode mode,
                AssetProviderConfig config,
                CancellationToken ct)
            {
                if (_initializations.Count == 0)
                    throw new InvalidOperationException("No initialization gate planned.");

                var gate = _initializations.Dequeue();
                InitializeCalls++;
                InitializeOwnerToken = ct;
                gate.OwnerToken = ct;
                ReceivedInitializationConfig = config;
                gate.Started.TrySetResult();
                await gate.Release.Task.AttachExternalCancellation(ct);
                gate.CompletionThread = Thread.CurrentThread.ManagedThreadId;
                if (gate.Failure != null) throw gate.Failure;
                _readyPackages.Add(packageName);
            }

            public bool IsPackageReady(string packageName) => _readyPackages.Contains(packageName);

            public InitializationGate PlanInitialization(Exception failure = null)
            {
                var gate = new InitializationGate(failure);
                _initializations.Enqueue(gate);
                return gate;
            }

            public MaintenanceGate PlanMaintenance(string kind, Exception failure = null)
            {
                var gate = new MaintenanceGate(kind, failure);
                _maintenance.Enqueue(gate);
                return gate;
            }

            public AssetLoadGate PlanAssetLoad(
                IAssetHandle<UnityEngine.Object> result = null,
                Exception failure = null,
                bool ignoreCancellation = false)
            {
                var gate = new AssetLoadGate(result, failure, ignoreCancellation);
                _assetLoads.Enqueue(gate);
                return gate;
            }

            public UniTask ClearCacheAsync(
                string packageName,
                AssetCacheClearMode mode,
                CancellationToken ct)
                => RunMaintenance("clear", ct);

            public UniTask ClearCacheByTagsAsync(
                string packageName,
                IReadOnlyList<string> tags,
                CancellationToken ct)
            {
                ReceivedTags = Copy(tags);
                return RunMaintenance("tags", ct);
            }

            public UniTask ClearCacheByLocationsAsync(
                string packageName,
                IReadOnlyList<string> locations,
                CancellationToken ct)
            {
                ReceivedLocations = Copy(locations);
                return RunMaintenance("locations", ct);
            }

            public UniTask UnloadUnusedAssetsAsync(string packageName, CancellationToken ct)
                => RunMaintenance("unload", ct);

            private async UniTask RunMaintenance(string expectedKind, CancellationToken ct)
            {
                if (_maintenance.Count == 0)
                    throw new InvalidOperationException($"No maintenance gate planned for '{expectedKind}'.");

                var gate = _maintenance.Dequeue();
                if (gate.Kind != expectedKind)
                    throw new InvalidOperationException($"Expected maintenance '{gate.Kind}', got '{expectedKind}'.");

                StartedMaintenanceKinds.Add(expectedKind);
                _activeMaintenance++;
                MaxActiveMaintenance = Math.Max(MaxActiveMaintenance, _activeMaintenance);
                gate.Started.TrySetResult();
                try
                {
                    await gate.Release.Task.AttachExternalCancellation(ct);
                    if (gate.Failure != null) throw gate.Failure;
                }
                finally
                {
                    _activeMaintenance--;
                }
            }

            private static string[] Copy(IReadOnlyList<string> items)
            {
                var result = new string[items.Count];
                for (int i = 0; i < items.Count; i++) result[i] = items[i];
                return result;
            }

            public async UniTask<IAssetHandle<UnityEngine.Object>> LoadAssetAsync(
                string packageName, string locationOrGuid, bool byGuid, Type type, CancellationToken ct)
            {
                AssetLoadCalls++;
                if (_assetLoads.Count > 0)
                {
                    var gate = _assetLoads.Dequeue();
                    gate.Started.TrySetResult();
                    if (gate.IgnoreCancellation)
                        await gate.Release.Task;
                    else
                        await gate.Release.Task.AttachExternalCancellation(ct);
                    if (gate.Failure != null) throw gate.Failure;
                    return gate.Result;
                }

                await SwitchLoadCompletionThreadIfRequested();
                if (NextAssetLoadFailure != null)
                {
                    var failure = NextAssetLoadFailure;
                    NextAssetLoadFailure = null;
                    throw failure;
                }
                return AssetResult;
            }

            public async UniTask<ISceneHandle> LoadSceneAsync(
                string packageName, string location, LoadSceneMode mode, bool suspendLoad, CancellationToken ct)
            {
                await SwitchLoadCompletionThreadIfRequested();
                return SceneResult;
            }

            public async UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct)
            {
                await SwitchLoadCompletionThreadIfRequested();
                return TextResult;
            }

            public async UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct)
            {
                await SwitchLoadCompletionThreadIfRequested();
                return BytesResult;
            }

            private async UniTask SwitchLoadCompletionThreadIfRequested()
            {
                if (CompleteLoadsOnThreadPool)
                    await UniTask.SwitchToThreadPool();
                LoadCompletionThreads.Add(Thread.CurrentThread.ManagedThreadId);
            }

            public bool CheckLocationValid(string packageName, string location)
            {
                CheckLocationCalls++;
                LastQueriedPackage = packageName;
                return LocationValid;
            }

            public bool IsNeedDownload(string packageName, string location)
            {
                NeedDownloadCalls++;
                LastQueriedPackage = packageName;
                return NeedDownload;
            }
            public string GetPackageVersion(string packageName) => _readyPackages.Contains(packageName) ? "test" : null;
            public IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags, int maxConcurrent, int retries)
                => DownloaderResult;
            public IAssetDownloader CreateAllDownloader(string packageName, int maxConcurrent, int retries)
            {
                LastDownloaderMaxConcurrent = maxConcurrent;
                LastDownloaderRetries = retries;
                return DownloaderResult;
            }
            public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations, int maxConcurrent, int retries)
                => DownloaderResult;
            public void Dispose()
            {
                Disposed = true;
                if (ThrowOnDispose)
                    throw new InvalidOperationException("asset-provider-dispose-probe");
            }
        }
    }
}
