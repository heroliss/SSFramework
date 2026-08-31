using System.Diagnostics;
using Cysharp.Threading.Tasks;
using Game.Framework.Logging;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 框架统一的「主线程独占」契约断言点。Container / InjectionPlan / LayerInterfacesCache 等
    /// 共享静态缓存与 Unity 对象的基础设施都不加锁——框架的 Awake / OnDestroy / Command / Event / UI 路径
    /// 全在 Unity 主线程跑，hot path 不付并发开销；本类在 Editor / Development Build 下兜底检测跨线程误用，
    /// Release 编译消除。位于 <c>Internal</c> 命名空间，供可选 Framework Module 复用，不是业务层的调度 API。
    /// </summary>
    /// <remarks>
    /// 主线程身份取自 UniTask 已安装的 Unity PlayerLoop，不依赖“首次触达本类”的线程时机。
    /// 业务如需从工作线程调框架，先 <c>await UniTask.SwitchToMainThread()</c>。
    /// </remarks>
    public static class MainThreadGuard
    {
        /// <summary>断言当前在 Unity 主线程。Editor 与 Development Build 下生效，Release 编译消除、零开销。</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AssertMainThread(string who)
        {
            if (!PlayerLoopHelper.IsMainThread)
                Log.Error(
                    "此 API 非线程安全，必须从 Unity 主线程访问；" +
                    "请先 await UniTask.SwitchToMainThread()。",
                    category: who);
        }
    }
}
