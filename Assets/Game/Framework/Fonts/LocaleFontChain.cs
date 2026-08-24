using System;
using System.Collections.Generic;
using Game.Framework.Logging;
using TMPro;
using UnityEngine;
using TextCoreFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Game.Framework.Fonts
{
    /// <summary>
    /// 字体 fallback 链的核心实现（ADR-0025）：把「locale 补充字体 + OS 字体兜底」写进各<b>主字体资产</b>的
    /// <c>fallbackFontAssetTable</c>——文本渲染自动逐层找字形，业务代码零感知。
    /// 场景路径由 <see cref="MonoLocaleFonts"/> 包装（订阅 <c>ILocalizationUtility.Locale</c> 驱动 <see cref="Apply"/>）；
    /// 本类不依赖场景，可脱离 GameObject 单测。
    /// </summary>
    /// <remarks>
    /// <b>为什么写主字体的表、不写全局 settings：</b>per-font 表在 TMP 与 TextCore 两侧都是 public 可写（对称）；
    /// Toolkit 侧全局入口（PanelTextSettings）在 Unity 6000.3 已收进 internal。且「共享主字体不换、换链上的语言层」
    /// 正是 per-font 表的形状。代价是主字体要显式列出——用了未列出字体的文本不受链条保护。<br/>
    /// <b>还原语义：</b>构造时快照各主字体的原始 fallback 表；每次 <see cref="Apply"/> = 原始表 + ②当前 locale 补充 + ③OS 兜底；
    /// <see cref="Dispose"/> 还原原始表并销毁运行时创建的 OS 字体资产（含 atlas 纹理 / 材质）——
    /// 字体资产是全工程共享资产，Editor Play 会话不留残留、子 Context 反复建销不泄漏。<br/>
    /// <b>OS 字体创建：</b><c>CreateFontAsset(族名, null, 90)</c>（null 样式 = 默认 face；找不到族名返回 null），
    /// 按序试候选到第一个成功，失败结果也按族名缓存（避免每次 Apply 重试刷日志）。⚠ 族名用英文名——
    /// 本地化名（如「微软雅黑」）字体引擎查不到。<br/>
    /// <b>线程：</b>主线程独占（框架统一契约）。
    /// </remarks>
    public sealed class LocaleFontChain : IDisposable
    {
        /// <summary>动态字体的采样点大小，取 TMP / TextCore 各自默认重载的 90（SDF 质量与内存的均衡点）。</summary>
        private const int OsFontPointSize = 90;

        private readonly TMP_FontAsset[] _tmpMainFonts;
        private readonly TextCoreFontAsset[] _toolkitMainFonts;
        private readonly LocaleFontProfile[] _profiles;

        // 构造时快照的原始 fallback 表（Apply 的基底、Dispose 的还原目标）。
        private readonly List<TMP_FontAsset>[] _tmpOriginals;
        private readonly List<TextCoreFontAsset>[] _toolkitOriginals;

        // OS 字体资产按族名缓存（多 locale 共用同族名只建一份）；创建失败也缓存 null，避免每次 Apply 重试。
        private readonly Dictionary<string, TMP_FontAsset> _tmpOsFonts = new();
        private readonly Dictionary<string, TextCoreFontAsset> _toolkitOsFonts = new();

        private bool _disposed;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly HashSet<string> _warned = new();
#endif

        /// <summary>
        /// 创建一条字体链并立即快照所有主字体的当前 fallback 表。实例拥有之后创建的 OS 字体资产；
        /// 调用方必须在不再接管这些主字体时 <see cref="Dispose"/>，以还原快照并释放运行时资产。
        /// </summary>
        /// <param name="tmpMainFonts">TMP（UGUI 侧）主字体列表；链条写到这些资产的 fallback 表上。null 列表、null 项与重复项自动忽略。</param>
        /// <param name="toolkitMainFonts">TextCore（UI Toolkit 侧）主字体列表；两栏可只配一栏（单后端项目）。null 规则同上。</param>
        /// <param name="profiles">各 locale 的字体档案；null 按空列表处理，locale 重复时首个生效（Editor/Dev 警告）。</param>
        public LocaleFontChain(
            IReadOnlyList<TMP_FontAsset> tmpMainFonts,
            IReadOnlyList<TextCoreFontAsset> toolkitMainFonts,
            IReadOnlyList<LocaleFontProfile> profiles)
        {
            _tmpMainFonts = Compact(tmpMainFonts);
            _toolkitMainFonts = Compact(toolkitMainFonts);
            _profiles = CompactProfiles(profiles);

            _tmpOriginals = new List<TMP_FontAsset>[_tmpMainFonts.Length];
            for (int i = 0; i < _tmpMainFonts.Length; i++)
                _tmpOriginals[i] = SnapshotTable(_tmpMainFonts[i].fallbackFontAssetTable);

            _toolkitOriginals = new List<TextCoreFontAsset>[_toolkitMainFonts.Length];
            for (int i = 0; i < _toolkitMainFonts.Length; i++)
                _toolkitOriginals[i] = SnapshotTable(_toolkitMainFonts[i].fallbackFontAssetTable);
        }

        /// <summary>是否已释放（还原并停止工作）。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 把 locale 对应的链条写到所有主字体上：表 = 原始表 + ②补充字体 + ③OS 兜底。
        /// 未配置该 locale 的档案 = 还原为原始表（degrade，Editor/Dev 一次性警告）；档案里 ②/③ 缺哪层跳哪层。
        /// 应用后对使用主字体的存活 TMP 文本强制重建网格（TMP 有字形解析缓存；Toolkit 文本随后续文本变更自然刷新）。
        /// </summary>
        /// <param name="locale">要应用的 locale code；必须与 <see cref="LocaleFontProfile.Locale"/> 使用同一命名契约。</param>
        /// <exception cref="ArgumentException"><paramref name="locale"/> 为 null 或空字符串。</exception>
        /// <exception cref="ObjectDisposedException">本实例已经释放；释放后的链不会再次接管字体资产。</exception>
        public void Apply(string locale)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LocaleFontChain));
            if (string.IsNullOrEmpty(locale))
                throw new ArgumentException("locale 不能为空。", nameof(locale));

            var profile = FindProfile(locale);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (profile == null && _warned.Add("no-profile:" + locale))
                Log.Warning(
                    $"locale '{locale}' 没有字体档案——主字体保持原始 fallback 表（仅共享字形层）。",
                    category: nameof(LocaleFontChain));
#endif

            for (int i = 0; i < _tmpMainFonts.Length; i++)
            {
                var main = _tmpMainFonts[i];
                if (main == null) continue; // 主字体资产被外部卸载/销毁：跳过，切语言不因此炸

                var table = new List<TMP_FontAsset>(_tmpOriginals[i]);
                if (profile != null)
                {
                    foreach (var f in profile.TmpFonts)
                        AppendFallback(table, main, f);
                    var os = ResolveOsFont(profile.OsFontNames, _tmpOsFonts,
                        static name => TMP_FontAsset.CreateFontAsset(name, null, OsFontPointSize));
                    AppendFallback(table, main, os);
                }
                main.fallbackFontAssetTable = table;
            }

            for (int i = 0; i < _toolkitMainFonts.Length; i++)
            {
                var main = _toolkitMainFonts[i];
                if (main == null) continue;

                var table = new List<TextCoreFontAsset>(_toolkitOriginals[i]);
                if (profile != null)
                {
                    foreach (var f in profile.ToolkitFonts)
                        AppendFallback(table, main, f);
                    var os = ResolveOsFont(profile.OsFontNames, _toolkitOsFonts,
                        static name => TextCoreFontAsset.CreateFontAsset(name, null, OsFontPointSize));
                    AppendFallback(table, main, os);
                }
                main.fallbackFontAssetTable = table;
            }

            PurgeEngineLookupCaches();
            RefreshLiveTmpTexts();
        }

        /// <summary>
        /// 还原所有主字体的原始 fallback 表，并销毁运行时创建的 OS 字体资产。可重复调用。
        /// </summary>
        /// <remarks>
        /// ⚠ <b>Toolkit 存活文本约束</b>：Dispose 会强刷存活 <b>TMP</b> 文本再销毁 OS 资产（TMP 侧安全），
        /// 但<b>不</b>强刷 UI Toolkit 文本——UI Toolkit 无 TMP 那样的按对象强制重排版入口。
        /// <c>MonoLocaleFonts</c> 主路径下 Dispose = Context/场景拆除，Toolkit 文本同时销毁，无隐患；
        /// 但脱离 Mono 的 standalone 用法里，若配了 Toolkit 主字体 + OS 候选、且 Dispose 时仍有存活的 Toolkit
        /// 文本引用链上的 OS 字体，其已烘焙 mesh 会采样到被销毁的 atlas（显示错乱直到文本变化）。
        /// standalone + Toolkit + OS 兜底的组合，Dispose 前应确保无存活 Toolkit 文本引用该链。
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < _tmpMainFonts.Length; i++)
                if (_tmpMainFonts[i] != null)
                    _tmpMainFonts[i].fallbackFontAssetTable = new List<TMP_FontAsset>(_tmpOriginals[i]);
            for (int i = 0; i < _toolkitMainFonts.Length; i++)
                if (_toolkitMainFonts[i] != null)
                    _toolkitMainFonts[i].fallbackFontAssetTable = new List<TextCoreFontAsset>(_toolkitOriginals[i]);

            // 先按原始表重刷存活文本，再销毁 OS 资产——刷新后没人再引用它们，销毁安全；
            // 顺序反了会让文本网格里残留已销毁字体的字形。
            PurgeEngineLookupCaches();
            RefreshLiveTmpTexts();

            foreach (var asset in _tmpOsFonts.Values)
                if (asset != null)
                    DestroyRuntimeFontAsset(asset.material, asset.atlasTextures, asset);
            foreach (var asset in _toolkitOsFonts.Values)
                if (asset != null)
                    DestroyRuntimeFontAsset(asset.material, asset.atlasTextures, asset);
            _tmpOsFonts.Clear();
            _toolkitOsFonts.Clear();
        }

        private LocaleFontProfile FindProfile(string locale)
        {
            foreach (var p in _profiles)
                if (string.Equals(p.Locale, locale, StringComparison.Ordinal))
                    return p;
            return null;
        }

        /// <summary>
        /// 按序尝试 OS 字体族名候选，返回第一个可用的动态字体资产。
        /// 结果按族名缓存（含失败的 null——CreateFontAsset 每次失败都会打日志，缓存避免每次切语言重试刷屏）。
        /// </summary>
        private T ResolveOsFont<T>(IReadOnlyList<string> candidates, Dictionary<string, T> cache, Func<string, T> create)
            where T : UnityEngine.Object
        {
            if (candidates == null || candidates.Count == 0) return null;

            foreach (var name in candidates)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (cache.TryGetValue(name, out var cached))
                {
                    if (cached != null) return cached;
                    continue; // 已知失败的族名：跳过，试下一个候选
                }
                var asset = create(name);
                cache[name] = asset;
                if (asset != null)
                {
                    asset.name = "OS Fallback: " + name; // CreateFontAsset(族名…) 产出的资产 name 为空串，命名便于诊断面板 / 日志辨认
                    return asset;
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_warned.Add("no-os-font:" + string.Join("|", candidates)))
                Log.Warning(
                    $"OS 字体候选全部不可用（{string.Join(", ", candidates)}）——" +
                    "降级为仅①主字体+②补充字体。候选需用英文族名（本地化名查不到）。",
                    category: nameof(LocaleFontChain));
#endif
            return null;
        }

        // TextCore 侧清缓存入口在 internal 类上（方法本身 public）——反射一次缓存 MethodInfo；
        // 引擎版本变动找不到时降级为只清 TMP 侧（Editor/Dev 警告一次），不炸。
        private static readonly System.Reflection.MethodInfo TextCoreClearGlyphCache =
            typeof(UnityEngine.TextCore.Text.FontAsset).Assembly
                .GetType("UnityEngine.TextCore.Text.TextResourceManager")
                ?.GetMethod("ClearFontAssetGlyphCache",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static bool _warnedTextCoreCacheMissing;
#endif

        /// <summary>
        /// 清掉双后端字体引擎的「fallback 解析结果缓存」：字符经链上字体解析成功后，引擎会把跨字体引用
        /// 缓存进被查字体的 lookup 表（TMP <c>AddCharacterToLookupCache</c>；TextCore 同构）——
        /// 只改 fallback 表不清缓存，已解析过的字符会继续命中旧链上的字体（撤下的语言层像没撤一样）。
        /// 两个 ClearFontAssetGlyphCache 都只重建 lookup 表（不动 atlas），切语言频率下开销可忽略。
        /// </summary>
        private static void PurgeEngineLookupCaches()
        {
            TMP_ResourceManager.ClearFontAssetGlyphCache();

            // 反射调用整段兜异常：本方法在 Dispose 里先于「销毁 OS 字体资产」执行，若引擎改了签名让 Invoke 抛，
            // 异常穿出会跳过后续销毁 → 泄漏。降级为一次性警告，宁可缓存没清干净也不能漏销毁。
            if (TextCoreClearGlyphCache != null)
            {
                try { TextCoreClearGlyphCache.Invoke(null, null); }
                catch (Exception ex) { WarnTextCoreCacheOnce("调用抛异常（" + ex.GetType().Name + "，引擎版本变动？）"); }
            }
            else
            {
                WarnTextCoreCacheOnce("未找到入口（引擎版本变动？）");
            }
        }

        private static void WarnTextCoreCacheOnce(string reason)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_warnedTextCoreCacheMissing) return;
            _warnedTextCoreCacheMissing = true;
            Log.Warning(
                $"TextCore 的 ClearFontAssetGlyphCache {reason}——" +
                "UI Toolkit 侧已解析字符可能沿用旧链字形，直到文本内容变化。",
                category: nameof(LocaleFontChain));
#endif
        }

        /// <summary>
        /// TMP 在字形解析上有缓存，改 fallback 表后已显示文本不会自动重查——
        /// 对使用主字体的存活文本强制重解析。切语言是低频操作，全场景扫描可接受。
        /// Toolkit 侧刻意不在此处理：Apply 时本地化文本随 Locale 推送重设 text 自然重排（且 Apply 不销毁 OS 字体，
        /// 无采样已销毁资产的风险）；Dispose 时的 Toolkit 约束见 <see cref="Dispose"/> 备注。
        /// </summary>
        private void RefreshLiveTmpTexts()
        {
            if (_tmpMainFonts.Length == 0) return;
            var texts = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None);
            foreach (var t in texts)
                if (t.font != null && Array.IndexOf(_tmpMainFonts, t.font) >= 0)
                    t.ForceMeshUpdate(true, true);
        }

        /// <summary>运行时创建的字体资产要连 atlas 纹理 / 材质一起销毁——它们是独立的引擎对象，只销毁资产会泄漏纹理。</summary>
        private static void DestroyRuntimeFontAsset(Material material, Texture2D[] atlasTextures, UnityEngine.Object asset)
        {
            if (material != null) UnityEngine.Object.Destroy(material);
            if (atlasTextures != null)
                foreach (var tex in atlasTextures)
                    if (tex != null) UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(asset);
        }

        private static T[] Compact<T>(IReadOnlyList<T> source) where T : UnityEngine.Object
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var list = new List<T>(source.Count);
            foreach (var item in source)
                if (item != null && !list.Contains(item)) // 过滤 Inspector 空槽与重复项（重复会导致快照互相覆盖）
                    list.Add(item);
            return list.ToArray();
        }

        /// <summary>
        /// 保留 fallback 首次出现的位置：资产原始链优先于 locale 补充，locale 补充又优先于 OS 兜底。
        /// 同一字体若同时出现在原始表和 Profile，重复追加既不会提高覆盖率，还会让共享字体资产在 Editor Play 后留下脏数据。
        /// </summary>
        private static void AppendFallback<T>(List<T> table, T main, T fallback) where T : UnityEngine.Object
        {
            if (fallback != null && fallback != main && !table.Contains(fallback))
                table.Add(fallback);
        }

        private LocaleFontProfile[] CompactProfiles(IReadOnlyList<LocaleFontProfile> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<LocaleFontProfile>();
            var list = new List<LocaleFontProfile>(source.Count);
            foreach (var p in source)
            {
                if (p == null || string.IsNullOrEmpty(p.Locale)) continue; // 空行留给没填完的 Inspector 配置
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                foreach (var existing in list)
                    if (string.Equals(existing.Locale, p.Locale, StringComparison.Ordinal) &&
                        _warned.Add("dup-profile:" + p.Locale))
                        Log.Warning(
                            $"locale '{p.Locale}' 配置了多份字体档案，仅第一份生效。",
                            category: nameof(LocaleFontChain));
#endif
                list.Add(p);
            }
            return list.ToArray();
        }

        private static List<T> SnapshotTable<T>(List<T> table) =>
            table != null ? new List<T>(table) : new List<T>();
    }
}
