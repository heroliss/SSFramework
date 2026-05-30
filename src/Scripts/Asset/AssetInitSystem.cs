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
    /// System 层只负责编排：读取 <see cref="AssetSettingsModel"/>、配置 <see cref="AssetUtility"/>、按包触发初始化。
    /// 具体资源库如何创建包、更新清单、处理远端地址与解密，都由 provider 适配层负责。
    ///
    /// 单个 package 初始化失败只会让对应包进入 Failed，不阻塞后续包；业务加载某个包时会等待该包自己的状态。
    /// </summary>
    public class AssetInitSystem : MonoSystemBase
    {
        [Inject] private AssetUtility _utility;
        [Inject] private AssetSettingsModel _settings;

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
                    "[AssetInitSystem] AssetUtility or AssetSettingsModel not found in Context. " +
                    "Place both components under the same MonoGameContextBase.");
                Debug.LogError(ex);
                _utility?.FailDefaultInitialization(ex);
                return;
            }

            var config = new AssetProviderConfig
            {
                MainCdnUrl = _settings.MainCdnUrl,
                FallbackCdnUrl = _settings.FallbackCdnUrl,
                FileOffset = _settings.FileOffset,
                DownloadingMaxNumber = _settings.DownloadingMaxNumber,
                FailedTryAgain = _settings.FailedTryAgain,
            };

            _utility.Configure(_settings.DefaultPackageName, config);
#if UNITY_EDITOR
            // EditorSimulate 下载模拟时长：仅编辑器写入，AssetUtility 内部按需包装 SimulatedAssetDownloader。
            _utility.EditorSimulateDownloadSeconds = _settings.EditorSimulateDownloadSeconds;
#endif

            foreach (var packageName in _settings.EnumeratePackageNames())
            {
                if (token.IsCancellationRequested) break;
                await _utility.InitializePackageAsync(packageName, _settings.ActualPlayMode, token);
            }
        }
    }
}
