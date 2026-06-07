using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.System;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// 进入游戏时的资源系统初始化流程。
    ///
    /// System 层只负责编排：读取 <see cref="AssetSystemConfigModel"/>、配置 <see cref="AssetUtility"/>、按包触发初始化。
    /// 具体资源库如何创建包、更新清单、处理远端地址与解密，都由 provider 适配层负责。
    ///
    /// 单个 package 初始化失败只会让对应包进入 Failed，不阻塞后续包；业务加载某个包时会等待该包自己的状态。
    ///
    /// <see cref="AssetSystemConfigModel.AutoInitializeOnStartup"/> 关闭时，启动只写配置、不触发初始化（不联网拉清单），
    /// 留给业务在合适时机显式调 <see cref="IAssetUtility.RetryInitialize"/>——用于隐私同意 / 选区后再联网的启动流程。
    /// </summary>
    public class AssetInitSystem : MonoSystemBase
    {
        [Inject] private AssetUtility _utility;
        [Inject] private AssetSystemConfigModel _settings;

        private CancellationTokenSource _cts;

        protected override void Awake()
        {
            base.Awake();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy(),
                ((IHasGameContext)this).Context.CancellationToken);
            InitAsync(_cts.Token).Forget();
        }

        protected override void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            base.OnDestroy();
        }

        private async UniTaskVoid InitAsync(CancellationToken token)
        {
            if (_utility == null || _settings == null)
            {
                var ex = new InvalidOperationException(
                    "[AssetInitSystem] AssetUtility or AssetSystemConfigModel not found in Context. " +
                    "Place both components under the same MonoGameContextBase.");
                Debug.LogError(ex);
                _utility?.FailDefaultInitialization(ex);
                return;
            }

            // Model→provider 配置 DTO 的映射收口在 Model.ToProviderConfig()（新增配置项只改一处）。
            // 默认包名 / 运行模式是「初始化身份 / 模式」参数、不在 DTO 里，单独传给 Configure。
            // 运行模式在此写入 CurrentPlayMode：即便关掉自动初始化、延迟到业务显式 RetryInitialize 触发，也能用正确模式跑。
            _utility.Configure(_settings.DefaultPackageName, _settings.ToProviderConfig(), _settings.ActualPlayMode);
#if UNITY_EDITOR
            // EditorSimulate 模拟下载（大小 + 速度，时长 = 大小/速度）：仅编辑器写入，AssetUtility 内部按需包装 SimulatedAssetDownloader。
            _utility.ConfigureEditorSimulateDownload(
                _settings.EditorSimulateDownloadSizeBytes,
                _settings.EditorSimulateDownloadSpeedBytesPerSec);
#endif

            // 延迟初始化：关掉自动初始化时，启动只配置、不联网拉清单；由业务在合适时机（如隐私同意 / 选区后）
            // 调 IAssetUtility.RetryInitialize() 触发——RetryInitialize 对 Idle 包即「冷启动初始化」。
            if (!_settings.AutoInitializeOnStartup)
                return;

            foreach (var packageName in _settings.EnumeratePackageNames())
            {
                if (token.IsCancellationRequested) break;
                await _utility.InitializePackageAsync(packageName, _settings.ActualPlayMode, token);
            }
        }
    }
}
