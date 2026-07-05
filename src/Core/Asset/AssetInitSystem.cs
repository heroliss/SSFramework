using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Systems;
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
    /// 自动初始化是<b>按包</b>的（<see cref="AssetPackageConfig.AutoInitialize"/>）：只有标了「自动初始化」的包在启动时拉清单；
    /// 标「不自动初始化」的包（DLC 懒加载 / 合规延迟联网）保持 Idle，留给业务在合适时机显式调 <see cref="IAssetUtility.Initialize"/> 触发。
    /// 把全部要联网的包都设为「不自动初始化」= 启动前零网络连接（隐私同意 / 选区后再联网的合规启动）。
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
            // 运行模式在此写入 CurrentPlayMode：即便某些包延迟到业务显式 Initialize 触发，也能用正确模式跑。
            _utility.Configure(_settings.DefaultPackageName, _settings.ToProviderConfig(), _settings.ActualPlayMode);

            // 配置校验：默认包名指向不存在的包会让所有便捷重载失效——把默认包置 Failed 让等待方收到清晰异常，但不拖垮其它包。
            var configError = _settings.GetConfigError();
            if (configError != null)
            {
                var ex = new InvalidOperationException("[AssetInitSystem] " + configError);
                Debug.LogError(ex);
                _utility.FailDefaultInitialization(ex);
            }

            // 逐包自动初始化：先把「该自动初始化」的包统一标 Pending（消除批次窗口内 Load 抢跑竞态 → 此时 Load 等待而非报错），
            // 再依次初始化。标「不自动初始化」的包保持 Idle，待业务 Initialize 触发；全列表都不自动初始化 = 启动零联网（合规启动）。
            var autoInitPackages = new List<string>();
            foreach (var packageName in _settings.EnumeratePackageNames())
                if (_settings.ShouldAutoInitialize(packageName))
                    autoInitPackages.Add(packageName);

            _utility.MarkPackagesPending(autoInitPackages);

            try
            {
                foreach (var packageName in autoInitPackages)
                {
                    if (token.IsCancellationRequested) break;
                    await _utility.InitializePackageAsync(packageName, _settings.ActualPlayMode, token);
                }
            }
            finally
            {
                // 批次正常跑完时这是空操作（已无 Pending）；被取消/中止而提前结束时，把还没轮到、仍停在 Pending 的包置 Failed，
                // 避免其 InitTcs 永不完成、后续 EnsureInitialized 永久挂起。（销毁期 AssetUtility 已先 Dispose，则此调用直接短路。）
                _utility.AbandonPendingPackages();
            }
        }
    }
}
