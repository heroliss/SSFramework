using System;
using System.Net;
using System.Net.Sockets;
using Game.Framework.Demo.Modules.Services;
using NUnit.Framework;

namespace Game.Framework.Demo.Tests
{
    /// <summary>锁定内嵌服务器最容易被 Editor/Domain Reload 放大的端口与清理契约。</summary>
    public sealed class DemoGameServerTests
    {
        [Test]
        public void FirstHttpPortOccupied_FallsBackAndStartsCompleteServer()
        {
            TcpListener blocker = null;
            try
            {
                // TcpListener 占位能稳定复现 Unity Mono 下 HttpListener.Start 直接抛 SocketException 的路径。
                blocker = new TcpListener(IPAddress.Loopback, 18400);
                try { blocker.Start(); }
                catch (SocketException)
                {
                    // 当前机器已有进程/上次 Editor 残留占位，也满足本用例前置；不接管外部 listener。
                    blocker = null;
                }

                using var server = new DemoGameServer();
                int selectedPort = new Uri(server.HttpBaseUrl).Port;

                Assert.IsTrue(server.IsRunning);
                Assert.That(selectedPort, Is.InRange(18401, 18459),
                    "18400 冲突必须回退到后续端口，不能让 Mono SocketException 中断整个 Demo");
                Assert.That(new Uri(server.WsUrl).Port, Is.GreaterThan(0),
                    "构造成功意味着 HTTP 与 WebSocket 两个 listener 都已完整启动");
            }
            finally
            {
                try { blocker?.Stop(); } catch { }
            }
        }

        [Test]
        public void Dispose_IsIdempotent_AndPublishesStoppedState()
        {
            var server = new DemoGameServer();

            Assert.DoesNotThrow(server.Dispose);
            Assert.DoesNotThrow(server.Dispose);
            Assert.IsFalse(server.IsRunning);
        }
    }
}
