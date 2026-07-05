using System.Collections;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Context;
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
    /// - Instantiate 子树后自动找到父级 Context 并注册（AGENTS §16）
    /// - 父 Context 先 OnDestroy 时，子层反注册应短路避免 NRE（AGENTS §20）
    /// - 三层查找回退顺序（Target → 父级 → Main → 报错）
    /// PlayMode 测试，因为依赖 Unity 生命周期与 GameObject 销毁顺序。
    /// </summary>
    public class MonoLifecycleTests
    {
        private sealed class TestMonoModel : MonoModelBase
        {
            public string Tag = "default";
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
            Assert.IsNotNull(resolved, "Instantiate 后 prefab 内的 MonoModelBase 应自动注册（AGENTS §16）");
            Assert.AreEqual("from-prefab", resolved.Tag);

            UnityEngine.Object.Destroy(prefabRoot);
        });

        // ── 用例 3：父 Context 先 Destroy，子 Model 后 Destroy 不 NRE（AGENTS §20）─

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
            Assert.Pass("OnDestroy 链未抛 NRE（AGENTS §20 的 IsDisposed 短路生效）");
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
                "无 Inspector 绑定且无父级时，Model 应回退注册到 GameContext.Main（AGENTS §16 查找第 3 步）");
        });
    }
}
