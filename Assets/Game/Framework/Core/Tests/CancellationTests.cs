using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.Systems;
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

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = new GameObject("CancellationTestRoot");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
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

        // ── 用例 4：显式 lifetime 覆盖 Mono 销毁默认值 ───────────────

        [UnityTest]
        public IEnumerator ExecuteCommandAsync_WithExplicitLifetime_ViewDestroyDoesNotCancelUntilExternalCancels()
            => UniTask.ToCoroutine(async () =>
            {
                var ctx = CreateCommandReadyContext();
                await UniTask.Yield();

                var viewGo = new GameObject("View");
                viewGo.transform.SetParent(ctx.transform);
                var view = viewGo.AddComponent<TestView>();

                using var lifetime = new CancellationTokenSource();
                var cmd = new LongRunningCommand();
                var task = view.ExecuteCommandAsync(cmd, lifetime.Token).SuppressCancellationThrow();
                await UniTask.WaitUntil(() => cmd.Started);

                UnityEngine.Object.Destroy(viewGo);
                await UniTask.Yield();
                await UniTask.Yield();

                Assert.IsFalse(cmd.Cancelled,
                    "可取消的显式 token 是 View 侧 lifetime override，不能又隐式链接 Mono 销毁令牌。");
                Assert.AreEqual(UniTaskStatus.Pending, task.Status,
                    "View 销毁后任务应继续由显式 lifetime 持有，直到它或 Context 取消。");

                lifetime.Cancel();
                Assert.IsTrue(await task);
                Assert.IsTrue(cmd.Cancelled);
            });

        // ── 用例 5：显式 lifetime 不能覆盖 Context owner ─────────────

        [UnityTest]
        public IEnumerator ExecuteCommandAsync_WithExplicitLifetime_ContextDisposeStillCancels()
            => UniTask.ToCoroutine(async () =>
            {
                var ctx = CreateCommandReadyContext();
                await UniTask.Yield();

                var viewGo = new GameObject("View");
                viewGo.transform.SetParent(ctx.transform);
                var view = viewGo.AddComponent<TestView>();

                using var lifetime = new CancellationTokenSource();
                var cmd = new LongRunningCommand();
                var task = view.ExecuteCommandAsync(cmd, lifetime.Token).SuppressCancellationThrow();
                await UniTask.WaitUntil(() => cmd.Started);

                // 只销毁 Context，保留 View 本体，证明取消来自不可覆盖的 Context owner。
                viewGo.transform.SetParent(_root.transform);
                UnityEngine.Object.Destroy(ctx.gameObject);
                await UniTask.Yield();
                await UniTask.Yield();

                Assert.IsTrue(await task, "显式 View lifetime 不能让命令逃离所属 Context。");
                Assert.IsTrue(cmd.Cancelled);
                Assert.IsFalse(lifetime.IsCancellationRequested,
                    "本次取消应来自 Context，而不是测试的显式 lifetime。");
            });

    }
}
