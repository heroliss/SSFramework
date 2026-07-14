using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Framework.Context;
using Game.Framework.Localization;
using NUnit.Framework;
using R3;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证本地化服务（ADR-0024）：当前语言查询、SetLocale 响应式推送（同值幂等）、
    /// 缺 key 回退链（fallbackLocale → 裸 key + 去重警告）、格式化宽容语义、字典源、
    /// RegisterOwned 注册与随 Context 释放。纯 C#，全部用例无场景无帧推进。
    /// </summary>
    public class LocalizationTests
    {
        private DictionaryLocalizedTextSource _source;
        private LocalizationUtility _loc;

        [SetUp]
        public void SetUp()
        {
            _source = new DictionaryLocalizedTextSource()
                .Add("zh-CN", "menu/start", "开始游戏")
                .Add("zh-CN", "lobby/welcome", "欢迎回来，{0}！")
                .Add("zh-CN", "demo/only-zh", "仅中文有")
                .Add("en", "menu/start", "Start Game")
                .Add("en", "lobby/welcome", "Welcome back, {0}!")
                .Add("en", "demo/bad-format", "oops {0");
            _loc = new LocalizationUtility(_source, "zh-CN", fallbackLocale: "zh-CN");
        }

        [TearDown]
        public void TearDown() => _loc.Dispose();

        // ── 查询与响应式切换 ─────────────────────────────────────────────────

        [Test]
        public void Get_CurrentLocale_SetLocalePushes_SameValueIsNoOp()
        {
            var pushes = new List<string>();
            using var sub = _loc.Locale.Subscribe(pushes.Add); // R3 订阅立即推当前值

            Assert.AreEqual("开始游戏", _loc.Get("menu/start"));
            Assert.AreEqual(1, pushes.Count);

            _loc.SetLocale("en");
            Assert.AreEqual("Start Game", _loc.Get("menu/start"));
            Assert.AreEqual(2, pushes.Count);

            _loc.SetLocale("en"); // 同值：不推送（绑定不做无谓重刷）
            Assert.AreEqual(2, pushes.Count);
        }

        // ── 缺 key 回退链 ────────────────────────────────────────────────────

        [Test]
        public void MissingKey_FallsBackToFallbackLocale_ThenBareKey_WarnsOnce()
        {
            _loc.SetLocale("en");

            // en 缺、fallback zh-CN 有 → 回退文本（不警告）
            Assert.AreEqual("仅中文有", _loc.Get("demo/only-zh"));

            // 两边都缺 → 裸 key 上屏；同一 (locale, key) 只警告一次（绑定标签会反复重查）
            LogAssert.Expect(LogType.Warning, new Regex("demo/nowhere"));
            Assert.AreEqual("demo/nowhere", _loc.Get("demo/nowhere"));
            Assert.AreEqual("demo/nowhere", _loc.Get("demo/nowhere"));
            LogAssert.NoUnexpectedReceived();
        }

        // ── 格式化 ───────────────────────────────────────────────────────────

        [Test]
        public void Format_AppliesArgs_MalformedTemplateReturnsRawAndWarns()
        {
            Assert.AreEqual("欢迎回来，SS！", _loc.Get("lobby/welcome", "SS"));

            _loc.SetLocale("en");
            LogAssert.Expect(LogType.Warning, new Regex("demo/bad-format"));
            Assert.AreEqual("oops {0", _loc.Get("demo/bad-format", 1)); // 文案错不炸游戏：返回未格式化模板
        }

        // ── 参数校验 ─────────────────────────────────────────────────────────

        [Test]
        public void InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => _ = new LocalizationUtility(null, "zh-CN"));
            Assert.Throws<ArgumentException>(() => _ = new LocalizationUtility(_source, ""));
            Assert.Throws<ArgumentException>(() => _loc.SetLocale(null));
            Assert.Throws<ArgumentException>(() => _loc.Get(""));
            Assert.Throws<ArgumentException>(() => _source.Add("", "k", "v"));
            Assert.Throws<ArgumentException>(() => _source.Add("zh-CN", null, "v"));
            Assert.Throws<ArgumentNullException>(() => _source.AddLocale("zh-CN", null));
        }

        // ── 字典源 ───────────────────────────────────────────────────────────

        [Test]
        public void DictionarySource_BulkAdd_MergesAndOverwrites()
        {
            var src = new DictionaryLocalizedTextSource()
                .Add("en", "a", "old")
                .AddLocale("en", new Dictionary<string, string> { ["a"] = "new", ["b"] = "B" });

            Assert.IsTrue(src.TryGet("en", "a", out var a));
            Assert.AreEqual("new", a); // 同 key 覆盖
            Assert.IsTrue(src.TryGet("en", "b", out _));
            Assert.IsFalse(src.TryGet("fr", "a", out _)); // 未知 locale：false 不抛
        }

        // ── 生命周期 ─────────────────────────────────────────────────────────

        [Test]
        public void RegisterOwned_ResolvesEverywhere_DisposesWithContext()
        {
            var builder = new ContainerBuilder();
            builder.RegisterOwned(new LocalizationUtility(_source, "zh-CN"), typeof(ILocalizationUtility));
            using var ctx = new GameContext(builder.Build(), inheritFromGlobal: false);

            var resolved = (ILocalizationUtility)ctx.Resolve(typeof(ILocalizationUtility));
            Assert.AreEqual("开始游戏", resolved.Get("menu/start"));

            ctx.Dispose(); // RegisterOwned：随 Context 释放

            Assert.AreEqual("开始游戏", resolved.Get("menu/start")); // 查询读普通字段，Dispose 后仍安全
            Assert.Throws<ObjectDisposedException>(() => resolved.SetLocale("en")); // 换语言是明确操作，过期引用应暴露
        }
    }
}
