using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework;
using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Event;
using Game.Framework.Internal;
using Game.Framework.Logging;
using Game.Framework.Model;
using Game.Framework.Systems;
using R3;
using UnityEngine;
using DisposableBag = Game.Framework.DisposableBag; // 与 R3.DisposableBag 同名，显式指向框架版

namespace Game.Framework.Diagnostics
{
    /// <summary>
    /// 框架真机自检：把框架最依赖反射 / 跨 AOT 泛型的链路在当前构建下各跑一遍——DI 容器、<c>RP&lt;T&gt;</c> + R3
    /// 订阅、struct Command 双泛型零装箱分发、class Command 注入、事件总线与 UniTask 异步命令——
    /// 逐项 ✓/✗ 落日志、屏显并在原生 Inspector 可视化。
    ///
    /// <para>定位：**可选的诊断组件**，不是启动必需。接入 IL2CPP / 热更链路时挂上它做一次性冒烟（验证框架基元
    /// 在当前执行模式下端到端可用，ADR-0008 §6 的验证重点）；上线前把组件移除即可。自检用到的最小层 / 命令都是
    /// 本类的私有嵌套类型，不污染框架公共命名空间。</para>
    ///
    /// <para>用法：<c>gameObject.AddComponent&lt;FrameworkSelfCheck&gt;()</c> 即自动在 <see cref="Start"/> 跑一遍；
    /// 可先设 <see cref="Caption"/> 作抬头（如入口版本号）。自检 Context 自给自足、不依赖 <c>GameContext.Main</c>。</para>
    /// </summary>
    public sealed class FrameworkSelfCheck : MonoBehaviour
    {
        [Tooltip("屏显 / Inspector 抬头，可由入口设置（如热更版本号），方便肉眼比对玩家包跑的是哪一版。")]
        public string Caption = "FrameworkSelfCheck";

        private readonly List<string> _results = new();
        private GameContext _ctx;
        private DisposableBag _bag;

        internal string KernelAssembly => typeof(GameContext).Assembly.GetName().Name;

        internal string Environment => $"{Application.platform}{(Application.isEditor ? "（编辑器旁路）" : "")}";

        internal bool AllOk => _results.Count > 0 && !_results.Exists(r => r.StartsWith("✗", StringComparison.Ordinal));

        internal IReadOnlyList<string> Results => _results;

        private void Start() => Run();

        /// <summary>跑一遍全部自检（同步项立即出结果、异步项完成后追加）。可重复调用：每次先清理上一轮再重跑。</summary>
        public void Run()
        {
            // 重跑前清掉上一轮（Inspector 按钮可能多次触发），避免 Context / 订阅泄漏。
            _bag?.Dispose();
            _ctx?.Dispose();
            _results.Clear();

            // 自检专用 Context：自给自足，不依赖全局（玩家包里此刻可能还没有 GameContext.Main）。
            var model = new CheckModel();
            var system = new CheckSystem();
            using var builder = new ContainerBuilder();
            builder.RegisterModel(model);
            builder.RegisterSystem(system);
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            // 值绑定实例（model / system）由 GameContext 构造时统一 Inject + AttachTo（ADR-0019），无需手动补。
            _ctx = new GameContext(builder.Build(), inheritFromGlobal: false) { DebugName = nameof(FrameworkSelfCheck) };
            _bag = new DisposableBag(_ctx);

            Check("DI 容器注册/解析", () =>
            {
                if (!ReferenceEquals(_ctx.GetModel<CheckModel>(), model)) throw new Exception("GetModel 实例不一致");
                if (!ReferenceEquals(_ctx.GetSystem<CheckSystem>(), system)) throw new Exception("GetSystem 实例不一致");
            });

            Check("RP<int> + R3 订阅（跨 AOT 泛型）", () =>
            {
                int received = -1;
                _bag.Subscribe(_ctx.GetModel<CheckModel>().Count, v => received = v);
                if (received != 0) throw new Exception($"订阅未收到初值（got {received}）");
                _ctx.GetModel<CheckModel>().Count.Value = 42;
                if (received != 42) throw new Exception($"订阅未收到推送（got {received}）");
            });

            Check("struct Command 分发", () =>
            {
                _ctx.ExecuteCommand(new AddStructCommand(8));
                if (_ctx.GetModel<CheckModel>().Count.Value != 50) throw new Exception("命令未生效");
            });

            Check("struct Command 双泛型返回值（零装箱）", () =>
            {
                int v = _ctx.ExecuteCommand<ReadCountStructCommand, int>(new ReadCountStructCommand());
                if (v != 50) throw new Exception($"返回值错误（got {v}）");
            });

            Check("class Command [Inject] 注入", () =>
            {
                _ctx.ExecuteCommand(new InjectClassCommand());
                if (_ctx.GetModel<CheckModel>().Count.Value != 51) throw new Exception("注入字段未生效");
            });

            Check("事件总线 SendEvent/Subscribe", () =>
            {
                int got = 0;
                _bag.Subscribe<CountReachedEvent>(e => got = e.Value);
                _ctx.SendEvent(new CountReachedEvent { Value = 7 });
                if (got != 7) throw new Exception($"事件未送达（got {got}）");
            });

            RunAsyncChecks().Forget(ex =>
            {
                _results.Add($"✗ 异步命令（UniTask）：{ex.GetType().Name} {ex.Message}");
                LogSummary();
            });
        }

        private async UniTask RunAsyncChecks()
        {
            // 解释器下的 async 状态机 + AOT UniTask 跨界：分别跑 struct 异步命令与带返回值异步命令。
            await _ctx.ExecuteCommandAsync(new DelayAddStructCommand(9));
            if (_ctx.GetModel<CheckModel>().Count.Value != 60)
                throw new Exception($"struct 异步命令未生效（Count={_ctx.GetModel<CheckModel>().Count.Value}）");

            int echoed = await _ctx.ExecuteCommandAsync(new EchoAsyncCommand { Input = 123 });
            if (echoed != 123) throw new Exception($"异步返回值错误（got {echoed}）");

            _results.Add("✓ 异步命令（UniTask 解释器状态机）");
            LogSummary();
        }

        private void Check(string name, Action body)
        {
            try
            {
                body();
                _results.Add("✓ " + name);
            }
            catch (Exception e)
            {
                _results.Add($"✗ {name}：{e.GetType().Name} {e.Message}");
            }
        }

        private void LogSummary()
        {
            string summary = $"[FrameworkSelfCheck] {Caption} 自检完成 allOk={AllOk}\n" +
                             string.Join("\n", _results);
            if (AllOk) Log.Info(summary, nameof(FrameworkSelfCheck), this);
            else Log.Error(summary, category: nameof(FrameworkSelfCheck), context: this);
        }

        private void OnGUI()
        {
            GUI.Label(new Rect(10, 70, Screen.width - 20, 24), $"{Caption} · 框架内核：{KernelAssembly}");
            for (int i = 0; i < _results.Count; i++)
                GUI.Label(new Rect(10, 98 + i * 22, Screen.width - 20, 22), _results[i]);
        }

        private void OnDestroy()
        {
            _bag?.Dispose();
            _ctx?.Dispose();
        }

        // ───────────── 自检用的最小层与命令（私有嵌套，全部是普通托管类型，不进框架公共面） ─────────────

        private sealed class CheckModel : IModel
        {
            public RP<int> Count { get; } = new(0);
        }

        private sealed class CheckSystem : ISystem
        {
            public void Add(CheckModel model, int delta) => model.Count.Value += delta;
        }

        private readonly struct AddStructCommand : ICommand
        {
            private readonly int _delta;
            public AddStructCommand(int delta) => _delta = delta;

            public void Execute(ICommandContext ctx)
                => ctx.GetSystem<CheckSystem>().Add(ctx.GetModel<CheckModel>(), _delta);
        }

        private readonly struct ReadCountStructCommand : ICommand<int>
        {
            public int Execute(ICommandContext ctx) => ctx.GetModel<CheckModel>().Count.Value;
        }

        private sealed class InjectClassCommand : ICommand
        {
            [Inject] private CheckModel _model;
            [Inject] private CheckSystem _system;

            public void Execute(ICommandContext ctx) => _system.Add(_model, 1);
        }

        private readonly struct DelayAddStructCommand : IAsyncCommand
        {
            private readonly int _delta;
            public DelayAddStructCommand(int delta) => _delta = delta;

            public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
            {
                await UniTask.Delay(100, cancellationToken: cancellationToken);
                ctx.GetSystem<CheckSystem>().Add(ctx.GetModel<CheckModel>(), _delta);
            }
        }

        private sealed class EchoAsyncCommand : IAsyncCommand<int>
        {
            public int Input;

            public async UniTask<int> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
            {
                await UniTask.Delay(50, cancellationToken: cancellationToken);
                return Input;
            }
        }

        private sealed class CountReachedEvent : IEvent
        {
            public int Value;
        }

    }
}
