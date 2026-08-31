using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Internal;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// UI 核心的线程提交契约：调用入口属于 Unity 主线程；Adapter、过渡与计时任务即使在 worker
    /// 物理结束，窗口状态、hook、backend 收尾及公共 task 终态也必须回到主线程。
    /// </summary>
    public sealed class UIThreadBoundaryTests
    {
        private GameContext _context;
        private UIUtility _ui;

        [SetUp]
        public void SetUp()
        {
            using var builder = new ContainerBuilder();
            _context = new GameContext(builder.Build(), inheritFromGlobal: false);
        }

        [TearDown]
        public void TearDown()
        {
            _ui?.Dispose();
            _context.Dispose();
        }

        [UnityTest]
        public IEnumerator BackendWorkerSuccess_CommitsAndCompletesOnMainThread()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                var backend = new WorkerBackend();
                _ui = new UIUtility(_context, backend);

                UniTask<ThreadWindow> opening = _ui.Open<ThreadWindow>();
                Assert.AreEqual(UniTaskStatus.Pending, opening.Status,
                    "释放 worker gate 前，Open 必须真实挂起并已登记 continuation。");
                backend.CompleteCreateFromThreadPool();
                var window = await opening;

                Assert.AreNotEqual(mainThread, backend.CreateCompletionThread,
                    "用例必须让自定义 backend 真实地在 worker 完成。");
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                    "Open 成功应在 Unity 主线程交付给调用方。");
                Assert.AreEqual(mainThread, window.CreateThread);
                Assert.AreEqual(mainThread, window.OpenThread);
                Assert.That(backend.PostCreateThreads.ToArray(), Is.All.EqualTo(mainThread),
                    "窗口映射提交后的 backend 调用不得继承 CreateWindow 的 worker 完成线程。");
            });

        [UnityTest]
        public IEnumerator BackendWorkerFailure_PreservesRootAndReleasesCreatingSlotOnMainThread()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                var expected = new InvalidOperationException("worker-create-failure");
                var backend = new WorkerBackend { Failure = expected };
                _ui = new UIUtility(_context, backend);
                UniTask<ThreadWindow> opening = _ui.Open<ThreadWindow>();
                Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
                backend.CompleteCreateFromThreadPool();

                try
                {
                    await opening;
                    Assert.Fail("worker 创建故障必须传播。");
                }
                catch (InvalidOperationException actual)
                {
                    Assert.AreSame(expected, actual, "主线程切换不得包装或替换 backend 根异常。");
                    Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                        "Open 失败也必须在 Unity 主线程交付。");
                    StringAssert.Contains(nameof(WorkerBackend.CreateWindow), actual.StackTrace);
                }

                backend.Failure = null;
                backend.GateCreate = false;
                var retry = await _ui.Open<ThreadWindow>();

                Assert.IsNotNull(retry, "失败事务必须摘除 _creating owner，后续重试才能重新创建。");
                Assert.AreEqual(2, backend.CreateCount);
            });

        [UnityTest]
        public IEnumerator ConcurrentWaiterCanceledFromWorker_ReturnsMainThreadWithoutCancelingOwner()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                var backend = new WorkerBackend();
                _ui = new UIUtility(_context, backend);
                using var waiterCancellation = new CancellationTokenSource();

                UniTask<ThreadWindow> owner = _ui.Open<ThreadWindow>();
                UniTask<ThreadWindow> waiter = _ui.Open<ThreadWindow>(waiterCancellation.Token);
                Assert.AreEqual(UniTaskStatus.Pending, owner.Status);
                Assert.AreEqual(UniTaskStatus.Pending, waiter.Status,
                    "取消前等待者必须已挂到共享 _creating task，避免测试已完成 task。");
                CancelOnThreadPool(waiterCancellation).Forget();

                try
                {
                    await waiter;
                    Assert.Fail("并发等待者应观察到自己的取消。");
                }
                catch (OperationCanceledException error)
                {
                    Assert.AreEqual(waiterCancellation.Token, error.CancellationToken);
                    Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId,
                        "worker 发起取消不能把等待者 continuation 留在线程池。");
                }

                Assert.AreEqual(UniTaskStatus.Pending, owner.Status,
                    "等待者取消只脱离共享创建，不能取消物理 owner。");
                backend.CompleteCreateFromThreadPool();
                Assert.IsNotNull(await owner);
                Assert.AreEqual(1, backend.CreateCount);
            });

        [UnityTest]
        public IEnumerator WorkerTransitions_FinalizeHooksAndBackendOnMainThread()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                WorkerTransitionWindow.Reset();
                var backend = new WorkerBackend { GateCreate = false };
                _ui = new UIUtility(_context, backend);

                var window = await _ui.Open<WorkerTransitionWindow>();
                Assert.AreEqual(1, backend.InputBlockCount,
                    "pending 入场过渡应先开启挡板，不能在 Status 检查前抢跑成零时长任务。");
                WorkerTransitionWindow.CompleteOpenFromThreadPool();
                await UniTask.WaitUntil(() => backend.InputBlockCount == 2);

                Assert.AreNotEqual(mainThread, WorkerTransitionWindow.OpenCompletionThread);
                Assert.That(backend.InputBlockThreads.ToArray(), Is.All.EqualTo(mainThread),
                    "入场过渡在 worker 完成后也必须从主线程撤输入挡板。");

                _ui.Close<WorkerTransitionWindow>();
                Assert.AreEqual(3, backend.InputBlockCount,
                    "pending 出场过渡应再次开启挡板，并延迟 OnClose 与物理回收。");
                Assert.Zero(backend.DestroyCount);
                WorkerTransitionWindow.CompleteCloseFromThreadPool();
                await UniTask.WaitUntil(() => backend.DestroyCount == 1);

                Assert.AreNotEqual(mainThread, WorkerTransitionWindow.CloseCompletionThread);
                Assert.AreEqual(mainThread, window.CloseThread,
                    "出场过渡结束后的 OnClose 必须回主线程。");
                Assert.AreEqual(4, backend.InputBlockCount);
                Assert.That(backend.InputBlockThreads.ToArray(), Is.All.EqualTo(mainThread));
                Assert.That(backend.DestroyThreads.ToArray(), Is.All.EqualTo(mainThread));
            });

        [UnityTest]
        public IEnumerator WorkerToastDelay_AutoCloseReturnsToMainThread()
            => UniTask.ToCoroutine(async () =>
            {
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                var delay = new GatedDelay();
                var backend = new WorkerBackend { GateCreate = false };
                _ui = new UIUtility(
                    _context,
                    backend,
                    new UIBuiltinWindows { Toast = typeof(ToastWindow) },
                    delay.Wait);

                await _ui.ShowToast("thread-boundary", duration: 0f);
                Assert.IsTrue(delay.Started);
                Assert.Zero(backend.DestroyCount,
                    "释放计时 gate 前 Toast 必须仍打开，避免已完成 task 造成假阳性。");
                delay.CompleteFromThreadPool();
                await UniTask.WaitUntil(() => backend.DestroyCount == 1);

                Assert.AreNotEqual(mainThread, delay.CompletionThread);
                Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
                Assert.That(backend.DestroyThreads.ToArray(), Is.All.EqualTo(mainThread),
                    "Toast 时钟可以在 worker 结束，但自动关闭与物理回收必须回主线程。");
            });

        private static async UniTaskVoid CancelOnThreadPool(CancellationTokenSource source)
        {
            await UniTask.SwitchToThreadPool();
            source.Cancel();
        }

        [UIWindow(Layer = UILayer.Popup, Modal = true)]
        private class ThreadWindow : IUIWindow
        {
            public int CreateThread { get; private set; }
            public int OpenThread { get; private set; }
            public int CloseThread { get; private set; }

            public void OnCreate() => CreateThread = Thread.CurrentThread.ManagedThreadId;
            public void OnOpen(object args) => OpenThread = Thread.CurrentThread.ManagedThreadId;
            public void OnClose() => CloseThread = Thread.CurrentThread.ManagedThreadId;
            public void OnCover() { }
            public void OnReveal() { }
            public virtual UniTask OnOpenTransition(CancellationToken ct) => UniTask.CompletedTask;
            public virtual UniTask OnCloseTransition(CancellationToken ct) => UniTask.CompletedTask;
        }

        [UIWindow(Layer = UILayer.Window)]
        private sealed class WorkerTransitionWindow : ThreadWindow
        {
            private static UniTaskCompletionSource s_openGate;
            private static UniTaskCompletionSource s_closeGate;
            private static int s_openCompletionThread;
            private static int s_closeCompletionThread;

            public static int OpenCompletionThread => Volatile.Read(ref s_openCompletionThread);
            public static int CloseCompletionThread => Volatile.Read(ref s_closeCompletionThread);

            public static void Reset()
            {
                s_openGate = new UniTaskCompletionSource();
                s_closeGate = new UniTaskCompletionSource();
                Volatile.Write(ref s_openCompletionThread, 0);
                Volatile.Write(ref s_closeCompletionThread, 0);
            }

            public static void CompleteOpenFromThreadPool() => CompleteFromThreadPool(s_openGate);
            public static void CompleteCloseFromThreadPool() => CompleteFromThreadPool(s_closeGate);

            public override async UniTask OnOpenTransition(CancellationToken ct)
            {
                await s_openGate.Task.AttachExternalCancellation(ct);
                Volatile.Write(ref s_openCompletionThread, Thread.CurrentThread.ManagedThreadId);
            }

            public override async UniTask OnCloseTransition(CancellationToken ct)
            {
                await s_closeGate.Task.AttachExternalCancellation(ct);
                Volatile.Write(ref s_closeCompletionThread, Thread.CurrentThread.ManagedThreadId);
            }
        }

        [UIWindow(Layer = UILayer.Top)]
        private sealed class ToastWindow : ThreadWindow { }

        private sealed class GatedDelay
        {
            private readonly UniTaskCompletionSource _gate = new();
            private int _started;
            private int _completionThread;

            public bool Started => Volatile.Read(ref _started) != 0;
            public int CompletionThread => Volatile.Read(ref _completionThread);

            public UniTask Wait(TimeSpan _, CancellationToken ct)
            {
                Volatile.Write(ref _started, 1);
                return AwaitCompletion(ct);
            }

            public void CompleteFromThreadPool()
                => UIThreadBoundaryTests.CompleteFromThreadPool(_gate);

            private async UniTask AwaitCompletion(CancellationToken ct)
            {
                await _gate.Task.AttachExternalCancellation(ct);
                Volatile.Write(ref _completionThread, Thread.CurrentThread.ManagedThreadId);
            }
        }

        private class WorkerBackend : IUIBackend
        {
            private readonly UniTaskCompletionSource _createGate = new();
            private int _createCount;
            private int _createCompletionThread;

            public Exception Failure { get; set; }
            public bool GateCreate { get; set; } = true;
            public int CreateCount => Volatile.Read(ref _createCount);
            public int CreateCompletionThread => Volatile.Read(ref _createCompletionThread);
            public ConcurrentQueue<int> PostCreateThreads { get; } = new();
            public ConcurrentQueue<int> InputBlockThreads { get; } = new();
            public ConcurrentQueue<int> DestroyThreads { get; } = new();
            public int InputBlockCount => InputBlockThreads.Count;
            public int DestroyCount => DestroyThreads.Count;

            public void Initialize() { }

            public virtual async UniTask<IUIWindow> CreateWindow(
                UIWindowMeta meta,
                IGameContext context,
                CancellationToken ct)
            {
                Interlocked.Increment(ref _createCount);
                if (GateCreate)
                {
                    await _createGate.Task.AttachExternalCancellation(ct);
                    Volatile.Write(ref _createCompletionThread, Thread.CurrentThread.ManagedThreadId);
                }
                if (Failure != null) throw Failure;
                return (IUIWindow)Activator.CreateInstance(meta.WindowType);
            }

            public void CompleteCreateFromThreadPool() => CompleteFromThreadPool(_createGate);

            public void BringToFront(IUIWindow window) => PostCreateThreads.Enqueue(Thread.CurrentThread.ManagedThreadId);
            public void SetVisible(IUIWindow window, bool visible) => PostCreateThreads.Enqueue(Thread.CurrentThread.ManagedThreadId);

            public void SetModalMask(IUIWindow ownerWindow, bool on)
                => PostCreateThreads.Enqueue(Thread.CurrentThread.ManagedThreadId);

            public void DestroyWindow(IUIWindow window)
            {
                int thread = Thread.CurrentThread.ManagedThreadId;
                PostCreateThreads.Enqueue(thread);
                DestroyThreads.Enqueue(thread);
            }

            public void SetInputBlocked(bool blocked)
            {
                int thread = Thread.CurrentThread.ManagedThreadId;
                PostCreateThreads.Enqueue(thread);
                InputBlockThreads.Enqueue(thread);
            }

            public void Teardown() { }
        }

        private static async UniTaskVoid CompleteFromThreadPool(UniTaskCompletionSource gate)
        {
            await UniTask.SwitchToThreadPool();
            gate.TrySetResult();
        }
    }
}
