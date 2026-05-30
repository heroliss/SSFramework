using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using UnityEngine;
using R3;

namespace Game.Framework.Demo.Command
{
    /// <summary>
    /// 异步倒计时 Command：演示 <c>IAsyncCommand</c> + <c>CancellationToken</c>。
    /// </summary>
    /// <remarks>
    /// <b>cancellationToken：</b>框架已合并 View 销毁令牌 + Context 销毁令牌，View 调用方无需手动传。<br/>
    /// <b>进度回吐：</b>通过构造期传入的 <c>Progress</c> RP 上报，View 订阅即可（View → Command → 流回 View，闭环）。<br/>
    /// <b>异常合约：</b>用 <c>OperationCanceledException</c> 表达"被取消"；其它异常向上抛出。
    /// </remarks>
    public sealed class AsyncCountdownCommand : IAsyncCommand
    {
        public float DurationSeconds = 3f;
        public RP<float> Progress;
        public RP<string> Status;

        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            if (Status != null) Status.Value = "运行中…";
            if (Progress != null) Progress.Value = 0f;

            try
            {
                float elapsed = 0f;
                while (elapsed < DurationSeconds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    elapsed += Time.deltaTime;
                    if (Progress != null) Progress.Value = Math.Min(elapsed / DurationSeconds, 1f);
                    await UniTask.Yield(cancellationToken: cancellationToken);
                }

                if (Progress != null) Progress.Value = 1f;
                if (Status != null) Status.Value = "已完成";
            }
            catch (OperationCanceledException)
            {
                if (Status != null) Status.Value = "已取消";
                if (Progress != null) Progress.Value = 0f;
                throw;
            }
        }
    }
}
