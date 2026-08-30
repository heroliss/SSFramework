using System;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Systems;
using Game.Framework.Internal;
using Game.Framework.Command;
using Game.Framework.Event;
using NUnit.Framework;

namespace Game.Framework.Test
{
    /// <summary>
    /// 测试 Event 事件系统：发送、注册、取消订阅
    /// </summary>
    public class EventTests
    {
        private GameContext _gameContext;

        [SetUp]
        public void SetUp()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            _gameContext = new GameContext(builder.Build());
        }

        [TearDown]
        public void TearDown() => _gameContext?.Dispose();

        [Test]
        public void SendEvent_WithParameters_ShouldBeReceived()
        {
            string receivedMessage = null;
            int receivedValue = 0;

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var subscription = testSystem.RegisterEvent<TestEvent>(evt =>
            {
                receivedMessage = evt.Message;
                receivedValue = evt.Value;
            });

            testSystem.SendEvent(new TestEvent("hello", 42));

            Assert.AreEqual("hello", receivedMessage);
            Assert.AreEqual(42, receivedValue);

            subscription.Dispose();
        }

        [Test]
        public void SendEvent_EmptyEvent_ShouldBeReceived()
        {
            var received = false;

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var subscription = testSystem.RegisterEvent<EmptyEvent>(_ =>
            {
                received = true;
            });

            testSystem.SendEvent<EmptyEvent>();

            Assert.IsTrue(received);

            subscription.Dispose();
        }

        [Test]
        public void SendEvent_MultipleSubscribers_ShouldAllReceive()
        {
            var count1 = 0;
            var count2 = 0;

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var sub1 = testSystem.RegisterEvent<TestEvent>(_ => count1++);
            var sub2 = testSystem.RegisterEvent<TestEvent>(_ => count2++);

            testSystem.SendEvent(new TestEvent("multi", 1));

            Assert.AreEqual(1, count1);
            Assert.AreEqual(1, count2);

            sub1.Dispose();
            sub2.Dispose();
        }

        [Test]
        public void DisposedSubscription_DoesNotReceiveFurtherEvents()
        {
            var count = 0;

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var subscription = testSystem.RegisterEvent<TestEvent>(_ => count++);

            testSystem.SendEvent(new TestEvent());
            Assert.AreEqual(1, count);

            subscription.Dispose();

            testSystem.SendEvent(new TestEvent());
            Assert.AreEqual(1, count); // 不应再增长
        }

        [Test]
        public void RegisterEvent_AfterContextDispose_Throws()
        {
            _gameContext.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => _gameContext.RegisterEvent<TestEvent>(_ => { }),
                "已销毁的事件总线不得创建一个永远不会被 Context 回收的新 Subject/订阅");
        }

        [Test]
        public void SendEvent_FromCommand_ShouldBeReceived()
        {
            string received = null;

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var subscription = testSystem.RegisterEvent<TestEvent>(evt => received = evt.Message);

            _gameContext.ExecuteCommand(new EventSendingCommand { Message = "from_cmd" });

            Assert.AreEqual("from_cmd", received);

            subscription.Dispose();
        }

        [Test]
        public void View_CanRegisterEvent_ShouldReceiveEvent()
        {
            string received = null;

            var view = new TestView();
            _gameContext.Inject(view);
            _gameContext.AttachTo(view);
            var subscription = view.RegisterEvent<TestEvent>(evt => received = evt.Message);

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            testSystem.SendEvent(new TestEvent("view_event", 0));

            Assert.AreEqual("view_event", received);

            subscription.Dispose();
        }

        /// <summary>
        /// 不同事件类型的 Subject 互相隔离：发送 TestEvent 不触发 EmptyEvent 订阅者。
        /// </summary>
        [Test]
        public void SendEvent_DifferentTypes_ShouldNotInterfere()
        {
            var testEventReceived = false;
            var emptyEventReceived = false;

            var testSystem = new EventTestSystem();
            _gameContext.Inject(testSystem);
            _gameContext.AttachTo(testSystem);
            var sub1 = testSystem.RegisterEvent<TestEvent>(_ => testEventReceived = true);
            var sub2 = testSystem.RegisterEvent<EmptyEvent>(_ => emptyEventReceived = true);

            testSystem.SendEvent(new TestEvent("only_test", 0));

            Assert.IsTrue(testEventReceived);
            Assert.IsFalse(emptyEventReceived);

            sub1.Dispose();
            sub2.Dispose();
        }

        /// <summary>
        /// GameContext Dispose 后所有订阅停止接收事件。
        /// </summary>
        [Test]
        public void Dispose_ShouldCleanupAllSubscriptions()
        {
            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), new[] { typeof(ICommandSystem) });
            using var ctx = new GameContext(builder.Build());

            var count = 0;
            var subscription = ctx.RegisterEvent<TestEvent>(_ => count++);

            ctx.SendEvent(new TestEvent("before", 0));
            Assert.AreEqual(1, count);

            ctx.Dispose();
            ctx.SendEvent(new TestEvent("after", 0));
            Assert.AreEqual(1, count);

            subscription.Dispose();
        }

        /// <summary>
        /// 测试用 System，用于发送和接收事件。通过 GameContext.AttachTo 设置上下文。
        /// </summary>
        private class EventTestSystem : Game.Framework.Systems.ISystem, IHasGameContext
        {
            private GameContext _ctx;
            IGameContext IHasGameContext.Context => _ctx;
        }

        /// <summary>
        /// 测试用 Command，在 Execute 中通过 ctx 参数发送事件。
        /// </summary>
        private class EventSendingCommand : ICommand
        {
            public string Message;

            public void Execute(ICommandContext ctx)
            {
                ctx.SendEvent(new TestEvent(Message, 0));
            }
        }
    }
}
