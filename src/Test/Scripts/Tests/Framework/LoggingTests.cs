using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Game.Framework.Internal;
using Game.Framework.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证框架日志接缝（ADR-0034）：<c>FrameworkLog</c> 门面的分级 / 多播 / 过滤 / Trace 门控 / 异常隔离，
    /// 以及内核 <see cref="FileLogSink"/> 的落盘与按大小滚动。全部纯 C# 无头可测——接缝本身不依赖场景。
    /// </summary>
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

        [SetUp]
        public void SetUp()
        {
            // FrameworkLog 是全局静态——每个用例先清干净，避免默认 UnityDebugLogSink 往 Console 刷 + 用例间串味。
            FrameworkLog.ClearSinks();
            FrameworkLog.Verbose = false;
        }

        [TearDown]
        public void TearDown()
        {
            // 恢复出厂默认（一个 UnityDebugLogSink），不给后续测试留下被清空的日志系统。
            FrameworkLog.ClearSinks();
            FrameworkLog.AddSink(new UnityDebugLogSink());
            FrameworkLog.Verbose = false;
        }

        // ── 多播 / 过滤 ──────────────────────────────────────────────────

        [Test]
        public void Info_BroadcastsToAllSinks()
        {
            var a = new CapturingSink();
            var b = new CapturingSink();
            FrameworkLog.AddSink(a);
            FrameworkLog.AddSink(b);

            FrameworkLog.Info("hello", "Cat");

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
            FrameworkLog.AddSink(warnOnly);

            FrameworkLog.Info("info");         // 低于阈值，被挡
            FrameworkLog.Warning("warn");      // 命中
            FrameworkLog.Error("err");         // 高于阈值，命中

            Assert.AreEqual(2, warnOnly.Entries.Count, "低于 MinLevel 的条目不应投递");
            Assert.AreEqual(LogLevel.Warning, warnOnly.Entries[0].Level);
            Assert.AreEqual(LogLevel.Error, warnOnly.Entries[1].Level);
        }

        [Test]
        public void RemoveSink_StopsDelivery()
        {
            var sink = new CapturingSink();
            FrameworkLog.AddSink(sink);
            FrameworkLog.Info("first");

            Assert.IsTrue(FrameworkLog.RemoveSink(sink));
            FrameworkLog.Info("second");

            Assert.AreEqual(1, sink.Entries.Count, "移除后不再收到");
            Assert.AreEqual("first", sink.Entries[0].Message);
            Assert.IsFalse(FrameworkLog.RemoveSink(sink), "重复移除返回 false");
        }

        // ── Trace 门控（受 Verbose + 仅 Editor/Dev）─────────────────────────

        [Test]
        public void Trace_OnlyDeliveredWhenVerbose()
        {
            var sink = new CapturingSink();
            FrameworkLog.AddSink(sink);

            FrameworkLog.Verbose = false;
            FrameworkLog.Trace("noise");
            FrameworkLog.LogVerbose("legacy noise");
            Assert.AreEqual(0, sink.Entries.Count, "Verbose 关时 Trace 不投递");

            // 测试在 Editor 下跑（UNITY_EDITOR 为真），故 Verbose 开后 Trace 应放行。
            FrameworkLog.Verbose = true;
            FrameworkLog.Trace("visible");
            Assert.AreEqual(1, sink.Entries.Count, "Verbose 开时 Trace 投递");
            Assert.AreEqual(LogLevel.Trace, sink.Entries[0].Level);
        }

        // ── 异常 / 结构化载荷 ────────────────────────────────────────────

        [Test]
        public void Error_CarriesException()
        {
            var sink = new CapturingSink();
            FrameworkLog.AddSink(sink);

            var ex = new InvalidOperationException("boom");
            FrameworkLog.Error("failed", ex, "Net");

            Assert.AreEqual(1, sink.Entries.Count);
            Assert.AreSame(ex, sink.Entries[0].Exception);
            Assert.AreEqual("Net", sink.Entries[0].Category);
        }

        [Test]
        public void Log_PassesStructuredFields()
        {
            var sink = new CapturingSink();
            FrameworkLog.AddSink(sink);

            var fields = new List<KeyValuePair<string, object>>
            {
                new("userId", 42),
                new("action", "login"),
            };
            FrameworkLog.Log(LogLevel.Info, "structured", fields);

            var entry = sink.Entries[0];
            Assert.IsNotNull(entry.Fields);
            Assert.AreEqual(2, entry.Fields.Count);
            Assert.AreEqual("userId", entry.Fields[0].Key);
            Assert.AreEqual(42, entry.Fields[0].Value);
        }

        [Test]
        public void SinkException_DoesNotBreakOtherSinks()
        {
            var good = new CapturingSink();
            FrameworkLog.AddSink(new ThrowingSink()); // 先抛的排前面，验证不影响后面的
            FrameworkLog.AddSink(good);

            // 抛异常的 sink 会被门面吞掉并降级为一条 Debug.LogWarning。
            LogAssert.Expect(LogType.Warning, new Regex("sink ThrowingSink 抛异常"));
            FrameworkLog.Info("survives");

            Assert.AreEqual(1, good.Entries.Count, "一个 sink 抛异常不应阻断其它 sink");
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
                    FrameworkLog.AddSink(file);
                    FrameworkLog.Info("line one", "A");
                    FrameworkLog.Warning("line two");
                    FrameworkLog.Trace("filtered"); // Verbose 关 + 低于 Info，落不进文件
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
