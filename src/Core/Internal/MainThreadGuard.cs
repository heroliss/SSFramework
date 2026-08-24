using System.Diagnostics;
using System.Threading;
using Game.Framework.Logging;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 框架统一的「主线程独占」契约断言点。Container / InjectionPlan / LayerInterfacesCache 等
    /// 共享静态缓存的基础设施都不加锁——框架的 Awake / OnDestroy / Command / Event 路径全在 Unity 主线程跑，
    /// hot path 不付并发开销；本类在 Editor / Development Build 下兜底检测跨线程误用，Release 编译消除。
    /// </summary>
    /// <remarks>
    /// 主线程 ID 在本类首次被触达时捕获——正常路径下首次触达一定来自主线程的容器解析或注册。
    /// 业务如需从工作线程调框架，先 <c>await UniTask.SwitchToMainThread()</c>。
    /// </remarks>
    internal static class MainThreadGuard
    {
        private static readonly int s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>断言当前在 Unity 主线程。Editor 与 Development Build 下生效，Release 编译消除、零开销。</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AssertMainThread(string who)
        {
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                Log.Error(
                    "Not thread-safe; must be accessed from the Unity main thread " +
                    "(await UniTask.SwitchToMainThread() first).",
                    category: who);
        }
    }
}
