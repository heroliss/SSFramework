using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Utility;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Framework
{
    /// <summary>
    /// <see cref="IAssetUtility"/> 的默认实现，挂在场景中的 Context 节点上。
    ///
    /// 职责边界：
    /// - 持有场景运行配置，并在 Start 编排标记为自动初始化的包；
    /// - 管理多个 package 的初始化状态、失败异常和等待入口；
    /// - 提供类型化加载 API 与加载结果的类型校验（<see cref="CastHandle{T}"/>）；
    /// - 把具体资源库的初始化、加载（含"Component 请求解析到 GameObject prefab 再取组件"）、handle 包装和下载器适配委托给 provider。
    ///
    /// 每次 Load 都返回独立 handle，调用方可以手动 Dispose；业务层通常通过 <see cref="DisposableBag"/> 托管。
    /// </summary>
    [DisallowMultipleComponent]
    public class AssetUtility : MonoUtilityBase, IAssetUtility
    {
        private sealed class InitAttempt
        {
            public readonly UniTaskCompletionSource Done = new();
            public Exception Error;
        }

        private sealed class PackageState
        {
            public readonly string Name;
            public readonly ReactiveProperty<AssetInitState> State = new(AssetInitState.Idle);
            public readonly AssetPackageOperationLane MaintenanceOperations = new();
            public InitAttempt Attempt = new();

            public PackageState(string name) => Name = name;
        }

        [SerializeField]
        [InspectorName("资源运行配置")]
        [Tooltip("资源包、运行模式、CDN、下载器与加密设置。场景路径会在 Start 自动初始化；代码引导若在 Start 前调用 Configure，则以代码配置为准。")]
        private AssetRuntimeSettings _settings = new();

        private readonly Dictionary<string, PackageState> _packages = new();
        private readonly HashSet<string> _autoInitializePackages = new();
        private IAssetProvider _provider;
        private CancellationTokenSource _disposeCts;
        private CancellationTokenSource _startupCts;
        private string _defaultPackageName = "DefaultPackage";
        private AssetProviderConfig _config = new();
        private string _configurationError;
        private bool _startupClaimed;
        private bool _configurationErrorReported;
        private bool _disposedByDestroy;

        // 无默认包（DefaultPackageName 留空）时恒为 false——没有「默认包」可言。
        public bool IsInitialized
        {
            get
            {
                ThrowIfDisposed();
                return !string.IsNullOrWhiteSpace(_defaultPackageName) &&
                       GetState(_defaultPackageName).State.Value == AssetInitState.Ready;
            }
        }
        public AssetPlayMode CurrentPlayMode { get; private set; } = AssetPlayMode.EditorSimulate;
        public ReadOnlyReactiveProperty<AssetInitState> InitState
        {
            get
            {
                ThrowIfDisposed();
                return GetState(_defaultPackageName).State;
            }
        }

        /// <summary>
        /// 当前组件的 Inspector 场景配置。集合返回结构只读视图；代码引导请在 <c>Start</c> 前调用
        /// <see cref="Configure"/> 提交独立运行快照，它不会反向改写本属性。
        /// </summary>
        public AssetRuntimeSettings Settings => _settings ??= new AssetRuntimeSettings();

#if UNITY_EDITOR
        /// <summary>原生 Inspector 的只读运行时快照；不参与资源状态机。</summary>
        internal IEnumerable<string> EditorDiagnostics
        {
            get
            {
                yield return $"运行模式：{CurrentPlayMode}";
                yield return $"默认包：{(string.IsNullOrEmpty(_defaultPackageName) ? "（无）" : _defaultPackageName)}";
                foreach (var pair in _packages)
                {
                    AssetInitState state = pair.Value.State.Value;
                    string detail = state switch
                    {
                        AssetInitState.Failed when pair.Value.Attempt.Error != null => pair.Value.Attempt.Error.Message,
                        AssetInitState.Ready => $"版本 {GetPackageVersion(pair.Key) ?? "?"}",
                        _ => string.Empty,
                    };
                    yield return string.IsNullOrEmpty(detail)
                        ? $"{pair.Key}：{state}"
                        : $"{pair.Key}：{state} — {detail}";
                }
            }
        }

        // 编辑器「模拟断网」开关：开启后 provider 的远端请求走不可达地址，使远端拉取（初始化 / 下载 / 需下载的 Load）失败。
        // 序列化且置于诊断折叠组外——它是可在进入 Play 前设置的「控制开关」而非「只读诊断」：已 Ready 的包不会因开关回退，
        // 故只有在包初始化前开启才能让其初始化失败。用 RP<bool> 让 Inspector 与订阅方实时同步。
        [SerializeField]
        [Tooltip("开启 = 远端请求走不可达地址，远端拉取失败。仅编辑器 / 仅远端模式有意义；进 Play 前开启才能让初始化失败，已 Ready 的包不受影响。")]
        private RP<bool> _simulateOffline = new(false);

        ReadOnlyReactiveProperty<bool> IAssetUtility.SimulateOffline
        {
            get
            {
                ThrowIfDisposed();
                return _simulateOffline;
            }
        }

        void IAssetUtility.SetSimulateOffline(bool on)
        {
            ThrowIfDisposed();
            _simulateOffline.Value = on;
        }
#endif

        protected override void Awake()
        {
            base.Awake();
            _disposeCts = new CancellationTokenSource();
            _provider = AssetProviderFactory.CreateDefault();
#if UNITY_EDITOR
            _provider.SimulateOffline = () => _simulateOffline.CurrentValue; // 把开关接到 provider（实时读取当前值）
#endif
            // Awake 只应用设置并建立可观察状态；真正的远端 / 清单操作推迟到 Start，给代码引导保留
            // “AddComponent 后立即 Configure”的确定窗口，也避免 AddComponent 在 Awake 中抢先联网。
            ApplySettings(Settings);
        }

        private void Start()
        {
            if (_startupClaimed || _disposedByDestroy) return;
            _startupClaimed = true;

            CancellationToken token = _disposeCts.Token;
            IGameContext context = ((IHasGameContext)this).Context;
            if (context != null)
            {
                _startupCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _disposeCts.Token,
                    context.CancellationToken);
                token = _startupCts.Token;
            }

            RunAutoInitializationAsync(token).Forget(ex => Log.Error(
                "资源系统自动初始化批次异常停止。",
                ex,
                nameof(AssetUtility),
                this));
        }

        protected override void OnDestroy()
        {
            _disposedByDestroy = true;
            try
            {
                CancelAndDispose(ref _startupCts, "自动初始化批次");
                CancelAndDispose(ref _disposeCts, "资源 Utility 生命周期");
                DisposeProviderSafely();
                DisposePackageStatesSafely();

#if UNITY_EDITOR
                DisposeStateSafely(_simulateOffline, "模拟断网状态流");
#endif
            }
            finally
            {
                // Provider / 状态订阅属于可替换边界；任何一个坏 Dispose 都不能让 Mono 仍留在 Context 中。
                base.OnDestroy();
            }
        }

        private void DisposeProviderSafely()
        {
            IAssetProvider provider = _provider;
            _provider = null;
            if (provider == null) return;
            try
            {
                provider.Dispose();
            }
            catch (Exception exception)
            {
                Log.Error(
                    "资源 Provider 在释放期间抛出异常；状态流与 Context 清理仍会继续。",
                    exception,
                    nameof(AssetUtility),
                    this);
            }
        }

        private void DisposePackageStatesSafely()
        {
            try
            {
                foreach (PackageState state in _packages.Values)
                {
                    try
                    {
                        state.Attempt.Done.TrySetCanceled();
                    }
                    catch (Exception exception)
                    {
                        ReportCleanupFailure($"包“{state.Name}”的初始化等待者", exception);
                    }

                    DisposeStateSafely(state.State, $"包“{state.Name}”的初始化状态流");
                }
            }
            finally
            {
                _packages.Clear();
            }
        }

        private void DisposeStateSafely<T>(ReactiveProperty<T> state, string owner)
        {
            if (state == null) return;
            try
            {
                // 锁定公开长期源的完结契约，不把 R3 无参 Dispose 的默认值变成隐藏知识。
                state.Dispose(callOnCompleted: true);
            }
            catch (Exception exception)
            {
                ReportCleanupFailure(owner, exception);
            }
        }

        private void ReportCleanupFailure(string owner, Exception exception)
        {
            Log.Error(
                $"{owner}在释放期间抛出异常；其余资源清理仍会继续。",
                exception,
                nameof(AssetUtility),
                this);
        }

        private void CancelAndDispose(ref CancellationTokenSource source, string owner)
        {
            CancellationTokenSource releasing = source;
            source = null;
            if (releasing == null) return;
            try { releasing.Cancel(); }
            catch (Exception exception)
            {
                // 取消意图已经成立；第三方或业务注册的坏回调不能截断 provider、状态流与容器清理。
                Log.Error(
                    $"{owner}的取消回调执行失败；其余资源清理将继续。",
                    exception,
                    nameof(AssetUtility),
                    this);
            }
            finally
            {
                releasing.Dispose();
            }
        }

        /// <summary>
        /// 在初始化前写入运行时配置、默认包名与运行模式。它面向代码引导路径：在 <c>Start</c> 前调用会明确
        /// 接管启动过程，抑制 Inspector 设置的自动初始化；随后由入口代码调用 <see cref="Initialize"/> 与
        /// <c>LoadScene</c>。配置与其集合会在调用时深拷贝，之后修改原 DTO 不会热换本 Utility。
        /// 场景常规路径不必调用，直接在 Play 前编辑 <see cref="Settings"/> 即可。
        /// <para>运行模式在此即写入 <see cref="CurrentPlayMode"/>（而非等到 <see cref="InitializePackageAsync"/>）：
        /// 某些包关闭自动初始化、延迟到业务显式 <c>Initialize</c> 触发时，仍能用正确模式初始化，而不是回落到默认值。</para>
        /// </summary>
        public void Configure(string defaultPackageName, AssetProviderConfig config, AssetPlayMode mode)
        {
            ThrowIfDisposed();
            _startupClaimed = true;
            _autoInitializePackages.Clear();
            _configurationError = null;
            _configurationErrorReported = false;
            ApplyConfiguration(defaultPackageName, config, mode);
        }

        private void ApplyConfiguration(string defaultPackageName, AssetProviderConfig config, AssetPlayMode mode)
        {
            _defaultPackageName = defaultPackageName?.Trim() ?? string.Empty;
            _config = (config ?? new AssetProviderConfig()).Snapshot();
            CurrentPlayMode = mode;
            if (!string.IsNullOrWhiteSpace(_defaultPackageName))
                GetState(_defaultPackageName);
        }

        private void ApplySettings(AssetRuntimeSettings settings)
        {
            _settings = settings ?? new AssetRuntimeSettings();
            _autoInitializePackages.Clear();
            foreach (string packageName in _settings.EnumeratePackageNames())
                if (_settings.ShouldAutoInitialize(packageName))
                    _autoInitializePackages.Add(packageName);
            _configurationError = _settings.GetConfigError();
            _configurationErrorReported = false;
            ApplyConfiguration(
                _settings.DefaultPackageName,
                _settings.ToProviderConfig(),
                _settings.ActualPlayMode);
        }

        /// <summary>旧场景兼容适配器使用：让旧配置接管新 Utility 的启动，并复用同一批量初始化实现。</summary>
        internal async UniTask ConfigureAndAutoInitialize(AssetRuntimeSettings settings, CancellationToken token)
        {
            ThrowIfDisposed();
            _startupClaimed = true;
            ApplySettings(settings);
            await RunAutoInitializationAsync(token);
        }

#if UNITY_EDITOR
        /// <summary>Editor 迁移器写入深拷贝设置；只改序列化数据，不在 Edit Mode 启动资源操作。</summary>
        internal void ReplaceSettingsForEditorMigration(AssetRuntimeSettings settings)
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("资源配置迁移只能在 Edit Mode 执行。");
            _settings = settings ?? new AssetRuntimeSettings();
        }
#endif

        /// <summary>
        /// 用可控实现替换 Awake 创建的默认 provider，供资源状态机的契约测试使用。
        /// 只能在任何包开始初始化前调用，避免测试缝成为运行时热替换入口。
        /// </summary>
        internal void ReplaceProviderForTesting(IAssetProvider provider)
        {
            ThrowIfDisposed();
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            foreach (var state in _packages.Values)
            {
                if (state.State.Value != AssetInitState.Idle)
                    throw new InvalidOperationException(
                        "[AssetUtility] 测试 Provider 只能在任何资源包开始初始化前替换。");
            }

            _provider?.Dispose();
            _provider = provider;
#if UNITY_EDITOR
            _provider.SimulateOffline = () => _simulateOffline.CurrentValue;
#endif
        }

        /// <summary>
        /// 初始化单个 package。物理初始化由 utility 生命周期持有；调用者 token 只取消自己的等待，
        /// 不会把仍在底层运行的初始化误标成 Failed，也不会为同包启动第二份原生 operation。
        /// 普通失败只记录到该 package 的状态，不向外抛出，避免阻断其他包初始化；等待取消保持
        /// <see cref="OperationCanceledException"/>。
        /// </summary>
        internal async UniTask InitializePackageAsync(string packageName, AssetPlayMode mode, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            // 所有包共享同一 PlayMode；CurrentPlayMode 反映最近一次实际初始化使用的模式，供诊断展示。
            CurrentPlayMode = mode;
            packageName = NormalizePackageName(packageName);
            var state = GetState(packageName);

            if (state.State.Value == AssetInitState.Ready) return;
            var waitingAttempt = state.Attempt;
            if (state.State.Value != AssetInitState.Initializing)
            {
                // 走到这里只剩 Idle / Pending / Failed：启动唯一 owner。Pending 复用 fresh TCS；Failed 重建后才能重试。
                if (state.State.Value == AssetInitState.Failed)
                {
                    state.Attempt = new InitAttempt();
                }

                var attempt = state.Attempt;
                // owner 可能同步完成并在 Failed 订阅中立即触发下一代重试；本调用必须始终等待自己启动的这一代。
                waitingAttempt = attempt;
                state.State.Value = AssetInitState.Initializing;
                var provider = _provider;
                var config = _config;
                var ownerToken = _disposeCts.Token;
                RunInitializationOwner(state, attempt, packageName, mode, provider, config, ownerToken)
                    .Forget(ex => Log.Error(
                        $"资源包“{packageName}”的初始化所有者（owner）异常停止。",
                        ex,
                        nameof(AssetUtility),
                        this));
            }

            // 每个调用者只等待共享结果。取消这个 await 不触碰 owner、不改变 InitState；owner 最终仍会落到 Ready / Failed。
            using var linked = LinkDispose(token, out var waitToken);
            await waitingAttempt.Done.Task.AttachExternalCancellation(waitToken);
        }

        private async UniTask RunInitializationOwner(
            PackageState state,
            InitAttempt attempt,
            string packageName,
            AssetPlayMode mode,
            IAssetProvider provider,
            AssetProviderConfig config,
            CancellationToken ownerToken)
        {
            try
            {
                // 这里刻意不用任一调用者 token。provider 的物理操作归 utility 生命周期所有；只有 OnDestroy 才取消 owner。
                await provider.InitializeAsync(packageName, mode, config.Snapshot(), ownerToken);
                if (_disposedByDestroy) return;
                state.State.Value = AssetInitState.Ready;
                attempt.Done.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                if (_disposedByDestroy) return; // OnDestroy 已统一取消 TCS 并 Dispose 状态流。
                // 失败 / 取消都让本 attempt 的 Done 以「成功完成」收尾、错误另存——
                // 若给 Done 挂异常，而失败后又没人 await 它（EnsureInitialized 在 Failed 分支直接抛 Error、不 await），
                // UniTask 会在该 Task 被回收时把它当 unobserved exception 再报一条。等待方醒来后读取 attempt.Error 即可。
                attempt.Error = ex;
                state.State.Value = AssetInitState.Failed;
                // 完成 owner 捕获的 attempt，而不是同步 State 订阅回调可能新建的重试 attempt。
                attempt.Done.TrySetResult();
                Log.Info(
                    $"资源包“{packageName}”初始化已取消。",
                    nameof(AssetUtility),
                    this);
            }
            catch (Exception ex)
            {
                if (_disposedByDestroy) return;
                attempt.Error = ex;
                state.State.Value = AssetInitState.Failed;
                attempt.Done.TrySetResult(); // 同上：失败经 attempt.Error 传递，不给 TCS 挂异常
                Log.Error(
                    $"Package '{packageName}' 初始化失败（模式 {mode}）。\n{InitFailureHint(mode)}",
                    ex,
                    nameof(AssetUtility),
                    this);
            }
        }

        // 按运行模式给出最可能的失败原因。笼统地说「没构建/部署」会误导排查——例如 Host 下资源其实都对、
        // 只是本地 CDN 服务没起（或端口和配置不一致），清单根本拉不到，此时该提示去起服务 / 对端口，而不是去重新构建。
        private static string InitFailureHint(AssetPlayMode mode) => mode switch
        {
            AssetPlayMode.Host or AssetPlayMode.Web =>
                "拉远端清单失败：确认已①构建 ②部署资源，且远端 CDN 可达——本地联调还需③启动本地 CDN 服务，且服务端口与当前生效配置的 CDN 主地址一致（场景路径来自 Settings，代码路径来自 Configure）。开发期可改回 EditorSimulate 免构建。",
            AssetPlayMode.Offline =>
                "读内置清单失败：确认已构建、且把 bundle 内置进首包（首包 Tags）。开发期可改回 EditorSimulate 免构建。",
            _ =>
                "EditorSimulate 一般无需构建；若仍失败，检查资源收集器 / 包名配置。",
        };

        /// <summary>配置缺失导致默认包无法开始初始化时使用，让等待默认包的业务能收到明确异常。</summary>
        internal void FailDefaultInitialization(Exception exception)
        {
            if (string.IsNullOrWhiteSpace(_defaultPackageName)) return; // 无默认包则无从置 Failed（业务本就该用 packageName 重载）
            var state = GetState(_defaultPackageName);
            var ex = exception ?? new InvalidOperationException("[AssetUtility] 资源初始化失败。");
            var attempt = state.Attempt;
            attempt.Error = ex;
            state.State.Value = AssetInitState.Failed;
            attempt.Done.TrySetResult(); // 见 InitializePackageAsync：失败经 Error 传递，不给 TCS 挂异常
        }

        private async UniTask RunAutoInitializationAsync(CancellationToken token)
        {
            ReportConfigurationError();

            var packages = new List<string>(_autoInitializePackages.Count);
            foreach (string packageName in Settings.EnumeratePackageNames())
                if (_autoInitializePackages.Contains(packageName)) packages.Add(packageName);
            MarkPackagesPending(packages);

            try
            {
                foreach (string packageName in packages)
                {
                    if (token.IsCancellationRequested) break;
                    // 配置错误只封锁默认便捷入口；其它显式命名包仍可初始化，避免一个默认指针错误拖垮全部包。
                    if (_configurationError != null && packageName == _defaultPackageName) continue;
                    await InitializePackageAsync(packageName, CurrentPlayMode, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 调用方只取消当前批次的等待；已经开始的物理初始化仍由 Utility 生命周期持有。
            }
            finally
            {
                AbandonPendingPackages();
            }
        }

        private void ReportConfigurationError()
        {
            if (_configurationError == null || _configurationErrorReported) return;
            _configurationErrorReported = true;
            var exception = new InvalidOperationException("[AssetUtility] " + _configurationError);
            Log.Error(
                "资源运行配置无效，默认资源包已标记为失败。",
                exception,
                nameof(AssetUtility),
                this);
            FailDefaultInitialization(exception);
        }

        /// <summary>
        /// 把这些包标记为 <see cref="AssetInitState.Pending"/>（仅当前为 <see cref="AssetInitState.Idle"/> 时）。
        /// 由自动初始化批次在「逐个开跑前」统一调用：让「已登记会初始化、但还没轮到」的包
        /// 对并发 Load 表现为「等待」（Pending）而非「未初始化报错」（Idle）——消除批次窗口内的抢跑竞态。
        /// </summary>
        internal void MarkPackagesPending(IEnumerable<string> packageNames)
        {
            ThrowIfDisposed();
            if (packageNames == null) return;
            foreach (var packageName in packageNames)
            {
                if (string.IsNullOrWhiteSpace(packageName)) continue;
                var state = GetState(packageName);
                if (state.State.Value == AssetInitState.Idle)
                    state.State.Value = AssetInitState.Pending;
            }
        }

        /// <summary>
        /// 把仍停在 <see cref="AssetInitState.Pending"/>（已登记、但批次初始化没轮到就被中止）的包置 <see cref="AssetInitState.Failed"/>，
        /// 并唤醒其等待者。由自动初始化批次结束（含被取消）时兜底调用：
        /// 否则这些包的初始化 attempt 永不完成、后续 <c>EnsureInitialized</c> 会无限挂起——与「Pending 等待 / Idle 报错」契约相悖。
        /// 置 Failed（而非退回 Idle）让既有等待者醒来即拿到清晰异常；之后业务可 <see cref="Initialize"/> 重试。
        /// </summary>
        internal void AbandonPendingPackages()
        {
            if (_disposedByDestroy) return;
            foreach (var state in _packages.Values)
            {
                if (state.State.Value != AssetInitState.Pending) continue;
                var attempt = state.Attempt;
                attempt.Error = new InvalidOperationException(
                    $"[AssetUtility] 包 '{state.Name}' 的初始化在开始前被中止；如需加载请重新 Initialize(\"{state.Name}\")。");
                state.State.Value = AssetInitState.Failed;
                attempt.Done.TrySetResult(); // 见 InitializePackageAsync：失败经 Error 传递，不给 TCS 挂异常
            }
        }

        public ReadOnlyReactiveProperty<AssetInitState> GetInitState(string packageName)
        {
            ThrowIfDisposed();
            return GetState(NormalizePackageName(packageName)).State;
        }

        public UniTask EnsureInitialized(CancellationToken ct = default)
            => EnsureInitialized(_defaultPackageName, ct);

        public async UniTask EnsureInitialized(string packageName, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var name = RequirePackage(packageName);
            var state = GetState(name);
            var attempt = state.Attempt;
            var current = state.State.Value;
            if (current == AssetInitState.Ready) return;
            if (current == AssetInitState.Failed)
                throw attempt.Error ?? new InvalidOperationException($"[AssetUtility] 资源包“{name}”初始化失败。");
            // 极早的调用可能发生在本组件 Start 之前。只要包已配置为自动初始化，就由首次调用直接启动并加入同一个 owner；
            // Start 随后会幂等加入，避免依赖兄弟组件的 Start 顺序。
            if (current == AssetInitState.Idle && _autoInitializePackages.Contains(name))
            {
                if (_configurationError != null && name == _defaultPackageName)
                {
                    ReportConfigurationError();
                    throw GetState(name).Attempt.Error;
                }

                await InitializePackageAsync(name, CurrentPlayMode, ct);
                state = GetState(name);
                attempt = state.Attempt;
                current = state.State.Value;
                if (current == AssetInitState.Ready) return;
                if (current == AssetInitState.Failed)
                    throw attempt.Error ?? new InvalidOperationException($"[AssetUtility] 资源包“{name}”初始化失败。");
            }

            // Idle = 没配自动初始化、也没人 Initialize 过它：没人会完成当前 attempt，等待只会永久挂起。
            if (current == AssetInitState.Idle)
                throw new InvalidOperationException(
                    $"[AssetUtility] 包 '{name}' 未初始化：它既没开启自动初始化、也没被 Initialize 触发过。" +
                    $"请在 AssetUtility 的资源运行配置里为它开启「自动初始化」，或在加载前先调 Initialize(\"{name}\")。");

            // 剩 Pending（已登记排队）/ Initializing（进行中）：等本 attempt 结束。TCS 失败时也以成功完成收尾
            // （不挂异常，见 InitializePackageAsync），所以醒来后读取该 attempt 捕获的 Error；同步重试不会串台。
            if (!ct.CanBeCanceled)
            {
                await attempt.Done.Task.AttachExternalCancellation(_disposeCts.Token);
            }
            else
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                await attempt.Done.Task.AttachExternalCancellation(linked.Token);
            }

            if (attempt.Error != null)
                throw attempt.Error;
        }

        public async UniTask Initialize(string packageName = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var name = RequirePackage(packageName);
            // 复用场景设置或代码 Configure 写入的 _config 与 CurrentPlayMode。InitializePackageAsync 对 Idle / Pending / Failed 包会（重新）初始化、Ready 直接返回，
            // 故这里幂等；初始化失败不抛、结果写回 InitState（仅「未指定包又无默认包」这种调用方错误会经 RequirePackage 抛）。
            await InitializePackageAsync(name, CurrentPlayMode, ct);
        }

        public UniTask<IAssetHandle<T>> Load<T>(string location, CancellationToken ct = default)
            where T : UnityEngine.Object
            => Load<T>(_defaultPackageName, location, ct);

        public async UniTask<IAssetHandle<T>> Load<T>(string packageName, string location, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(location))
            {
                Log.Warning("资源地址（location）为空。", nameof(AssetUtility), this);
                return null;
            }

            var handle = await LoadInternal(packageName, location, byGuid: false, typeof(T), ct);
            return CastHandle<T>(handle, location);
        }

        public UniTask<IAssetHandle<T>> LoadByGuid<T>(string guid, CancellationToken ct = default)
            where T : UnityEngine.Object
            => LoadByGuid<T>(_defaultPackageName, guid, ct);

        public async UniTask<IAssetHandle<T>> LoadByGuid<T>(string packageName, string guid, CancellationToken ct = default)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(guid))
            {
                Log.Warning("GUID 为空。", nameof(AssetUtility), this);
                return null;
            }

            var handle = await LoadInternal(packageName, guid, byGuid: true, typeof(T), ct);
            return CastHandle<T>(handle, guid);
        }

        public UniTask<ISceneHandle> LoadScene(
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default)
            => LoadScene(_defaultPackageName, location, mode, suspendLoad, ct);

        public async UniTask<ISceneHandle> LoadScene(
            string packageName,
            string location,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool suspendLoad = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Log.Warning("场景地址（location）为空。", nameof(AssetUtility), this);
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadSceneAsync(packageName, location, mode, suspendLoad, lct);
        }

        public UniTask<string> LoadText(string location, CancellationToken ct = default)
            => LoadText(_defaultPackageName, location, ct);

        public async UniTask<string> LoadText(string packageName, string location, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Log.Warning("文本资源地址（location）为空。", nameof(AssetUtility), this);
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadTextAsync(packageName, location, lct);
        }

        public UniTask<byte[]> LoadBytes(string location, CancellationToken ct = default)
            => LoadBytes(_defaultPackageName, location, ct);

        public async UniTask<byte[]> LoadBytes(string packageName, string location, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(location))
            {
                Log.Warning("字节资源地址（location）为空。", nameof(AssetUtility), this);
                return null;
            }

            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadBytesAsync(packageName, location, lct);
        }

        public AssetLocationState GetLocationState(string location)
            => GetLocationState(_defaultPackageName, location);

        public AssetLocationState GetLocationState(string packageName, string location)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            if (string.IsNullOrWhiteSpace(location))
                return AssetLocationState.Invalid;

            // 初始化状态由 Core 持有，是资源查询的唯一真源。未 Ready 时不触碰 Adapter，避免 provider 的 false
            // 再次混入“地址无效 / 已在本地”；调用方需要具体原因时读取同包 GetInitState。
            if (string.IsNullOrWhiteSpace(packageName) ||
                GetState(packageName).State.Value != AssetInitState.Ready)
                return AssetLocationState.PackageNotReady;

            if (!_provider.CheckLocationValid(packageName, location))
                return AssetLocationState.Invalid;

            // 先证实 manifest 地址有效，再做受 Reader/Writer lane 保护的下载缓存快照。
            // 若两步间 Writer 开始或排队，provider 会 fail-fast，而不是拼出跨世代的伪快照。
            return _provider.IsNeedDownload(packageName, location)
                ? AssetLocationState.RequiresDownload
                : AssetLocationState.AvailableLocally;
        }

        public string GetPackageVersion(string packageName = null)
        {
            packageName = NormalizePackageName(packageName);
            if (_provider == null || string.IsNullOrWhiteSpace(packageName)) return null;
            return _provider.GetPackageVersion(packageName);
        }

        public IAssetDownloader CreateTagDownloader(params string[] tags)
        {
            ThrowIfDisposed();
            if (tags == null || tags.Length == 0)
                throw new ArgumentException("至少需要一个标签（tag）。", nameof(tags));
            return CreateTagDownloaderInternal(_defaultPackageName, tags);
        }

        public IAssetDownloader CreateTagDownloader(string packageName, IReadOnlyList<string> tags)
        {
            ThrowIfDisposed();
            if (tags == null || tags.Count == 0)
                throw new ArgumentException("至少需要一个标签（tag）。", nameof(tags));
            return CreateTagDownloaderInternal(packageName, tags);
        }

        public IAssetDownloader CreateAllDownloader()
            => CreateAllDownloader(_defaultPackageName);

        public IAssetDownloader CreateAllDownloader(string packageName)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            RequireReadyForDownloader(packageName);
            return _provider.CreateAllDownloader(packageName, _config.DownloadingMaxNumber, _config.FailedTryAgain);
        }

        public IAssetDownloader CreateLocationDownloader(params string[] locations)
        {
            ThrowIfDisposed();
            if (locations == null || locations.Length == 0)
                throw new ArgumentException("至少需要一个资源地址（location）。", nameof(locations));
            return CreateLocationDownloaderInternal(_defaultPackageName, locations);
        }

        public IAssetDownloader CreateLocationDownloader(string packageName, IReadOnlyList<string> locations)
        {
            ThrowIfDisposed();
            if (locations == null || locations.Count == 0)
                throw new ArgumentException("至少需要一个资源地址（location）。", nameof(locations));
            return CreateLocationDownloaderInternal(packageName, locations);
        }

        public UniTask ClearCache(AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default)
            => ClearCache(_defaultPackageName, mode, ct);

        public async UniTask ClearCache(string packageName, AssetCacheClearMode mode = AssetCacheClearMode.Unused, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            // 清理「未使用」要对照该包当前清单判断哪些 bundle 该删，所以先确保初始化完成。
            await EnsureInitialized(packageName, ct);
            var state = GetState(packageName);
            var provider = _provider;
            using var link = LinkDispose(ct, out var lct);
            await state.MaintenanceOperations.Run(
                $"ClearCache({mode})/{packageName}",
                ownerToken => provider.ClearCacheAsync(packageName, mode, ownerToken),
                _disposeCts.Token,
                lct);
        }

        public UniTask ClearCacheByTags(IReadOnlyList<string> tags, CancellationToken ct = default)
            => ClearCacheByTags(_defaultPackageName, tags, ct);

        public async UniTask ClearCacheByTags(string packageName, IReadOnlyList<string> tags, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (tags == null || tags.Count == 0)
                throw new ArgumentException("至少需要一个标签（tag）。", nameof(tags));
            // 维护操作可能先排队；先冻结参数，避免调用方随后修改原列表而改变尚未启动的清理范围。
            var tagSnapshot = CopyItems(tags);
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            var state = GetState(packageName);
            var provider = _provider;
            using var link = LinkDispose(ct, out var lct);
            await state.MaintenanceOperations.Run(
                $"ClearCacheByTags/{packageName}",
                ownerToken => provider.ClearCacheByTagsAsync(packageName, tagSnapshot, ownerToken),
                _disposeCts.Token,
                lct);
        }

        public UniTask ClearCacheByLocations(IReadOnlyList<string> locations, CancellationToken ct = default)
            => ClearCacheByLocations(_defaultPackageName, locations, ct);

        public async UniTask ClearCacheByLocations(string packageName, IReadOnlyList<string> locations, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (locations == null || locations.Count == 0)
                throw new ArgumentException("至少需要一个资源地址（location）。", nameof(locations));
            var locationSnapshot = CopyItems(locations);
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            var state = GetState(packageName);
            var provider = _provider;
            using var link = LinkDispose(ct, out var lct);
            await state.MaintenanceOperations.Run(
                $"ClearCacheByLocations/{packageName}",
                ownerToken => provider.ClearCacheByLocationsAsync(packageName, locationSnapshot, ownerToken),
                _disposeCts.Token,
                lct);
        }

        public UniTask UnloadUnusedAssets(CancellationToken ct = default)
            => UnloadUnusedAssets(_defaultPackageName, ct);

        public async UniTask UnloadUnusedAssets(string packageName, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            var state = GetState(packageName);
            var provider = _provider;
            using var link = LinkDispose(ct, out var lct);
            await state.MaintenanceOperations.Run(
                $"UnloadUnusedAssets/{packageName}",
                ownerToken => provider.UnloadUnusedAssetsAsync(packageName, ownerToken),
                _disposeCts.Token,
                lct);
        }

        private static string[] CopyItems(IReadOnlyList<string> source)
        {
            var snapshot = new string[source.Count];
            for (int i = 0; i < source.Count; i++) snapshot[i] = source[i];
            return snapshot;
        }

        private IAssetDownloader CreateTagDownloaderInternal(string packageName, IReadOnlyList<string> tags)
        {
            packageName = NormalizePackageName(packageName);
            RequireReadyForDownloader(packageName);
            return _provider.CreateTagDownloader(packageName, tags, _config.DownloadingMaxNumber, _config.FailedTryAgain);
        }

        private IAssetDownloader CreateLocationDownloaderInternal(string packageName, IReadOnlyList<string> locations)
        {
            packageName = NormalizePackageName(packageName);
            RequireReadyForDownloader(packageName);
            return _provider.CreateLocationDownloader(packageName, locations, _config.DownloadingMaxNumber, _config.FailedTryAgain);
        }

        // 三种下载器（tag / 全部 / 按地址）共用：建下载器前必须包已就绪，否则统计不出待下载清单。
        private void RequireReadyForDownloader(string packageName)
        {
            if (GetState(packageName).State.Value != AssetInitState.Ready)
                throw new InvalidOperationException(
                    $"[AssetUtility] 资源包“{packageName}”尚未初始化完成，不能创建下载器。");
        }

        private async UniTask<IAssetHandle<UnityEngine.Object>> LoadInternal(
            string packageName, string key, bool byGuid, Type type, CancellationToken ct)
        {
            ThrowIfDisposed();
            packageName = NormalizePackageName(packageName);
            await EnsureInitialized(packageName, ct);
            using var link = LinkDispose(ct, out var lct);
            return await _provider.LoadAssetAsync(packageName, key, byGuid, type, lct);
        }

        private static IAssetHandle<T> CastHandle<T>(IAssetHandle<UnityEngine.Object> handle, string key)
            where T : UnityEngine.Object
        {
            if (handle == null) return null;
            if (handle.Asset is T asset)
                return new TypedAssetHandle<T>(handle, asset);

            Log.Error(
                $"已加载资源“{key}”不能作为“{typeof(T).Name}”使用。",
                category: nameof(AssetUtility));
            handle.Dispose();
            return null;
        }

        private PackageState GetState(string packageName)
        {
            packageName = NormalizePackageName(packageName);
            if (_packages.TryGetValue(packageName, out var state)) return state;
            state = new PackageState(packageName);
            _packages.Add(packageName, state);
            return state;
        }

        // 把空 packageName 解析成默认包（默认包也可能为空）。被动用：仅查询 / 取状态，空结果让下游自然得到 not-ready，不抛。
        private string NormalizePackageName(string packageName)
            => string.IsNullOrWhiteSpace(packageName) ? _defaultPackageName : packageName;

        // 发起真正操作（加载 / 等待 / 初始化）前用：解析后若仍为空（未配置默认包且未指定 packageName），当场清晰报错，
        // 不把空包名一路带到 provider / 状态机产生晦涩错误。
        private string RequirePackage(string packageName)
        {
            var name = NormalizePackageName(packageName);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    "[AssetUtility] 当前生效配置没有默认资源包，且本次未指定 packageName——" +
                    "场景路径请检查 AssetUtility.Settings.DefaultPackageName，代码路径请检查 Configure 的 defaultPackageName；" +
                    "也可改用带 packageName 的重载（如 Load(packageName, location)）。");
            return name;
        }

        // 把调用方 ct 与 utility 销毁令牌（_disposeCts）链接。普通加载会把结果 token 传给 provider；
        // 包维护操作只把它用作 waiter token，物理操作仍只接收 _disposeCts.Token，避免短命调用者取消后提前释放 lane。
        // 两条路径都会在 OnDestroy 时取消等待；无外部 ct 时直接用 _disposeCts.Token，不分配 CTS。
        // 返回的 CTS 须由调用方 using 释放（无外部 ct 时返回 null，using null 为安全空操作）。
        private CancellationTokenSource LinkDispose(CancellationToken ct, out CancellationToken linked)
        {
            if (!ct.CanBeCanceled) { linked = _disposeCts.Token; return null; }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            linked = cts.Token;
            return cts;
        }

        private void ThrowIfDisposed()
        {
            if (_disposedByDestroy)
                throw new ObjectDisposedException(nameof(AssetUtility));
        }

        private sealed class TypedAssetHandle<T> : IAssetHandle<T> where T : UnityEngine.Object
        {
            private IAssetHandle<UnityEngine.Object> _inner;

            public TypedAssetHandle(IAssetHandle<UnityEngine.Object> inner, T asset)
            {
                _inner = inner;
                Asset = asset;
            }

            public T Asset { get; private set; }
            public bool IsValid => _inner != null && _inner.IsValid;

            public void Dispose()
            {
                if (_inner == null) return;
                _inner.Dispose();
                _inner = null;
                Asset = null;
            }
        }
    }
}
