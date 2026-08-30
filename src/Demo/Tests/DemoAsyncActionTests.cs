using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules;
using Game.Framework.Internal;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Tests
{
    /// <summary>锁住 Demo 异步按钮的异常、取消与防重入语义，避免教程代码悄悄退化成 async void。</summary>
    public sealed class DemoAsyncActionTests
    {
        [UnityTest]
        public IEnumerator HostButton_DisablesOnClickIgnoresReentryAndCancelsOnDispose()
        {
            return VerifyHostButtonLifecycle().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator Invoke_ReportsUnhandledExceptionAndRestoresButton()
        {
            return VerifyExceptionReporting().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator HostButtons_ReportSyncAndAsyncExceptionsOnceWithActionNames()
        {
            return VerifyHostExceptionLogging().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator CatalogDeactivate_CancelsHostBeforeModuleTeardown()
        {
            return VerifyCatalogReleaseOrder().ToCoroutine();
        }

        [Test]
        public void ModuleSources_UseDedicatedAsyncActionSeam()
        {
            string modulesDirectory = Path.Combine(
                Application.dataPath,
                "Game/Framework/Demo/Scripts/Modules");
            var forbidden = new Regex(@"\basync\s*\(\s*\)\s*=>", RegexOptions.Compiled);
            var forbiddenAsyncVoid = new Regex(@"\basync\s+void\b", RegexOptions.Compiled);
            int asyncActionRows = 0;

            foreach (string path in Directory.GetFiles(modulesDirectory, "*Module.cs", SearchOption.TopDirectoryOnly))
            {
                string source = File.ReadAllText(path);
                string codeOnly = CSharpLexicalMap.Create(source).CreateCodeOnlyText();
                Assert.IsFalse(
                    forbidden.IsMatch(codeOnly),
                    $"{Path.GetFileName(path)} 含 parameterless async lambda；异步按钮应使用 AddAsyncActionRow(async ct => ...) 并透传生命周期令牌。");
                Assert.IsFalse(
                    forbiddenAsyncVoid.IsMatch(codeOnly),
                    $"{Path.GetFileName(path)} 含 async void；Demo 异步入口必须返回 UniTask 并交给 Host 观察。");

                foreach (Match match in Regex.Matches(codeOnly, @"\b(?:AddActionRow|AddExperimentActionRow)\s*\("))
                {
                    int openParen = codeOnly.IndexOf('(', match.Index);
                    int closeParen = FindMatchingParenthesis(codeOnly, openParen);
                    Assert.Greater(closeParen, openParen,
                        $"{Path.GetFileName(path)} 的同步动作调用括号未闭合，无法完成异步门禁检查。");
                    string invocation = codeOnly.Substring(openParen, closeParen - openParen + 1);
                    Assert.IsFalse(
                        Regex.IsMatch(invocation, @"\basync\b|\.Forget\s*\(|\bUniTaskVoid\b"),
                        $"{Path.GetFileName(path)} 把异步工作藏进同步动作入口；请改用对应 Add*AsyncActionRow 并透传章节令牌。");
                }

                asyncActionRows += Regex.Matches(
                    codeOnly,
                    @"\b(?:AddAsyncActionRow|AddExperimentAsyncActionRow)\s*\(").Count;
            }

            Assert.AreEqual(61, asyncActionRows,
                "异步按钮增删时同步审查：必须全部走普通或教学实验的异步入口，不能藏回同步 Action + Forget/void 包装。 ");

            AssertActionOverloadGuards(nameof(DemoModuleHost.AddActionRow));
            AssertActionOverloadGuards(nameof(DemoModuleHost.AddExperimentActionRow));

            string shellSource = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Game/Framework/Demo/Scripts/Core/DemoShellController.cs"));
            string shellCode = CSharpLexicalMap.Create(shellSource).CreateCodeOnlyText();
            Assert.AreEqual(4, Regex.Matches(shellCode, @"\bReleaseCurrentModule\s*\(\s*\)\s*;").Count,
                "OnDestroy、UIDocument 重建、切章与 Build 失败必须全部走统一释放出口。 ");
        }

        [Test]
        public void SharedResourceOperationGates_LiveOnModuleInstancesAcrossBuilds()
        {
            // IDemoModule 实例会反复 Build/Teardown；Build 局部 gate 会在 UIDocument 重建时归零，
            // 但旧异步取消可能仍在收尾。共享子 Bag、缓存或白盒文件步骤的互斥必须跟模块实例走。
            AssertInstanceGate(typeof(AssetReferenceModule), "_configOperationGate");
            AssertInstanceGate(typeof(AssetDownloadCacheModule), "_downloadOperationGate");
            AssertInstanceGate(typeof(AssetOpsFlowModule), "_operationGate");
            AssertInstanceGate(typeof(PoolDemoModule), "_poolMaintenanceGate");
            AssertInstanceGate(typeof(StorageDemoModule), "_profileOperationGate");
        }

        [Test]
        public void OperationGate_BlocksOverlap_AndStaleLeaseCannotReleaseNewOwner()
        {
            var gate = new DemoOperationGate();

            Assert.IsTrue(gate.TryEnter(out var first));
            Assert.IsTrue(gate.IsEntered);
            Assert.IsFalse(gate.TryEnter(out _), "已有 owner 时必须拒绝重入。 ");

            var staleCopy = first;
            first.Dispose();
            Assert.IsFalse(gate.IsEntered);
            Assert.IsTrue(gate.TryEnter(out var second));

            staleCopy.Dispose();
            Assert.IsTrue(gate.IsEntered,
                "旧异步续体迟到释放时，不能把后来取得闸门的新 owner 一并放行。 ");

            second.Dispose();
            Assert.IsFalse(gate.IsEntered);
        }

        private static void AssertInstanceGate(Type moduleType, string fieldName)
        {
            FieldInfo gate = moduleType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(gate,
                $"{moduleType.Name}.{fieldName} 必须是模块实例字段，才能跨 UIDocument 重建覆盖旧操作的取消收尾期。 ");
            Assert.AreEqual(typeof(DemoOperationGate), gate.FieldType,
                "共享操作必须使用带 owner 身份的租约闸门，不能退回迟到 finally 可误释放新流程的裸 bool。 ");
            Assert.IsFalse(gate.IsStatic);
        }

        private static void AssertActionOverloadGuards(string methodName)
        {
            AssertAsyncOverloadGuard(methodName, typeof(UniTask));
            AssertAsyncOverloadGuard(methodName, typeof(UniTaskVoid));
            AssertAsyncOverloadGuard(methodName, typeof(System.Threading.Tasks.Task));
            AssertAsyncOverloadGuard(methodName, typeof(System.Threading.Tasks.ValueTask));
            AssertGenericAsyncOverloadGuard(methodName, typeof(UniTask<>));
            AssertGenericAsyncOverloadGuard(methodName, typeof(System.Threading.Tasks.Task<>));
            AssertGenericAsyncOverloadGuard(methodName, typeof(System.Threading.Tasks.ValueTask<>));
        }

        private static void AssertAsyncOverloadGuard(string methodName, Type asyncReturnType)
        {
            MethodInfo guarded = null;
            foreach (MethodInfo method in typeof(DemoModuleHost).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || !parameters[1].ParameterType.IsGenericType) continue;
                if (parameters[1].ParameterType.GetGenericTypeDefinition() != typeof(Func<>)) continue;
                if (parameters[1].ParameterType.GetGenericArguments()[0] == asyncReturnType)
                {
                    guarded = method;
                    break;
                }
            }

            Assert.IsNotNull(guarded, $"{methodName} 缺少 {asyncReturnType.Name} 误用的编译期护栏重载。 ");
            var obsolete = guarded.GetCustomAttribute<ObsoleteAttribute>();
            Assert.IsNotNull(obsolete);
            Assert.IsTrue(obsolete.IsError, $"{asyncReturnType.Name} 护栏必须是编译错误，不能只是可忽略的 warning。 ");
        }

        private static void AssertGenericAsyncOverloadGuard(string methodName, Type asyncReturnTypeDefinition)
        {
            MethodInfo guarded = null;
            foreach (MethodInfo method in typeof(DemoModuleHost).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName || !method.IsGenericMethodDefinition) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || !parameters[1].ParameterType.IsGenericType) continue;
                if (parameters[1].ParameterType.GetGenericTypeDefinition() != typeof(Func<>)) continue;
                Type callbackReturn = parameters[1].ParameterType.GetGenericArguments()[0];
                if (callbackReturn.IsGenericType &&
                    callbackReturn.GetGenericTypeDefinition() == asyncReturnTypeDefinition)
                {
                    guarded = method;
                    break;
                }
            }

            Assert.IsNotNull(guarded,
                $"{methodName} 缺少 {asyncReturnTypeDefinition.Name} 误用的编译期护栏重载。 ");
            var obsolete = guarded.GetCustomAttribute<ObsoleteAttribute>();
            Assert.IsNotNull(obsolete);
            Assert.IsTrue(obsolete.IsError,
                $"{asyncReturnTypeDefinition.Name} 护栏必须是编译错误，不能只是可忽略的 warning。 ");
        }

        private static int FindMatchingParenthesis(string source, int openParen)
        {
            int depth = 0;
            for (int i = openParen; i < source.Length; i++)
            {
                if (source[i] == '(') depth++;
                else if (source[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static async UniTask VerifyHostButtonLifecycle()
        {
            var host = new DemoModuleHost(new VisualElement());
            var finished = new UniTaskCompletionSource();
            int invocationCount = 0;
            bool cancellationObserved = false;
            Button button = host.AddAsyncActionRow(
                "test",
                async ct =>
                {
                    invocationCount++;
                    try
                    {
                        await UniTask.DelayFrame(5, cancellationToken: ct);
                    }
                    finally
                    {
                        cancellationObserved = ct.IsCancellationRequested;
                        finished.TrySetResult();
                    }
                });

            SimulateClick(button);
            SimulateClick(button);

            Assert.AreEqual(1, invocationCount);
            Assert.IsFalse(button.enabledSelf, "任务未结束时按钮应禁用。 ");

            host.Dispose();
            await finished.Task;

            Assert.IsTrue(cancellationObserved, "Host.Dispose 必须把切章取消传给真实按钮回调。 ");
            Assert.IsFalse(button.enabledSelf, "宿主已销毁时无需重新启用脱离面板的按钮。 ");
        }

        private static async UniTask VerifyExceptionReporting()
        {
            var button = new Button();
            var expected = new InvalidOperationException("action-boom");
            Exception reported = null;
            var binding = new DemoAsyncActionBinding(
                button,
                _ => UniTask.FromException(expected),
                CancellationToken.None,
                "test",
                e => reported = e);

            await binding.Invoke();

            Assert.AreSame(expected, reported, "未处理异常必须交给统一报告接缝。 ");
            Assert.IsTrue(button.enabledSelf, "失败后仍应恢复按钮，允许修正条件后重试。 ");
        }

        private static async UniTask VerifyHostExceptionLogging()
        {
            var previousSinks = new List<ILogSink>(Log.Sinks);
            var sink = new CapturingSink();
            var host = new DemoModuleHost(new VisualElement());
            Log.ClearSinks();
            Log.AddSink(sink);
            try
            {
                Button syncButton = host.AddAsyncActionRow(
                    "sync-action",
                    _ => throw new InvalidOperationException("sync-boom"));
                Button asyncButton = host.AddAsyncActionRow(
                    "async-action",
                    async _ =>
                    {
                        await UniTask.Yield();
                        throw new InvalidOperationException("async-boom");
                    });

                SimulateClick(syncButton);
                SimulateClick(asyncButton);
                await UniTask.Yield();

                Assert.AreEqual(2, sink.Entries.Count, "同步抛出与 await 后抛出都应各记录一次。 ");
                Assert.AreEqual("DemoAction", sink.Entries[0].Category);
                StringAssert.Contains("sync-action", sink.Entries[0].Message);
                StringAssert.Contains("sync-boom", sink.Entries[0].Exception?.Message);
                Assert.AreEqual("DemoAction", sink.Entries[1].Category);
                StringAssert.Contains("async-action", sink.Entries[1].Message);
                StringAssert.Contains("async-boom", sink.Entries[1].Exception?.Message);
            }
            finally
            {
                host.Dispose();
                Log.ClearSinks();
                foreach (var previous in previousSinks) Log.AddSink(previous);
            }
        }

        private static async UniTask VerifyCatalogReleaseOrder()
        {
            CancellationToken actionToken = default;
            var started = new UniTaskCompletionSource();
            bool teardownCalled = false;
            Exception cancellationReentryException = null;
            Button button = null;
            DemoModuleCatalog catalog = null;
            var nextModule = new StubModule("next", 1, _ => { }, () => { });
            var module = new StubModule(
                "active",
                0,
                host => button = host.AddAsyncActionRow("long-running", async ct =>
                {
                    actionToken = ct;
                    using var registration = ct.Register(() =>
                    {
                        try
                        {
                            catalog.Activate(nextModule, new VisualElement());
                        }
                        catch (Exception e)
                        {
                            cancellationReentryException = e;
                        }
                    });
                    started.TrySetResult();
                    await UniTask.DelayFrame(1000, cancellationToken: ct);
                }),
                () =>
                {
                    teardownCalled = true;
                    Assert.IsTrue(actionToken.IsCancellationRequested,
                        "Module.Teardown 执行时，Host 的章节令牌必须已经取消。 ");
                });

            catalog = new DemoModuleCatalog(new[] { module, nextModule });
            using (catalog)
            {
                using var builder = new ContainerBuilder();
                catalog.InstallBindings(builder);
                using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
                catalog.Initialize(context);
                catalog.Activate(module, new VisualElement());

                SimulateClick(button);
                await started.Task;
                catalog.Deactivate();

                Assert.IsTrue(teardownCalled);
                Assert.IsInstanceOf<InvalidOperationException>(cancellationReentryException);
                Assert.AreEqual(0, nextModule.BuildCount,
                    "Host 取消回调不能在当前章节 Teardown 完成前激活下一章。 ");
                await UniTask.Yield(); // 让被取消的按钮回调完成 finally，避免把续体带到下一用例。
            }
        }

        private static void SimulateClick(Button button)
        {
            MethodInfo simulate = typeof(Clickable).GetMethod(
                "SimulateSingleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(simulate, "Unity UI Toolkit Clickable 的测试驱动入口已变化。 ");
            simulate.Invoke(button.clickable, new object[] { null, 0 });
        }

        private sealed class CapturingSink : ILogSink
        {
            internal readonly List<LogEntry> Entries = new();
            public LogLevel MinLevel => LogLevel.Error;
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private sealed class StubModule : IDemoModule
        {
            private readonly string _id;
            private readonly int _order;
            private readonly Action<DemoModuleHost> _onBuild;
            private readonly Action _onTeardown;
            internal StubModule(string id, int order, Action<DemoModuleHost> onBuild, Action onTeardown)
            {
                _id = id;
                _order = order;
                _onBuild = onBuild;
                _onTeardown = onTeardown;
            }
            public string Id => _id;
            public string Title => _id;
            public string Category => "入门";
            public int Order => _order;
            public string Summary => "Stub";
            public bool IsComingSoon => false;
            public DemoTeachingKind TeachingKind => DemoTeachingKind.Capability;
            public void InstallBindings(ContainerBuilder builder) { }
            public void Initialize(IGameContext context) { }
            internal int BuildCount { get; private set; }
            public void Build(DemoModuleHost host)
            {
                BuildCount++;
                host.AddPositioning("异步动作测试章节");
                _onBuild(host);
                host.AddSectionTitle("可运行内容");
                host.AddActionRow("同步占位动作", () => { });
                host.AddNote("异步动作由测试回调按需构建。");
                host.AddSectionTitle("边界");
                host.AddNote("切章时必须取消尚未完成的动作。");
            }
            public void Teardown() => _onTeardown();
        }
    }
}
