using System;
using System.Collections;
using System.Collections.Generic;
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
    /// 通过可控 provider 验证 AssetUtility 的包级异步所有权；不依赖 YooAsset 时序或真实文件系统速度。
    /// </summary>
    public sealed class AssetOperationCoordinationTests
    {
        private const string Package = "CoordinationTestPackage";

        private GameObject _root;
        private AssetUtility _utility;
        private ControllableAssetProvider _provider;

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
        public IEnumerator LocationState_DistinguishesNotReadyInvalidLocalAndRemote_PerPackage()
            => LocationState_DistinguishesNotReadyInvalidLocalAndRemote_PerPackageAsync().ToCoroutine();

        [UnityTest]
        public IEnumerator EmptyLocation_IsReportedThroughLoggingSeam_BeforeProviderWork()
            => EmptyLocation_IsReportedThroughLoggingSeam_BeforeProviderWorkAsync().ToCoroutine();

        private async UniTask SetUpAsync()
        {
            _root = new GameObject(nameof(AssetOperationCoordinationTests));

            var contextObject = new GameObject("Context");
            contextObject.transform.SetParent(_root.transform);
            contextObject.AddComponent<MonoGameContextBase>();

            var utilityObject = new GameObject("AssetUtility");
            utilityObject.transform.SetParent(contextObject.transform);
            _utility = utilityObject.AddComponent<AssetUtility>();

            _provider = new ControllableAssetProvider();
            _utility.ReplaceProviderForTesting(_provider);
            _utility.Configure(Package, new AssetProviderConfig(), AssetPlayMode.EditorSimulate);
            await UniTask.Yield();
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

                public InitializationGate(Exception failure) => Failure = failure;
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
            private int _activeMaintenance;

            public readonly List<string> StartedMaintenanceKinds = new();
            public int InitializeCalls { get; private set; }
            public CancellationToken InitializeOwnerToken { get; private set; }
            public int MaxActiveMaintenance { get; private set; }
            public IReadOnlyList<string> ReceivedTags { get; private set; }
            public IReadOnlyList<string> ReceivedLocations { get; private set; }
            public bool Disposed { get; private set; }
            public bool LocationValid { get; set; }
            public bool NeedDownload { get; set; }
            public int CheckLocationCalls { get; private set; }
            public int NeedDownloadCalls { get; private set; }
            public string LastQueriedPackage { get; private set; }

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
                gate.Started.TrySetResult();
                await gate.Release.Task.AttachExternalCancellation(ct);
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

            public UniTask<IAssetHandle<UnityEngine.Object>> LoadAssetAsync(
                string packageName, string locationOrGuid, bool byGuid, Type type, CancellationToken ct)
                => UniTask.FromResult<IAssetHandle<UnityEngine.Object>>(null);

            public UniTask<ISceneHandle> LoadSceneAsync(
                string packageName, string location, LoadSceneMode mode, bool suspendLoad, CancellationToken ct)
                => UniTask.FromResult<ISceneHandle>(null);

            public UniTask<string> LoadTextAsync(string packageName, string location, CancellationToken ct)
                => UniTask.FromResult<string>(null);

            public UniTask<byte[]> LoadBytesAsync(string packageName, string location, CancellationToken ct)
                => UniTask.FromResult<byte[]>(null);

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
            public IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags, int maxConcurrent, int retries) => null;
            public IAssetDownloader CreateAllDownloader(string packageName, int maxConcurrent, int retries) => null;
            public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations, int maxConcurrent, int retries) => null;
            public void Dispose() => Disposed = true;
        }
    }
}
