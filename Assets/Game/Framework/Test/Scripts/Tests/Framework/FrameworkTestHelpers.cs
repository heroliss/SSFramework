using System.Threading;
using Game.Framework.Context;
using Game.Framework.Common;
using Game.Framework.Internal;
using Game.Framework.Command;
using Game.Framework.Event;
using Game.Framework.Model;
using Game.Framework.System;
using Game.Framework.Utility;
using Game.Framework.View;
using Cysharp.Threading.Tasks;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试用 Model
    /// </summary>
    public class TestModel : IModel
    {
        public string Value = "default";
        public int Counter;
    }

    /// <summary>
    /// 测试用 System。实现 IHasGameContext，通过 GameContext.AttachTo 设置上下文。
    /// </summary>
    public class TestSystem : ISystem, IHasGameContext
    {
        private GameContext _ctx;
        IGameContext IHasGameContext.Context => _ctx;

        public int CallCount;

        public string GetGreeting(string name)
        {
            CallCount++;
            var model = this.GetModel<TestModel>();
            return $"Hello {name}, model value: {model.Value}";
        }

        public void UpdateModelValue(string value)
        {
            var model = this.GetModel<TestModel>();
            model.Value = value;
            model.Counter++;
        }
    }

    /// <summary>
    /// 测试用 Utility
    /// </summary>
    public class TestUtility : IUtility
    {
        public string Name = "default_utility";
        public int CallCount;
    }

    /// <summary>
    /// 测试用 System（依赖另一个 System）
    /// </summary>
    public class DependentSystem : ISystem, IHasGameContext
    {
        private GameContext _ctx;
        IGameContext IHasGameContext.Context => _ctx;

        public string GetCombinedGreeting(string name)
        {
            var testSystem = this.GetSystem<TestSystem>();
            var model = this.GetModel<TestModel>();
            return $"{testSystem.GetGreeting(name)}, counter: {model.Counter}";
        }
    }

    /// <summary>
    /// 测试用同步 Command（无返回值）。class 支持 [Inject]，此处演示直接通过 ctx 参数访问层。
    /// </summary>
    public class TestCommand : ICommand
    {
        public string Name;

        public void Execute(ICommandContext ctx)
        {
            var system = ctx.GetSystem<TestSystem>();
            system.UpdateModelValue(Name);
        }
    }

    /// <summary>
    /// 测试用同步 Command（有返回值）
    /// </summary>
    public class TestResultCommand : ICommand<string>
    {
        public string Name;

        public string Execute(ICommandContext ctx)
        {
            var system = ctx.GetSystem<TestSystem>();
            return system.GetGreeting(Name);
        }
    }

    /// <summary>
    /// 测试用异步 Command（无返回值）
    /// </summary>
    public class TestAsyncCommand : IAsyncCommand
    {
        public string Name;
        public int DelayMs;

        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(DelayMs, cancellationToken: cancellationToken);
            var system = ctx.GetSystem<TestSystem>();
            system.UpdateModelValue(Name);
        }
    }

    /// <summary>
    /// 测试用异步 Command（有返回值）
    /// </summary>
    public class TestAsyncResultCommand : IAsyncCommand<string>
    {
        public string Name;
        public int DelayMs;

        public async UniTask<string> ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(DelayMs, cancellationToken: cancellationToken);
            var system = ctx.GetSystem<TestSystem>();
            return system.GetGreeting(Name);
        }
    }

    /// <summary>
    /// 测试用类 Command（支持 [Inject]）。class 的 [Inject] 字段由 CommandSystem 在 Execute 前自动注入。
    /// </summary>
    public class TestInjectCommand : ICommand
    {
        [Inject] public TestSystem TestSystem;
        [Inject] public TestModel TestModel;

        public string Name;

        public void Execute(ICommandContext ctx)
        {
            TestModel.Value = Name;
            TestModel.Counter = TestSystem.CallCount + 1;
        }
    }

    /// <summary>
    /// 测试用 struct Command（无返回值）。
    /// struct 不能用 [Inject]（反射写字段只修改装箱副本，原值不变），
    /// 只能通过 ctx 参数访问层，零装箱零分配。
    /// </summary>
    public readonly struct TestStructCommand : ICommand
    {
        public readonly string Name;

        public TestStructCommand(string name) => Name = name;

        public void Execute(ICommandContext ctx)
        {
            var system = ctx.GetSystem<TestSystem>();
            system.UpdateModelValue(Name);
        }
    }

    /// <summary>
    /// 测试用 struct Command（有返回值）。演示 ICommand&lt;TResult&gt; + 双泛型重载，零装箱。
    /// </summary>
    public readonly struct TestStructResultCommand : ICommand<string>
    {
        public readonly string Name;
        public TestStructResultCommand(string name) => Name = name;

        public string Execute(ICommandContext ctx)
        {
            var system = ctx.GetSystem<TestSystem>();
            return system.GetGreeting(Name);
        }
    }

    /// <summary>
    /// 测试用异步 struct Command。演示 struct + UniTask 零装箱异步。
    /// </summary>
    public readonly struct TestStructAsyncCommand : IAsyncCommand
    {
        public readonly string Name;
        public readonly int DelayMs;
        public TestStructAsyncCommand(string name, int delayMs) { Name = name; DelayMs = delayMs; }

        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(DelayMs, cancellationToken: cancellationToken);
            ctx.GetSystem<TestSystem>().UpdateModelValue(Name);
        }
    }

    /// <summary>
    /// 测试用 Command（类 + [Inject] + 异步）。验证 class AsyncCommand 的 [Inject] 注入路径。
    /// </summary>
    public class TestInjectAsyncCommand : IAsyncCommand
    {
        [Inject] public TestSystem TestSystem;
        public string Name;
        public int DelayMs;

        public async UniTask ExecuteAsync(ICommandContext ctx, CancellationToken cancellationToken)
        {
            await UniTask.Delay(DelayMs, cancellationToken: cancellationToken);
            TestSystem.UpdateModelValue(Name);
        }
    }

    /// <summary>
    /// 测试用 Command（链式执行）。在 Execute 内通过 ctx 执行另一个 Command。
    /// </summary>
    public class TestChainCommand : ICommand
    {
        public string Name;

        public void Execute(ICommandContext ctx)
        {
            ctx.ExecuteCommand(new TestCommand { Name = Name + "_inner" });
            ctx.GetSystem<TestSystem>().UpdateModelValue(Name);
        }
    }

    /// <summary>
    /// 测试用 Event
    /// </summary>
    public record struct TestEvent(string Message, int Value) : IEvent;

    /// <summary>
    /// 测试用无参 Event
    /// </summary>
    public record struct EmptyEvent : IEvent;

    /// <summary>
    /// 测试用 View。实现 IHasGameContext，通过 GameContext.AttachTo 设置上下文。
    /// </summary>
    public class TestView : IView, IHasGameContext
    {
        private GameContext _ctx;
        IGameContext IHasGameContext.Context => _ctx;

        public string LastResult;

        public void DoExecuteCommand(string name)
        {
            this.ExecuteCommand(new TestCommand { Name = name });
        }

        public string DoExecuteResultCommand(string name)
        {
            return this.ExecuteCommand(new TestResultCommand { Name = name });
        }

        public void DoExecuteAsyncCommand(string name, int delayMs = 0)
        {
            this.ExecuteCommandAsync(new TestAsyncCommand { Name = name, DelayMs = delayMs });
        }

        public async UniTask<string> DoExecuteAsyncResultCommand(string name, int delayMs = 0)
        {
            return await this.ExecuteCommandAsync(new TestAsyncResultCommand { Name = name, DelayMs = delayMs });
        }
    }
}
