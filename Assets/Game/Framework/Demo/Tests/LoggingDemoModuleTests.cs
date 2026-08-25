using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules;
using Game.Framework.Internal;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Tests
{
    /// <summary>锁定日志教学章节对进程级门面的借用/归还边界，避免离章污染业务日志配置。</summary>
    public sealed class LoggingDemoModuleTests
    {
        [Test]
        public void Teardown_RestoresCaptureAndMinLevelThatAlreadyExistedBeforeTheChapter()
        {
            var originalCapture = Log.IsCapturingUnityLogs;
            var originalMinLevel = Log.MinLevel;
            try
            {
                Log.CaptureUnityLogs(true);
                Log.MinLevel = LogLevel.Warning;

                using var builder = new ContainerBuilder();
                using var context = new GameContext(builder.Build(), inheritFromGlobal: false);
                var module = new LoggingDemoModule();
                module.Initialize(context);
                try
                {
                    using var host = new DemoModuleHost(new VisualElement());
                    module.Build(host);
                    Log.MinLevel = LogLevel.Trace; // 模拟用户在章节内调总闸门。
                }
                finally
                {
                    // 即使 Build 或断言前步骤失败，也不能把 sink / scheduler / 全局状态泄漏给后续测试。
                    module.Teardown();
                }

                Assert.IsTrue(Log.IsCapturingUnityLogs,
                    "章节进入前已有的 Unity 日志接管不能被 Demo 擅自关闭。");
                Assert.AreEqual(LogLevel.Warning, Log.MinLevel,
                    "章节内调整的总闸门应在离章时恢复。 ");
            }
            finally
            {
                Log.CaptureUnityLogs(originalCapture);
                Log.MinLevel = originalMinLevel;
            }
        }
    }
}
