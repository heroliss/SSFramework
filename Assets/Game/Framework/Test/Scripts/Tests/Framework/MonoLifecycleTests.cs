using System;
using System.Collections;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Diagnostics;
using Game.Framework.Internal;
using Game.Framework.Model;
using Game.Framework.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证 MonoXxxBase 在 Awake/OnDestroy 边界的契约行为：
    /// - Instantiate 子树后按“显式 Target → Transform 父链 → Main”找到 Context 并注册
    /// - 父 Context 先 OnDestroy 时，子层反注册应短路避免 NRE
    /// - 三层查找回退顺序（Target → 父级 → Main → 报错）
    /// PlayMode 测试，因为依赖 Unity 生命周期与 GameObject 销毁顺序。
    /// </summary>
    public class MonoLifecycleTests
    {
        private sealed class TestMonoModel : MonoModelBase
        {
            public string Tag = "default";
        }

        private sealed class DisposeProbe : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class FailingMonoContext : MonoGameContextBase
        {
            internal static Action<ContainerBuilder> Install;
            protected override void InstallBindings(ContainerBuilder builder) => Install?.Invoke(builder);
        }

        private GameObject _root;
        private GameContext _savedMain;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 备份并清空 GameContext.Main，避免测试间相互污染
            _savedMain = GameContext.Main;
            GameContext.Main = null;

            _root = new GameObject("LifecycleTestRoot");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            FailingMonoContext.Install = null;
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            // 测试结束清理 Main——如果中间设过 Main，TearDown 时已 destroy GameObject，
            // 但静态字段不会自动清；MonoGlobalContext.OnDestroy 才清。这里手动恢复。
            GameContext.Main = _savedMain;
            yield return null;
        }

        // ── 用例 1：子 Context 节点下的 Model 自动注册 ───────────────────

        [UnityTest]
        public IEnumerator ChildModelUnderContext_AutoRegistered_ResolvableImmediately() => UniTask.ToCoroutine(async () =>
        {
            var ctxGo = new GameObject("Ctx");
            ctxGo.transform.SetParent(_root.transform);
            var ctx = ctxGo.AddComponent<MonoGameContextBase>();

            var modelGo = new GameObject("Model");
            modelGo.transform.SetParent(ctxGo.transform);
            var model = modelGo.AddComponent<TestMonoModel>();
            model.Tag = "registered";

            // Awake 完成后立即解析
            await UniTask.Yield();

            var resolved = ctx.GetModel<TestMonoModel>();
            Assert.AreSame(model, resolved, "MonoModelBase Awake 后应自动注册到父级 MonoGameContextBase");
            Assert.AreEqual("registered", resolved.Tag);
        });

        // ── 用例 2：Instantiate prefab 子树到 Context 节点下，Model 仍自动注册 ─

        [UnityTest]
        public IEnumerator InstantiateUnderContext_ModelAutoRegistered() => UniTask.ToCoroutine(async () =>
        {
            var ctxGo = new GameObject("Ctx");
            ctxGo.transform.SetParent(_root.transform);
            var ctx = ctxGo.AddComponent<MonoGameContextBase>();
            await UniTask.Yield();

            // 模拟 prefab：单独建一个 GameObject 子树（含 TestMonoModel），SetActive(false) 防止 Awake 在原位置触发
            var prefabRoot = new GameObject("PrefabRoot");
            prefabRoot.SetActive(false);
            var modelInPrefab = prefabRoot.AddComponent<TestMonoModel>();
            modelInPrefab.Tag = "from-prefab";

            // Instantiate 到 Context 子节点下并激活
            var instance = UnityEngine.Object.Instantiate(prefabRoot, ctxGo.transform);
            instance.SetActive(true);
            await UniTask.Yield();

            var resolved = ctx.GetModel<TestMonoModel>();
            Assert.IsNotNull(resolved, "Instantiate 后 prefab 内的 MonoModelBase 应按 Context 自动绑定顺序注册");
            Assert.AreEqual("from-prefab", resolved.Tag);

            UnityEngine.Object.Destroy(prefabRoot);
        });

        // ── 用例 3：父 Context 先 Destroy，子 Model 后 Destroy 不 NRE ──

        [UnityTest]
        public IEnumerator ParentContextDestroyedBeforeChildModel_NoNRE() => UniTask.ToCoroutine(async () =>
        {
            var ctxGo = new GameObject("Ctx");
            ctxGo.transform.SetParent(_root.transform);
            var ctx = ctxGo.AddComponent<MonoGameContextBase>();

            var modelGo = new GameObject("Model");
            modelGo.transform.SetParent(ctxGo.transform);
            modelGo.AddComponent<TestMonoModel>();
            await UniTask.Yield();

            // 销毁整个 ctxGo（带子模型）。Unity 会按 DefaultExecutionOrder 排 OnDestroy 顺序：
            // MonoGameContextBase(-1000) 比 MonoModelBase(-300) 先 OnDestroy → Context 先 dispose，
            // 子层 OnDestroy 时 _contextProvider.IsDisposed == true，反注册应短路。
            // 如果短路缺失会抛 NRE 进 console，被 LogAssert 捕获。

            UnityEngine.Object.Destroy(ctxGo);
            await UniTask.Yield();
            await UniTask.Yield();

            // LogAssert.NoUnexpectedReceived 不需要主动 fail——只要没 LogAssert.Expect 但收到的 error/exception
            // 在测试框架里会自动 fail 测试。这里靠"无异常通过"作为隐式断言。
            Assert.Pass("OnDestroy 链未抛 NRE（父 Context 的 IsDisposed 短路生效）");
        });

        // ── 用例 4：无父级 + Main 未设置 → 明确错误（不 NRE）──────────────

        [UnityTest]
        public IEnumerator NoParent_NoMain_LogsErrorAndSkipsRegister() => UniTask.ToCoroutine(async () =>
        {
            Assert.IsNull(GameContext.Main, "前置：Main 应为 null");

            // 在没有父级 Context 的孤立 GameObject 上挂 Model
            var orphanGo = new GameObject("Orphan");
            orphanGo.transform.SetParent(_root.transform);

            // 期望框架输出 [IModel] No IGameContext found... 之类的 error
            LogAssert.Expect(LogType.Error, new Regex(@"No IGameContext found"));
            orphanGo.AddComponent<TestMonoModel>();
            await UniTask.Yield();

            // 不应抛 NRE，已被 LogAssert.Expect 捕获 error
            Assert.Pass("无父级 + Main 未设置时应输出明确 error 而非 NRE");
        });

        // ── 用例 5：无父级 + Main 已设置 → 自动回退到 Main ────────────────

        [UnityTest]
        public IEnumerator NoParent_FallsBackToMain() => UniTask.ToCoroutine(async () =>
        {
            // 先建一个独立 Context 作 Main 兜底
            var mainCtxGo = new GameObject("MainCtx");
            mainCtxGo.transform.SetParent(_root.transform);
            var mainCtx = mainCtxGo.AddComponent<MonoGameContextBase>();
            await UniTask.Yield();
            GameContext.Main = mainCtx.RawContext;

            // 在不挂在 mainCtx 下的孤立位置创建 Model
            var orphanGo = new GameObject("Orphan");
            orphanGo.transform.SetParent(_root.transform);
            var model = orphanGo.AddComponent<TestMonoModel>();
            model.Tag = "fallback";
            await UniTask.Yield();

            // 应当注册到 Main
            var resolved = mainCtx.GetModel<TestMonoModel>();
            Assert.AreSame(model, resolved,
                "无 Inspector 绑定且无父级时，Model 应按 Context 自动绑定顺序回退到 GameContext.Main");
        });

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator ReadyContext_DiagnosticSnapshotShowsCommittedState() => UniTask.ToCoroutine(async () =>
        {
            var contextGo = new GameObject("ReadyCtx");
            contextGo.transform.SetParent(_root.transform);
            var context = contextGo.AddComponent<MonoGameContextBase>();
            await UniTask.Yield();

            var snapshot = context.DiagnosticSnapshot;
            Assert.AreEqual(MonoContextDiagnosticState.Ready, snapshot.State);
            Assert.IsNull(snapshot.Failure);
            Assert.AreSame(context.RawContext, snapshot.Context);
        });
#endif

        [UnityTest]
        public IEnumerator ContextInitializationFailure_RollsBackOwned_GuardsCalls_AndStopsChildCascade()
            => UniTask.ToCoroutine(async () =>
            {
                var probe = new DisposeProbe();
                FailingMonoContext.Install = builder =>
                {
                    builder.RegisterOwned(probe, typeof(DisposeProbe));
                    throw new InvalidOperationException("install-boom");
                };

                var parentGo = new GameObject("ParentCtx");
                parentGo.transform.SetParent(_root.transform);
                var parent = parentGo.AddComponent<MonoGameContextBase>();

#if UNITY_EDITOR
                int liveBeforeFailure = FrameworkDiagnostics.LiveContexts.Count;
#endif

                var contextGo = new GameObject("BrokenCtx");
                contextGo.transform.SetParent(parentGo.transform);
                LogAssert.Expect(LogType.Exception,
                    new Regex(@"InvalidOperationException: install-boom"));
                var failed = contextGo.AddComponent<FailingMonoContext>();
                await UniTask.Yield();

                Assert.AreEqual(1, probe.DisposeCount,
                    "InstallBindings 在 Build 前失败时 Builder 应回滚 owned 资源");
                Assert.IsTrue(failed.IsDisposed, "失败 Context 对调用方必须表现为不可用");
                Assert.IsNull(failed.RawContext, "失败事务不得发布半初始化 GameContext");

#if UNITY_EDITOR
                var snapshot = failed.DiagnosticSnapshot;
                Assert.AreEqual(MonoContextDiagnosticState.Failed, snapshot.State);
                Assert.AreSame(parent, snapshot.ResolvedParent);
                Assert.IsNull(snapshot.Context);
                StringAssert.Contains("install-boom", snapshot.Failure.ToString());
                Assert.AreEqual(liveBeforeFailure, FrameworkDiagnostics.LiveContexts.Count,
                    "诊断展示不得为 Failed Mono 伪造或残留 GameContext");
#endif

                var error = Assert.Throws<InvalidOperationException>(
                    () => failed.Resolve(typeof(DisposeProbe)));
                StringAssert.Contains("initialization failed", error.Message);
                StringAssert.Contains("install-boom", error.ToString(), "重复调用仍应保留原始根因，而不是 NRE");

                // 最近父 Context 已知失败时，Mono 层必须停在该边界；不得偷偷回退 Main，也不得再抛 NRE。
                var child = new GameObject("ChildModel");
                child.transform.SetParent(contextGo.transform);
                child.AddComponent<TestMonoModel>();
                await UniTask.Yield();

                Assert.AreEqual(1, probe.DisposeCount, "子层 Awake 不应触发失败 Context 重试或二次清理");
                FailingMonoContext.Install = null;
            });
    }
}
