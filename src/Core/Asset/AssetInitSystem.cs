using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Systems;
using UnityEngine;

namespace Game.Framework
{
    /// <summary>
    /// 旧场景兼容适配器：把 <see cref="AssetSystemConfigModel"/> 的序列化数据交给
    /// <see cref="AssetUtility"/>。新场景不再需要独立初始化 System。
    /// </summary>
    [Obsolete("请迁移为 AssetUtility 单组件入口；AssetUtility 会自行编排自动初始化。", false)]
    [AddComponentMenu("")]
    public class AssetInitSystem : MonoSystemBase
    {
        [Inject] private AssetUtility _utility;
#pragma warning disable CS0618 // 本类型的唯一职责就是桥接旧版配置组件。
        [Inject] private AssetSystemConfigModel _settings;
#pragma warning restore CS0618

        private CancellationTokenSource _cts;

        protected override void Awake()
        {
            base.Awake();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                this.GetCancellationTokenOnDestroy(),
                ((IHasGameContext)this).Context.CancellationToken);
            CancellationToken token = _cts.Token;
            InitializeCompatibilityPathAsync(token).Forget(exception =>
                ObserveCompatibilityFailure(exception, token));
        }

        protected override void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            base.OnDestroy();
        }

        private async UniTask InitializeCompatibilityPathAsync(CancellationToken token)
        {
            if (_utility == null || _settings == null)
            {
                var exception = new InvalidOperationException(
                    "[AssetInitSystem] 旧场景兼容接线不完整：需要同一 Context 中同时存在 " +
                    "AssetUtility、AssetSystemConfigModel 与 AssetInitSystem。建议执行资源系统单入口迁移。");
                Log.Error(
                    "旧版资源系统无法启动。",
                    exception,
                    nameof(AssetInitSystem),
                    this);
                _utility?.FailDefaultInitialization(exception);
                return;
            }

            await _utility.ConfigureAndAutoInitialize(_settings.ToRuntimeSettings(), token);
        }

        private void ObserveCompatibilityFailure(Exception exception, CancellationToken token)
        {
            // Context / 组件销毁是这个兼容 owner 的正常终态；Adapter 即使用别的异常形态退场，也以 owner 意图为准。
            if (token.IsCancellationRequested) return;

            Log.Error(
                "旧版资源系统兼容初始化异常停止。",
                exception,
                nameof(AssetInitSystem),
                this);
            try
            {
                _utility?.FailDefaultInitialization(exception);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // 记录异常与发布状态之间宿主开始拆除：无需再向已释放的 Utility 写终态。
            }
            catch (Exception stateException)
            {
                Log.Error(
                    "旧版资源初始化失败后无法发布默认包终态。",
                    stateException,
                    nameof(AssetInitSystem),
                    this);
            }
        }
    }
}
