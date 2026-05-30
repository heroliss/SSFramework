using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.System;
using Game.Framework.View;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证异步 Command 的 CancellationToken 在 Context.Dispose / View 销毁时真实触发。
    /// PlayMode 测试，因为依赖 MonoBehaviour.GetCancellationTokenOnDestroy 与帧推进。
    /// </summary>
    public class CancellationTests
    {
        /// <summary>长时间运行的命令，捕获 token 取消状态供断言。</summary>
        private sealed class LongRunningCommand : IAsyncCommand
        {
            public bool Started;
            public bool Cancelled;
            public Exception Caught;

            public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
            {
                Started = true;
                try
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException ex)
                {
                    Cancelled = cancellationToken.IsCancellationRequested;
                    Caught = ex;
                    throw;
                }
            }
        }

        private sealed class TestView : MonoViewBase { }

        private GameObject _root;
        private MonoGameContextBase _ctx;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = new GameObject("CancellationTestRoot");

            var ctxGo = new GameObject("Context");
            ctxGo.transform.SetParent(_root.transform);
            _ctx = ctxGo.AddComponent<MonoGameContextBase>();
            // CommandSystem 是异步命令必需依赖，通过 RegisterUtility 走纯 C# 注册
            // —— 实际上 CommandSystem 注册到 ICommandSystem，走 RegisterValue 太晚（builder 已 Build），
            // 这里走 Container 路径不行。用一个挂 Awake 钩子的简单方式：直接 RegisterValue 到 GameContext 的运行期。
            // 更简单：用纯 C# Context，但又需要 MonoViewBase 自动绑定父级——所以仍走 Mono 路径。
            // 解法：定义一个简易 MonoGameContextBase 子类，InstallBindings 时注册 ICommandSystem。
            // 这里直接拿 _ctx 不行——它已经 Awake 了。
            // 临时方案：用一个全新的派生 Context；见 SetUpWithCommandSystem。
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _ctx = null;
            yield return null;
        }

        /// <summary>派生 Context 子类：在 InstallBindings 时注册 ICommandSystem，让异步 Command 能跑。</summary>
        private sealed class CmdEnabledContext : MonoGameContextBase
        {
            protected override void InstallBindings(ContainerBuilder builder)
            {
                builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            }
        }

        private CmdEnabledContext CreateCommandReadyContext()
        {
            // 销毁 SetUp 创建的占位 _ctx，换成 CmdEnabledContext。
            UnityEngine.Object.Destroy(_ctx.gameObject);
            var ctxGo = new GameObject("CmdCtx");
            ctxGo.transform.SetParent(_root.transform);
            return ctxGo.AddComponent<CmdEnabledContext>();
        }

        // ── 用例 1：View 销毁 → 命令收到取消 ───────────────────────────

        [UnityTest]
        public IEnumerator ExecuteCommandAsync_FromView_CancelledOnViewDestroy() => UniTask.ToCoroutine(async () =>
        {
            var ctx = CreateCommandReadyContext();
            await UniTask.Yield();

            var viewGo = new GameObject("View");
            viewGo.transform.SetParent(ctx.transform);
            var view = viewGo.AddComponent<TestView>();

            var cmd = new LongRunningCommand();
            // View 路径无参重载——自动链 view 销毁 + ctx 销毁
            var task = view.ExecuteCommandAsync(cmd).SuppressCancellationThrow();

            // 等 Started=true
            await UniTask.WaitUntil(() => cmd.Started);
            Assert.IsTrue(cmd.Started);
            Assert.IsFalse(cmd.Cancelled);

            UnityEngine.Object.Destroy(viewGo);
            await UniTask.Yield();
            await UniTask.Yield();  // 让 OnDestroy 完成

            bool isCanceled = await task;
            Assert.IsTrue(isCanceled, "View 销毁应取消进行中的 ExecuteCommandAsync");
            Assert.IsTrue(cmd.Cancelled, "命令内的 cancellationToken.IsCancellationRequested 应为 true");
        });

        // ── 用例 2：Context.Dispose → 命令收到取消 ─────────────────────

        [UnityTest]
        public IEnumerator ExecuteCommandAsync_FromView_CancelledOnContextDispose() => UniTask.ToCoroutine(async () =>
        {
            var ctx = CreateCommandReadyContext();
            await UniTask.Yield();

            var viewGo = new GameObject("View");
            viewGo.transform.SetParent(ctx.transform);
            var view = viewGo.AddComponent<TestView>();

            var cmd = new LongRunningCommand();
            var task = view.ExecuteCommandAsync(cmd).SuppressCancellationThrow();

            await UniTask.WaitUntil(() => cmd.Started);

            UnityEngine.Object.Destroy(ctx.gameObject);
            await UniTask.Yield();
            await UniTask.Yield();

            bool isCanceled = await task;
            Assert.IsTrue(isCanceled, "Context.Dispose 应取消进行中的 ExecuteCommandAsync");
            Assert.IsTrue(cmd.Cancelled);
        });

        // ── 用例 3：外部 customToken 取消 → 命令收到取消 ────────────────

        [UnityTest]
        public IEnumerator ExecuteCommandAsync_WithCustomToken_CancelledByExternalToken() => UniTask.ToCoroutine(async () =>
        {
            var ctx = CreateCommandReadyContext();
            await UniTask.Yield();

            var viewGo = new GameObject("View");
            viewGo.transform.SetParent(ctx.transform);
            var view = viewGo.AddComponent<TestView>();

            using var cts = new CancellationTokenSource();
            var cmd = new LongRunningCommand();
            // 显式传 token：框架会链接 ctx.CT + customToken（按 A1 的 LinkExecutionToken 语义，外部 token 优先于 view 销毁 token）
            var task = view.ExecuteCommandAsync(cmd, cts.Token).SuppressCancellationThrow();

            await UniTask.WaitUntil(() => cmd.Started);
            cts.Cancel();
            await UniTask.Yield();

            bool isCanceled = await task;
            Assert.IsTrue(isCanceled, "外部 token 取消应当传到命令内");
            Assert.IsTrue(cmd.Cancelled);
        });

    }
}
