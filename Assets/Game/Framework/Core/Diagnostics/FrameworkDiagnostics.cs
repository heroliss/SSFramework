using System.Collections.Generic;
using System.Diagnostics;
using Game.Framework.Context;
using UnityEngine;

namespace Game.Framework.Diagnostics
{
    /// <summary>
    /// 框架诊断采集层（Editor 专用）：存活 <see cref="GameContext"/> 登记表 + <see cref="DisposableBag"/> 计数，
    /// 供编辑器「框架诊断面板」还原作用域树、观察泄漏趋势（ADR-0026）。
    /// </summary>
    /// <remarks>
    /// <b>只在 Unity Editor 下采集</b>：挂钩方法标 <c>[Conditional("UNITY_EDITOR")]</c>，玩家包（含 Development Build）
    /// 里调用点整体编译消除、登记表不存在——登记表持强引用会改变 GC 行为（未 Dispose 的 Context 永不回收），
    /// 这种「观察改变被观察者」的代价只允许发生在编辑器。真机诊断走 <see cref="FrameworkSelfCheck"/> 冒烟 +
    /// <c>Log</c> 日志（<see cref="Logging.Log"/>，配 <c>CaptureUnityLogs</c> + <c>FileLogSink</c> 可全量落盘）+
    /// <see cref="Systems.LoggingCommandSystem"/>（opt-in）。<br/>
    /// <b>登记表刻意持强引用不判活</b>：Context 构造登记、Dispose 注销——列表里长期存在且业务已不再使用的条目，
    /// 正是「创建了却忘记 Dispose」的泄漏本身，诊断面板要暴露的就是它。<br/>
    /// <b>线程契约</b>：主线程独占（与 Container 一致），不加锁。<br/>
    /// <b>Play 会话边界</b>：每次进入 Play（SubsystemRegistration 时机）清空——上一次 Play 泄漏的条目不跨会话残留，
    /// 关闭 Domain Reload 的 Enter Play Mode 设置下同样正确。
    /// </remarks>
    internal static class FrameworkDiagnostics
    {
#if UNITY_EDITOR
        private static readonly List<GameContext> _liveContexts = new();
        private static int _bagsAlive;
        private static long _bagsCreated;

        /// <summary>存活（已构造未 Dispose）的 GameContext，按创建顺序。仅诊断面板读取。</summary>
        internal static IReadOnlyList<GameContext> LiveContexts => _liveContexts;

        /// <summary>当前存活（已构造未 Dispose）的 DisposableBag 数。持续增长 = 泄漏嫌疑。</summary>
        internal static int BagsAlive => _bagsAlive;

        /// <summary>本次 Play 会话累计创建的 DisposableBag 数。</summary>
        internal static long BagsCreated => _bagsCreated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlaySession()
        {
            _liveContexts.Clear();
            _bagsAlive = 0;
            _bagsCreated = 0;
        }
#endif

        [Conditional("UNITY_EDITOR")]
        internal static void OnContextCreated(GameContext ctx)
        {
#if UNITY_EDITOR
            _liveContexts.Add(ctx);
#endif
        }

        [Conditional("UNITY_EDITOR")]
        internal static void OnContextDisposed(GameContext ctx)
        {
#if UNITY_EDITOR
            _liveContexts.Remove(ctx);
#endif
        }

        [Conditional("UNITY_EDITOR")]
        internal static void OnBagCreated()
        {
#if UNITY_EDITOR
            _bagsAlive++;
            _bagsCreated++;
#endif
        }

        [Conditional("UNITY_EDITOR")]
        internal static void OnBagDisposed()
        {
#if UNITY_EDITOR
            _bagsAlive--;
#endif
        }
    }
}
