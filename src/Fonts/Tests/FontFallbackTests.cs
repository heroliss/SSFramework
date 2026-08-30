using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Framework.Fonts;
using Game.Framework.Logging;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using TextCoreFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Game.Framework.Test
{
    /// <summary>
    /// 验证字体 fallback 链核心（ADR-0025）：链条写入主字体表（原始表 + ②补充 + ③OS 兜底）、
    /// 切 locale 从原始表重建（无残留）、未配置 locale 降级 + 一次性警告、OS 候选按序择取与按族名缓存、
    /// Dispose 还原原始表。纯 C# 无场景；字体资产用 OS 字体运行时创建（本机 Windows：Arial 必在）。
    /// 渲染效果（豆腐块消失）无法 batchmode 断言，由 demo 章人工验证。
    /// </summary>
    public class FontFallbackTests
    {
        private sealed class CapturingSink : ILogSink
        {
            public LogLevel MinLevel => LogLevel.Trace;
            public readonly List<LogEntry> Entries = new();

            public void Log(in LogEntry entry) => Entries.Add(entry);
        }

        private readonly List<UnityEngine.Object> _createdAssets = new();
        private LocaleFontChain _chain;

        [TearDown]
        public void TearDown()
        {
            // 先还原主字体表（chain 内部创建的 OS 资产由它自己销毁），再销毁测试创建的资产。
            _chain?.Dispose();
            _chain = null;
            foreach (var asset in _createdAssets)
            {
                if (asset == null) continue;
                switch (asset)
                {
                    case TMP_FontAsset tmp:
                        DestroyFontObjects(tmp.material, tmp.atlasTextures, tmp);
                        break;
                    case TextCoreFontAsset tk:
                        DestroyFontObjects(tk.material, tk.atlasTextures, tk);
                        break;
                    default:
                        UnityEngine.Object.Destroy(asset);
                        break;
                }
            }
            _createdAssets.Clear();
        }

        // ── 链条写入：原始表 + ② 补充字体（双后端） ─────────────────────────

        [Test]
        public void Apply_WritesLocaleChain_TmpAndToolkit()
        {
            var tmpMain = CreateTmp();
            var tmpSupp = CreateTmp();
            var tkMain = CreateToolkit();
            var tkSupp = CreateToolkit();

            _chain = new LocaleFontChain(
                new[] { tmpMain },
                new[] { tkMain },
                new[] { new LocaleFontProfile("zh-CN", tmpFonts: new[] { tmpSupp }, toolkitFonts: new[] { tkSupp }) });

            _chain.Apply("zh-CN");

            CollectionAssert.AreEqual(new[] { tmpSupp }, tmpMain.fallbackFontAssetTable, "TMP 主字体链 = 原始表（空）+ ②补充");
            CollectionAssert.AreEqual(new[] { tkSupp }, tkMain.fallbackFontAssetTable, "Toolkit 主字体链 = 原始表（空）+ ②补充");
        }

        [Test]
        public void Apply_MainFontListedInOwnSupplements_IsExcluded()
        {
            // 配置错误：把主字体自己配进它的补充字体列表——会造成字体自引用链，必须被排除。
            var main = CreateTmp();
            var supp = CreateTmp();
            _chain = new LocaleFontChain(new[] { main }, null,
                new[] { new LocaleFontProfile("zh-CN", tmpFonts: new[] { main, supp }) });

            _chain.Apply("zh-CN");
            CollectionAssert.AreEqual(new[] { supp }, main.fallbackFontAssetTable, "主字体不应出现在自己的 fallback 链里");
        }

        [Test]
        public void Apply_SupplementAlreadyInOriginalOrRepeated_AppendsOnlyOnce()
        {
            // Profile 常会复用主字体资产里已有的中文 fallback；重复项不应改变原始优先级，也不应污染共享资产。
            var tmpMain = CreateTmp();
            var tmpSupp = CreateTmp();
            tmpMain.fallbackFontAssetTable = new List<TMP_FontAsset> { tmpSupp };
            var tkMain = CreateToolkit();
            var tkSupp = CreateToolkit();
            tkMain.fallbackFontAssetTable = new List<TextCoreFontAsset> { tkSupp };

            _chain = new LocaleFontChain(
                new[] { tmpMain },
                new[] { tkMain },
                new[]
                {
                    new LocaleFontProfile(
                        "zh-CN",
                        tmpFonts: new[] { tmpSupp, tmpSupp },
                        toolkitFonts: new[] { tkSupp, tkSupp }),
                });

            _chain.Apply("zh-CN");

            CollectionAssert.AreEqual(new[] { tmpSupp }, tmpMain.fallbackFontAssetTable);
            CollectionAssert.AreEqual(new[] { tkSupp }, tkMain.fallbackFontAssetTable);
        }

        // ── 切 locale：每次从原始表重建，无上个语言的残留 ────────────────────

        [Test]
        public void Apply_SwitchLocale_RebuildsFromOriginalSnapshot()
        {
            var main = CreateTmp();
            var preExisting = CreateTmp(); // 资产上预先配好的 fallback（如 emoji 字体）——链条应保留在基底里
            main.fallbackFontAssetTable = new List<TMP_FontAsset> { preExisting };
            var zhFont = CreateTmp();
            var jaFont = CreateTmp();

            _chain = new LocaleFontChain(new[] { main }, null, new[]
            {
                new LocaleFontProfile("zh-CN", tmpFonts: new[] { zhFont }),
                new LocaleFontProfile("ja", tmpFonts: new[] { jaFont }),
            });

            _chain.Apply("zh-CN");
            CollectionAssert.AreEqual(new[] { preExisting, zhFont }, main.fallbackFontAssetTable);

            _chain.Apply("ja");
            CollectionAssert.AreEqual(new[] { preExisting, jaFont }, main.fallbackFontAssetTable,
                "切语言应从原始快照重建——zh 的补充字体不能残留在 ja 的链上");
        }

        // ── 未配置的 locale：降级为原始表 + 一次性警告 ───────────────────────

        [Test]
        public void Apply_UnknownLocale_KeepsOriginal_WarnsOnce()
        {
            var main = CreateTmp();
            var original = CreateTmp();
            main.fallbackFontAssetTable = new List<TMP_FontAsset> { original };
            var zhFont = CreateTmp();

            _chain = new LocaleFontChain(new[] { main }, null,
                new[] { new LocaleFontProfile("zh-CN", tmpFonts: new[] { zhFont }) });

            var sink = new CapturingSink();
            Log.AddSink(sink);
            try
            {
                _chain.Apply("zh-CN"); // 先切到有档案的语言，再切走——验证降级会撤掉 zh 的补充
                LogAssert.Expect(LogType.Warning, new Regex("没有字体档案"));
                _chain.Apply("fr");
                _chain.Apply("fr"); // 同一 locale 只警告一次（绑定推送可能重复触发）
            }
            finally
            {
                Log.RemoveSink(sink);
            }

            CollectionAssert.AreEqual(new[] { original }, main.fallbackFontAssetTable, "未配置的 locale 应还原为原始表");
            Assert.AreEqual(1, sink.Entries.Count, "一次性降级警告也必须只进入日志 Seam 一次");
            Assert.AreEqual(LogLevel.Warning, sink.Entries[0].Level);
            Assert.AreEqual(nameof(LocaleFontChain), sink.Entries[0].Category);
            StringAssert.Contains("locale 'fr'", sink.Entries[0].Message);
            LogAssert.NoUnexpectedReceived();
        }

        // ── OS 兜底：候选按序试到第一个可用，追加链尾；按族名缓存共享 ──────────

        [Test]
        public void Apply_OsCandidates_FirstAvailableAppendedAtTail_CachedAcrossLocales()
        {
            var main = CreateTmp();
            // Windows 测试机必有 Arial；首个候选故意不存在，验证「按序试到成功」。
            var osNames = new[] { "__NoSuchFont_SSF__", "Arial" };
            _chain = new LocaleFontChain(new[] { main }, null, new[]
            {
                new LocaleFontProfile("zh-CN", osFontNames: osNames),
                new LocaleFontProfile("ja", osFontNames: osNames),
            });

            _chain.Apply("zh-CN");
            var table = main.fallbackFontAssetTable;
            Assert.AreEqual(1, table.Count);
            var osAsset = table[0];
            Assert.AreEqual("Arial", osAsset.faceInfo.familyName, "应择取首个可用候选（Arial）");

            _chain.Apply("ja");
            Assert.AreSame(osAsset, main.fallbackFontAssetTable[0], "同族名候选跨 locale 共享同一份运行时资产（按族名缓存）");
        }

        [Test]
        public void Apply_OsCandidate_ResolvesForToolkitBackend()
        {
            // TMP 与 TextCore 是两套 CreateFontAsset，Toolkit 侧 OS 解析路径单独覆盖（重载签名/行为可能不一致）。
            var main = CreateToolkit();
            _chain = new LocaleFontChain(null, new[] { main },
                new[] { new LocaleFontProfile("zh-CN", osFontNames: new[] { "Arial" }) });

            _chain.Apply("zh-CN");
            var table = main.fallbackFontAssetTable;
            Assert.AreEqual(1, table.Count, "Toolkit 主字体应挂上 OS 兜底字体");
            Assert.AreEqual("Arial", table[0].faceInfo.familyName);
        }

        [Test]
        public void Apply_AllOsCandidatesFail_WarnsOnce_ChainKeepsSupplements()
        {
            var main = CreateTmp();
            var zhFont = CreateTmp();
            _chain = new LocaleFontChain(new[] { main }, null, new[]
            {
                new LocaleFontProfile("zh-CN", tmpFonts: new[] { zhFont },
                    osFontNames: new[] { "__NoSuchFontA_SSF__", "__NoSuchFontB_SSF__" }),
            });

            // TMP 对每个查不到的族名打一条 info 日志（之后按族名缓存失败、不再重试）。
            LogAssert.Expect(LogType.Log, new Regex("Unable to find a font file"));
            LogAssert.Expect(LogType.Log, new Regex("Unable to find a font file"));
            LogAssert.Expect(LogType.Warning, new Regex("OS 字体候选全部不可用"));
            _chain.Apply("zh-CN");
            _chain.Apply("zh-CN"); // 失败已缓存：不重试、不再警告

            CollectionAssert.AreEqual(new[] { zhFont }, main.fallbackFontAssetTable, "OS 层降级后 ②补充字体仍在链上");
            LogAssert.NoUnexpectedReceived();
        }

        // ── Dispose：还原原始表；可重复；之后 Apply 抛 ───────────────────────

        [Test]
        public void Dispose_RestoresOriginals_ApplyAfterDisposeThrows()
        {
            var main = CreateTmp();
            var original = CreateTmp();
            main.fallbackFontAssetTable = new List<TMP_FontAsset> { original };
            var zhFont = CreateTmp();

            _chain = new LocaleFontChain(new[] { main }, null,
                new[] { new LocaleFontProfile("zh-CN", tmpFonts: new[] { zhFont }) });
            _chain.Apply("zh-CN");
            CollectionAssert.AreEqual(new[] { original, zhFont }, main.fallbackFontAssetTable);

            _chain.Dispose();
            CollectionAssert.AreEqual(new[] { original }, main.fallbackFontAssetTable, "Dispose 应还原快照的原始表");

            _chain.Dispose(); // 幂等
            Assert.Throws<ObjectDisposedException>(() => _chain.Apply("zh-CN"));
        }

        [UnityTest] // Object.Destroy 延迟到帧末：Dispose 后需 yield 一帧才能观察到 fake-null
        public IEnumerator Dispose_DestroysCreatedOsFontAssets()
        {
            var main = CreateTmp();
            _chain = new LocaleFontChain(new[] { main }, null,
                new[] { new LocaleFontProfile("zh-CN", osFontNames: new[] { "Arial" }) });
            _chain.Apply("zh-CN");

            var osAsset = main.fallbackFontAssetTable[0]; // 链运行时创建的 OS 字体
            Assert.IsTrue(osAsset != null);
            var osMaterial = osAsset.material;
            var osAtlas = osAsset.atlasTextures != null && osAsset.atlasTextures.Length > 0 ? osAsset.atlasTextures[0] : null;
            bool hadAtlas = osAtlas != null; // Dispose 前的真值快照——Destroy 后引用会变 fake-null

            _chain.Dispose();
            yield return null; // 让延迟的 Destroy 在帧末执行

            // Unity fake-null：Destroy 后引用 == null 为真。核心契约——OS 资产连 material/atlas 一起销毁，不泄漏。
            Assert.IsTrue(osAsset == null, "Dispose 应销毁运行时创建的 OS 字体资产");
            Assert.IsTrue(osMaterial == null, "OS 字体的 material 应一并销毁");
            if (hadAtlas)
                Assert.IsTrue(osAtlas == null, "OS 字体的 atlas 纹理应一并销毁");
        }

        // ── 配置容错：Inspector 空槽 / 重复主字体 / 重复 locale 档案 ──────────

        [Test]
        public void Constructor_ToleratesNullHolesAndDuplicates()
        {
            var main = CreateTmp();
            var zhFont = CreateTmp();

            LogAssert.Expect(LogType.Warning, new Regex("多份字体档案"));
            _chain = new LocaleFontChain(
                new[] { null, main, main },      // 空槽 + 重复项：去重去空（重复会导致快照互相覆盖）
                null,                            // 单后端项目：另一栏整个为 null
                new[]
                {
                    null,                        // 没填完的 Inspector 行
                    new LocaleFontProfile("zh-CN", tmpFonts: new[] { zhFont, null }), // 补充字体里的空槽
                    new LocaleFontProfile("zh-CN"),                                    // 重复 locale：首份生效 + 警告
                });

            _chain.Apply("zh-CN");
            CollectionAssert.AreEqual(new[] { zhFont }, main.fallbackFontAssetTable);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Apply_EmptyOrNullLocale_Throws()
        {
            _chain = new LocaleFontChain(new[] { CreateTmp() }, null, Array.Empty<LocaleFontProfile>());
            Assert.Throws<ArgumentException>(() => _chain.Apply(null));
            Assert.Throws<ArgumentException>(() => _chain.Apply(""));
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private TMP_FontAsset CreateTmp()
        {
            var asset = TMP_FontAsset.CreateFontAsset("Arial", null, 90);
            Assert.IsNotNull(asset, "测试机应有 Arial（Windows 必装）——没有说明环境异常");
            _createdAssets.Add(asset);
            return asset;
        }

        private TextCoreFontAsset CreateToolkit()
        {
            var asset = TextCoreFontAsset.CreateFontAsset("Arial", null, 90);
            Assert.IsNotNull(asset, "测试机应有 Arial（Windows 必装）——没有说明环境异常");
            _createdAssets.Add(asset);
            return asset;
        }

        private static void DestroyFontObjects(Material material, Texture2D[] atlasTextures, UnityEngine.Object asset)
        {
            if (material != null) UnityEngine.Object.Destroy(material);
            if (atlasTextures != null)
                foreach (var tex in atlasTextures)
                    if (tex != null) UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(asset);
        }
    }
}
