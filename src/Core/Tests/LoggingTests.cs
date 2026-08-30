using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证框架日志门面（ADR-0034）：分级 / 多播 / 过滤 / Trace 门控 / 异常隔离 / Unity 日志流接管，
    /// 以及内核 <see cref="FileLogSink"/> 的落盘与滚动。
    /// </summary>
    /// <remarks>
    /// 本程序集（<c>Game.Framework.Tests</c>）与 <c>Game.Framework</c> 是**不同程序集**，且**没有**声明
    /// 插值处理器所需的 polyfill attribute——因此这里的 <c>Log.Trace($"...")</c> 同时充当
    /// 「处理器能否跨程序集被调用方编译器识别」的验证（见 <see cref="Trace_Interpolation_IsLazy_WhenDisabled"/>）。
    /// </remarks>
    public class LoggingTests
    {
        // 收集投递到本 sink 的日志，供断言。
        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel { get; set; } = LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();
            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private sealed class ThrowingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public void Log(in LogEntry entry) => throw new InvalidOperationException("sink boom");
        }

        private sealed class ThrowingMinLevelSink : ILogSink
        {
            public LogLevel MinLevel => throw new InvalidOperationException("min-level boom");
            public void Log(in LogEntry entry) { }
        }

        // 插值惰性求值的探针：被求值就自增。
        private static int _touchCount;
        private static string Touch()
        {
            _touchCount++;
            return "touched";
        }

        [SetUp]
        public void SetUp()
        {
            // Log 是全局静态——每个用例先清干净，避免默认 UnityDebugLogSink 往 Console 刷 + 用例间串味。
            Log.ClearSinks();
            Log.MinLevel = LogLevel.Info;
            Log.CaptureUnityLogs(false);
            _touchCount = 0;
        }

        [TearDown]
        public void TearDown()
        {
            // 恢复出厂默认（一个 UnityDebugLogSink），不给后续测试留下被清空 / 被接管的日志系统。
            Log.CaptureUnityLogs(false);
            Log.ClearSinks();
            Log.AddSink(new UnityDebugLogSink());
            Log.MinLevel = LogLevel.Info;
        }

        // ── 多播 / 过滤 ──────────────────────────────────────────────────

        [Test]
        public void Info_BroadcastsToAllSinks()
        {
            var a = new CapturingSink();
            var b = new CapturingSink();
            Log.AddSink(a);
            Log.AddSink(b);

            Log.Info("hello", "Cat");

            Assert.AreEqual(1, a.Entries.Count);
            Assert.AreEqual(1, b.Entries.Count, "一条日志应广播到每个 sink");
            Assert.AreEqual("hello", a.Entries[0].Message);
            Assert.AreEqual("Cat", a.Entries[0].Category);
            Assert.AreEqual(LogLevel.Info, a.Entries[0].Level);
        }

        [Test]
        public void MinLevel_FiltersBelowThreshold()
        {
            var warnOnly = new CapturingSink { MinLevel = LogLevel.Warning };
            Log.AddSink(warnOnly);

            Log.Info("info");         // 低于阈值，被挡
            Log.Warning("warn");      // 命中
            Log.Error("err");         // 高于阈值，命中

            Assert.AreEqual(2, warnOnly.Entries.Count, "低于 MinLevel 的条目不应投递");
            Assert.AreEqual(LogLevel.Warning, warnOnly.Entries[0].Level);
            Assert.AreEqual(LogLevel.Error, warnOnly.Entries[1].Level);
        }

        [Test]
        public void RemoveSink_StopsDelivery()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);
            Log.Info("first");

            Assert.IsTrue(Log.RemoveSink(sink));
            Log.Info("second");

            Assert.AreEqual(1, sink.Entries.Count, "移除后不再收到");
            Assert.AreEqual("first", sink.Entries[0].Message);
            Assert.IsFalse(Log.RemoveSink(sink), "重复移除返回 false");
        }

        /// <summary>
        /// 自省 API（<see cref="Log.Sinks"/> / <see cref="Log.IsCapturingUnityLogs"/>）：
        /// sink 与「是否接管 Unity 日志流」都是业务在启动期用代码装配的，出问题时
        /// （「我的日志怎么没落盘？」）得有地方能查是压根没装、还是被 MinLevel 卡掉了。
        /// 「框架诊断面板」的日志一栏读的就是这两个。
        /// </summary>
        [Test]
        public void Sinks_And_IsCapturingUnityLogs_ReflectCurrentState()
        {
            Assert.AreEqual(0, Log.Sinks.Count, "SetUp 已 ClearSinks");
            Assert.IsFalse(Log.IsCapturingUnityLogs);

            var sink = new CapturingSink { MinLevel = LogLevel.Warning };
            Log.AddSink(sink);
            Assert.AreEqual(1, Log.Sinks.Count);
            Assert.AreSame(sink, Log.Sinks[0]);
            Assert.AreEqual(LogLevel.Warning, Log.Sinks[0].MinLevel, "面板要显示每个 sink 的 MinLevel");

            Log.CaptureUnityLogs(true);
            Assert.IsTrue(Log.IsCapturingUnityLogs);
            Log.CaptureUnityLogs(false);
            Assert.IsFalse(Log.IsCapturingUnityLogs, "关掉后应如实反映（幂等）");

            Log.RemoveSink(sink);
            Assert.AreEqual(0, Log.Sinks.Count);
        }

        [Test]
        public void Sinks_ReturnsStableReadOnlySnapshot_NotMutableBackingArray()
        {
            var first = new CapturingSink();
            var second = new CapturingSink();
            Log.AddSink(first);

            var snapshot = Log.Sinks;
            Assert.IsFalse(snapshot is ILogSink[],
                "自省 API 不得泄漏内部 copy-on-write 数组，否则强转后可绕过锁篡改投递路由");
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreSame(first, snapshot[0]);

            Log.AddSink(second);
            Assert.AreEqual(1, snapshot.Count,
                "已取得的自省视图应保持同一代快照，不随后续注册原地变化");
            Assert.AreEqual(2, Log.Sinks.Count);

            if (snapshot is IList<ILogSink> list)
                Assert.Throws<NotSupportedException>(() => list[0] = second,
                    "即使调用方强转到 IList，只读快照也应拒绝写入");
        }

        [Test]
        public void IsEnabled_FalseWhenEverySinkFiltersLevelOut()
        {
            Log.AddSink(new CapturingSink { MinLevel = LogLevel.Error });

            Assert.IsFalse(Log.IsEnabled(LogLevel.Info), "所有 sink 的分闸门都高于它 → 记了也没人收");
            Assert.IsTrue(Log.IsEnabled(LogLevel.Error));
        }

        /// <summary>
        /// 总闸门（<see cref="Log.MinLevel"/>）与分闸门（<see cref="ILogSink.MinLevel"/>）是**串联**的：
        /// 一条日志要**同时**过两道。这也是「<c>Verbose</c> 布尔被级别体系吸收」后新获得的能力——
        /// 全局压掉 Info 噪音，不必逐个去改 sink（原来的 bool 做不到）。
        /// </summary>
        [Test]
        public void GlobalMinLevel_GatesEveryLevel_EvenWhenSinkAcceptsIt()
        {
            var sink = new CapturingSink { MinLevel = LogLevel.Trace };   // 分闸门全开
            Log.AddSink(sink);

            Log.MinLevel = LogLevel.Warning;   // 总闸门只放 Warning 及以上
            Log.Info("info");                  // 分闸门全开，但过不了总闸门
            Log.Warning("warn");

            Assert.AreEqual(1, sink.Entries.Count, "sink 的分闸门再低，也得先过总闸门");
            Assert.AreEqual(LogLevel.Warning, sink.Entries[0].Level);
            Assert.IsFalse(Log.IsEnabled(LogLevel.Info), "总闸门挡住时 IsEnabled 应为 false（调用点据此跳过昂贵构造）");
        }

        // ── Trace 门控 + 插值惰性求值（跨程序集验证处理器）─────────────────

        [Test]
        public void Trace_OnlyDeliveredWhenGlobalGateAllowsIt()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            Log.MinLevel = LogLevel.Info;
            Log.Trace("noise");
            Assert.AreEqual(0, sink.Entries.Count, "总闸门未放行到 Trace 时 Trace 不投递");

            // 测试在 Editor 下跑（UNITY_EDITOR 为真，[Conditional] 不会剥掉调用），故总闸门放行到 Trace 后应投递。
            Log.MinLevel = LogLevel.Trace;
            Log.Trace("visible");
            Assert.AreEqual(1, sink.Entries.Count, "总闸门放行到 Trace 时 Trace 投递");
            Assert.AreEqual(LogLevel.Trace, sink.Entries[0].Level);
        }

        /// <summary>
        /// 本用例是整套插值处理器设计的地基验证，一箭双雕：<br/>
        /// ① <b>惰性求值</b>——总闸门未放行到 Trace 时 <c>$"..."</c> 里的 <c>Touch()</c> 一次都不该被调用
        ///    （编译器在调用点插了 <c>if (shouldAppend)</c> 守卫）；<br/>
        /// ② <b>跨程序集识别</b>——本测试程序集没有声明 polyfill attribute，若编译器仍把
        ///    <c>Log.Trace($"...")</c> 绑到处理器重载（而不是 string 重载），说明处理器可跨程序集正常工作。
        ///    若绑错到 string 重载，<c>_touchCount</c> 会变成 1，本用例即失败。
        /// </summary>
        [Test]
        public void Trace_Interpolation_IsLazy_WhenDisabled()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            Log.MinLevel = LogLevel.Info;
            Log.Trace($"noise {Touch()}");
            Assert.AreEqual(0, _touchCount, "总闸门未放行到 Trace 时插值表达式不应求值——否则处理器没生效（绑到了 string 重载）");
            Assert.AreEqual(0, sink.Entries.Count);

            Log.MinLevel = LogLevel.Trace;
            Log.Trace($"noise {Touch()}");
            Assert.AreEqual(1, _touchCount, "总闸门放行到 Trace 时插值正常求值");
            Assert.AreEqual(1, sink.Entries.Count);
            StringAssert.Contains("touched", sink.Entries[0].Message);
        }

        [Test]
        public void Trace_Interpolation_FormatsValuesAndAlignment()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);
            Log.MinLevel = LogLevel.Trace;

            int n = 7;
            double d = 3.14159;
            Log.Trace($"n={n} d={d:F2} pad=[{n,3}]");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreEqual("n=7 d=3.14 pad=[  7]", sink.Entries[0].Message);
        }

        // ── Console 双击定位（[HideInCallstack] 全链覆盖）────────────────────

        /// <summary>
        /// 守住「Console 双击日志落到**业务调用点**而不是框架内部」。
        /// </summary>
        /// <remarks>
        /// Unity 的规则：从 <c>Debug.Log</c> 那一帧往外走，**跳过所有标了 <c>[HideInCallstack]</c> 的帧，
        /// 停在第一个没标的帧**上做双击定位。因此整条链
        /// （调用点 → <c>Log.Info/Warning/Error/Trace/Write</c> → <c>Log.Dispatch</c> → <c>UnityDebugLogSink.Log</c> → <c>Debug.Log</c>）
        /// 上**每一层**都必须标——**漏一层就前功尽弃**（实测确认过：只标最外层门面时，双击落在
        /// <c>UnityDebugLogSink.cs</c>）。这是所有「包一层 Debug.Log」的日志门面最常见的死因，
        /// 且症状只有人肉双击才看得见，故用本用例机器守住：将来给链条加层（新 sink 包装、装饰器）忘了标，这里会红。
        /// </remarks>
        [Test]
        public void EntireForwardingChain_IsHiddenFromCallstack()
        {
            static void AssertHidden(MethodInfo m, string what)
            {
                Assert.IsNotNull(m, $"{what} 没找到——签名变了？");
                Assert.IsTrue(
                    m.GetCustomAttributes(typeof(HideInCallstackAttribute), false).Length > 0,
                    $"{what} 缺 [HideInCallstack]——Console 双击会落进框架内部而不是业务调用点");
            }

            var logType = typeof(Log);

            // 门面的每个公开重载（含 Trace 的 string / 插值处理器两个重载）。
            foreach (var m in logType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.Name is "Info" or "Warning" or "Error" or "Trace" or "Write"))
                AssertHidden(m, $"Log.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");

            // 中间层：私有广播器 + 默认 sink（真正调 Debug.Log 的那一帧）。
            AssertHidden(logType.GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static), "Log.Dispatch");
            AssertHidden(typeof(UnityDebugLogSink).GetMethod(nameof(UnityDebugLogSink.Log)), "UnityDebugLogSink.Log");
        }

        // ── 异常 / 堆栈 / 结构化载荷 / context ────────────────────────────

        [Test]
        public void Error_CarriesException()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            var ex = new InvalidOperationException("boom");
            Log.Error("failed", ex, "Net");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreSame(ex, sink.Entries[0].Exception);
            Assert.AreEqual("Net", sink.Entries[0].Category);
        }

        /// <summary>
        /// 可恢复失败仍应保留原始异常供文件 / 遥测 sink 消费；默认 Unity sink 则把它附在同一条 Warning 中，
        /// 不能悄悄丢失，也不能额外抬成一条 Error。
        /// </summary>
        [Test]
        public void WarningWithException_IsStructuredAndVisibleInUnityConsole()
        {
            var captured = new CapturingSink();
            Log.AddSink(new UnityDebugLogSink());
            Log.AddSink(captured);
            Log.CaptureUnityLogs(true);

            InvalidOperationException ex;
            try
            {
                throw new InvalidOperationException("warning boom");
            }
            catch (InvalidOperationException caught)
            {
                ex = caught;
            }
            LogAssert.Expect(LogType.Warning,
                new Regex(@"\[Net\] 可恢复失败，已回退。\s+System\.InvalidOperationException: warning boom"));

            Log.Write(
                LogLevel.Warning,
                "可恢复失败，已回退。",
                category: "Net",
                exception: ex);

            Assert.AreEqual(1, captured.Entries.Count,
                "Unity Console sink 的 Warning 不能被日志桥接回灌成第二条记录");
            Assert.AreEqual(LogLevel.Warning, captured.Entries[0].Level);
            Assert.AreEqual("Net", captured.Entries[0].Category);
            Assert.AreSame(ex, captured.Entries[0].Exception,
                "Warning 不能把异常压平成 message，否则结构化 sink 无法可靠分类和保留堆栈");
            StringAssert.Contains(nameof(WarningWithException_IsStructuredAndVisibleInUnityConsole), ex.StackTrace);
        }

        [Test]
        public void Error_WithoutException_AutoCapturesStackTrace()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            Log.Error("no exception here");

            Assert.IsNotNull(sink.Entries[0].StackTrace, "没带异常的 Error 应自动补抓调用栈，否则落盘后无从定位");
            StringAssert.Contains(nameof(Error_WithoutException_AutoCapturesStackTrace), sink.Entries[0].StackTrace);
        }

        [Test]
        public void Info_DoesNotCaptureStackTrace()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            Log.Info("cheap");

            Assert.IsNull(sink.Entries[0].StackTrace, "抓栈不便宜，只对 Error 做");
        }

        [Test]
        public void Write_PassesStructuredFields()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            var fields = new List<KeyValuePair<string, object>>
            {
                new("userId", 42),
                new("action", "login"),
            };
            Log.Write(LogLevel.Info, "structured", fields);

            var entry = sink.Entries[0];
            Assert.IsNotNull(entry.Fields);
            Assert.AreEqual(2, entry.Fields.Count);
            Assert.AreEqual("userId", entry.Fields[0].Key);
            Assert.AreEqual(42, entry.Fields[0].Value);
        }

        [Test]
        public void Context_IsCarriedToSink()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);

            var go = new GameObject("ctx-probe");
            try
            {
                Log.Info("with context", context: go);
                Assert.AreSame(go, sink.Entries[0].Context, "Unity context 应透传给 sink（Console 点击可定位）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SinkException_DoesNotBreakOtherSinks()
        {
            var good = new CapturingSink();
            Log.AddSink(new ThrowingSink()); // 先抛的排前面，验证不影响后面的
            Log.AddSink(good);

            // 抛异常的 sink 会被门面吞掉并降级为一条 Debug.LogWarning。
            LogAssert.Expect(LogType.Warning, new Regex("sink ThrowingSink 抛异常"));
            Log.Info("survives");

            Assert.AreEqual(1, good.Entries.Count, "一个 sink 抛异常不应阻断其它 sink");
        }

        [Test]
        public void SinkMinLevelException_DoesNotEscapeOrBlockOtherSinks()
        {
            var good = new CapturingSink();
            Log.AddSink(new ThrowingMinLevelSink());
            Log.AddSink(good);

            LogAssert.Expect(LogType.Warning, new Regex("sink ThrowingMinLevelSink 抛异常"));
            Assert.IsTrue(Log.IsEnabled(LogLevel.Info),
                "坏 sink 的过滤 getter 不能阻断后续正常 sink 的可用性判断。");

            LogAssert.Expect(LogType.Warning, new Regex("sink ThrowingMinLevelSink 抛异常"));
            Assert.DoesNotThrow(() => Log.Info("survives-min-level"),
                "日志去向的配置读取失败也不能冒泡打断业务。");
            Assert.AreEqual(1, good.Entries.Count);
            Assert.AreEqual("survives-min-level", good.Entries[0].Message);
        }

        // ── Unity 日志流接管 ──────────────────────────────────────────────

        [Test]
        public void CaptureUnityLogs_BridgesBareDebugLogIntoSinks()
        {
            var sink = new CapturingSink();
            Log.AddSink(sink);              // 注意：SetUp 已 ClearSinks，此时没有 UnityDebugLogSink
            Log.CaptureUnityLogs(true);

            LogAssert.Expect(LogType.Error, "bare engine error");
            Debug.LogError("bare engine error");   // 完全没走门面——模拟引擎 / 第三方 / 业务裸 Debug.Log

            Assert.AreEqual(1, sink.Entries.Count, "裸 Debug.LogError 应经桥接进入 sink（否则玩家的崩溃不在日志文件里）");
            Assert.AreEqual(LogLevel.Error, sink.Entries[0].Level);
            Assert.IsTrue(sink.Entries[0].FromUnity, "桥接来的条目应标记 FromUnity");
            Assert.AreEqual("Unity", sink.Entries[0].Category);
            Assert.IsNotNull(sink.Entries[0].StackTrace, "Unity 传来的栈应保留");
        }

        [Test]
        public void CaptureUnityLogs_DoesNotEchoFacadeLogsTwice()
        {
            var sink = new CapturingSink();
            Log.AddSink(new UnityDebugLogSink());   // 它会把门面日志转成 Debug.Log → 可能被桥接抓回来
            Log.AddSink(sink);
            Log.CaptureUnityLogs(true);

            LogAssert.Expect(LogType.Log, "once");
            Log.Info("once");

            Assert.AreEqual(1, sink.Entries.Count,
                "门面日志经 UnityDebugLogSink 回到 Console 后，不应被桥接当成新日志重复计入（重入 guard 生效）");
            Assert.IsFalse(sink.Entries[0].FromUnity);
        }

        [Test]
        public void UnityLogBridge_NestedEmitScope_DoesNotReleaseOuterEchoGuardEarly()
        {
            Assert.IsFalse(UnityLogBridge.Emitting);
            UnityLogBridge.BeginEmit();
            try
            {
                Assert.IsTrue(UnityLogBridge.Emitting);
                UnityLogBridge.BeginEmit();
                UnityLogBridge.EndEmit();
                Assert.IsTrue(UnityLogBridge.Emitting,
                    "内层门面退出不得提前释放外层回声保护，否则 Error 的后续 LogException 会重复回灌");
            }
            finally
            {
                UnityLogBridge.EndEmit();
            }
            Assert.IsFalse(UnityLogBridge.Emitting, "最外层输出结束后 guard 应准确归零");
        }

        [Test]
        public void UnityDebugLogSink_SkipsBridgedEntries()
        {
            // 桥接来的条目 Console 里已经有了，UnityDebugLogSink 必须跳过——否则重复刷屏 + 无限回环。
            // 这里只装 UnityDebugLogSink：若它不跳过，转发会再触发桥接，LogAssert 将看到多于一条。
            Log.AddSink(new UnityDebugLogSink());
            Log.CaptureUnityLogs(true);

            LogAssert.Expect(LogType.Warning, "bridged once");
            Debug.LogWarning("bridged once");

            // 没有额外的 Console 输出即通过（LogAssert 在 TearDown 会对未预期日志报错）。
            Assert.Pass();
        }

        // ── FileLogSink 落盘 / 滚动 ───────────────────────────────────────

        private static string TempDir()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "logging-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void FileLogSink_WritesLinesToDisk()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "framework.log");
            try
            {
                using (var file = new FileLogSink(path, LogLevel.Info))
                {
                    Log.AddSink(file);
                    Log.Info("line one", "A");
                    Log.Warning("line two");
                    Log.Trace("filtered"); // 总闸门是 Info，Trace 过不了，落不进文件
                } // Dispose 释放句柄后再读

                string content = File.ReadAllText(path);
                StringAssert.Contains("line one", content);
                StringAssert.Contains("[A]", content);
                StringAssert.Contains("line two", content);
                StringAssert.DoesNotContain("filtered", content);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void FileLogSink_WritesSessionHeader()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "framework.log");
            try
            {
                using (var file = new FileLogSink(path, LogLevel.Info))
                {
                    Log.AddSink(file);
                    Log.Info("after header");
                }

                string content = File.ReadAllText(path);
                StringAssert.Contains("session", content, "每次开档应写会话头，否则多次启动的日志混在一起无从分辨");
                StringAssert.Contains("unity", content);
                StringAssert.Contains(Application.unityVersion, content);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void FileLogSink_RollsOverWhenExceedingMaxBytes()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "framework.log");
            string archive1 = Path.Combine(dir, "framework.1.log");
            try
            {
                // 极小阈值 + 写多行，必然触发至少一次滚动。
                using (var file = new FileLogSink(path, LogLevel.Info, maxBytes: 256, maxFiles: 2))
                {
                    for (int i = 0; i < 50; i++)
                        file.Log(new LogEntry(LogLevel.Info, $"padding line number {i:D4} ----------"));
                }

                Assert.IsTrue(File.Exists(path), "滚动后仍应存在可写的当前文件");
                Assert.IsTrue(File.Exists(archive1), "超阈值应产生归档 framework.1.log");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
