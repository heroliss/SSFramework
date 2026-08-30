using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>生成器对工程输出的占用粒度；只描述可能被写入或清理的范围，不描述生成流程。</summary>
    public enum FrameworkGeneratedOutputClaimKind
    {
        /// <summary>生成器独占并可能整理整个目录；与目录树内任何其它生成输出冲突。</summary>
        ExclusiveDirectory,
        /// <summary>生成器会在目录树内递归写入并清理匹配后缀的文件。</summary>
        RecursiveFileSuffix,
        /// <summary>生成器只写一个规范化文件。</summary>
        ExactFile,
    }

    /// <summary>
    /// 一个生成输出或清理范围的中立声明。路径必须先经 <see cref="FrameworkProjectPath"/> 解析；Catalog
    /// 只比较已经成立的安全声明，不替所属 Module 判断 Profile 完整性、输入文件或工具链状态。
    /// </summary>
    public sealed class FrameworkGeneratedOutputClaim
    {
        private FrameworkGeneratedOutputClaim(
            string claimId,
            string ownerLabel,
            FrameworkGeneratedOutputClaimKind kind,
            string assetPath,
            string absolutePath,
            string fileSuffix)
        {
            if (string.IsNullOrWhiteSpace(claimId))
                throw new ArgumentException("输出 claim id 不能为空。", nameof(claimId));
            if (string.IsNullOrWhiteSpace(ownerLabel))
                throw new ArgumentException("输出 claim 的 owner 标签不能为空。", nameof(ownerLabel));
            if (string.IsNullOrWhiteSpace(assetPath))
                throw new ArgumentException("输出 claim 的 Asset Path 不能为空。", nameof(assetPath));
            if (string.IsNullOrWhiteSpace(absolutePath))
                throw new ArgumentException("输出 claim 的绝对路径不能为空。", nameof(absolutePath));
            if (!FrameworkProjectPath.TryResolve(
                    assetPath,
                    out string normalizedAssetPath,
                    out string resolvedAbsolutePath,
                    out string pathError) ||
                !normalizedAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException(
                    "输出 claim 必须指向 Assets 子项：" + pathError,
                    nameof(assetPath));
            if (!FrameworkProjectPath.PathsEqual(resolvedAbsolutePath, absolutePath))
                throw new ArgumentException(
                    $"输出 claim 的 Asset Path 与绝对路径不是同一目标：{assetPath} ↔ {absolutePath}",
                    nameof(absolutePath));
            if (kind == FrameworkGeneratedOutputClaimKind.RecursiveFileSuffix)
            {
                if (string.IsNullOrWhiteSpace(fileSuffix) ||
                    fileSuffix[0] != '.' ||
                    fileSuffix.IndexOfAny(new[] { '*', '?', '/', '\\' }) >= 0)
                    throw new ArgumentException(
                        "递归文件 claim 必须使用不含通配符或目录段的点号后缀，例如 .g.cs。",
                        nameof(fileSuffix));
            }
            else if (!string.IsNullOrEmpty(fileSuffix))
            {
                throw new ArgumentException("只有递归文件 claim 可以声明后缀。", nameof(fileSuffix));
            }

            ClaimId = claimId;
            OwnerLabel = ownerLabel;
            Kind = kind;
            AssetPath = normalizedAssetPath.TrimEnd('/');
            AbsolutePath = Path.GetFullPath(resolvedAbsolutePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            FileSuffix = fileSuffix ?? string.Empty;
        }

        /// <summary>在所属来源内稳定且唯一的声明身份，用于诊断和替换瞬时配置。</summary>
        public string ClaimId { get; }
        /// <summary>面向使用者的 owner 标签，通常包含生成器、Profile 和产物槽位。</summary>
        public string OwnerLabel { get; }
        /// <summary>写入或清理范围的粒度。</summary>
        public FrameworkGeneratedOutputClaimKind Kind { get; }
        /// <summary>规范化、使用正斜杠的 Unity Asset Path。</summary>
        public string AssetPath { get; }
        /// <summary>规范化绝对路径；只供 Editor Catalog 比较，不应写入项目配置。</summary>
        public string AbsolutePath { get; }
        /// <summary>递归认领的文件后缀；其它种类为空。</summary>
        public string FileSuffix { get; }

        /// <summary>声明一个会被生成器独占并整理的目录。</summary>
        public static FrameworkGeneratedOutputClaim ExclusiveDirectory(
            string claimId, string ownerLabel, string assetPath, string absolutePath) =>
            new(claimId, ownerLabel, FrameworkGeneratedOutputClaimKind.ExclusiveDirectory,
                assetPath, absolutePath, string.Empty);

        /// <summary>声明一个目录树内会被递归写入和清理的文件后缀。</summary>
        public static FrameworkGeneratedOutputClaim RecursiveFileSuffix(
            string claimId,
            string ownerLabel,
            string assetPath,
            string absolutePath,
            string fileSuffix) =>
            new(claimId, ownerLabel, FrameworkGeneratedOutputClaimKind.RecursiveFileSuffix,
                assetPath, absolutePath, fileSuffix);

        /// <summary>声明一个精确输出文件。</summary>
        public static FrameworkGeneratedOutputClaim ExactFile(
            string claimId, string ownerLabel, string assetPath, string absolutePath) =>
            new(claimId, ownerLabel, FrameworkGeneratedOutputClaimKind.ExactFile,
                assetPath, absolutePath, string.Empty);
    }

    /// <summary>
    /// 一个可选 Editor Module 向 Catalog 注册的 claim 来源。Collector 只能读取配置并返回已成立声明，不能写盘、
    /// 启动外部进程或反向调用 Catalog；异常会让其它生成器 fail-fast，避免把证据缺失误判为没有冲突。
    /// </summary>
    public sealed class FrameworkGeneratedOutputClaimSource
    {
        private readonly Func<IReadOnlyList<FrameworkGeneratedOutputClaim>> _collectClaims;

        /// <summary>创建一个按需读取当前项目配置的来源。</summary>
        public FrameworkGeneratedOutputClaimSource(
            string id,
            string title,
            Func<IReadOnlyList<FrameworkGeneratedOutputClaim>> collectClaims)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("claim 来源 id 不能为空。", nameof(id));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("claim 来源标题不能为空。", nameof(title));
            Id = id;
            Title = title;
            _collectClaims = collectClaims ?? throw new ArgumentNullException(nameof(collectClaims));
        }

        /// <summary>跨域重载稳定、跨 Module 唯一的来源身份。</summary>
        public string Id { get; }
        /// <summary>采集失败时展示的生成器名称。</summary>
        public string Title { get; }

        internal IReadOnlyList<FrameworkGeneratedOutputClaim> CollectClaims() => _collectClaims();

        internal bool HasSameRegistration(FrameworkGeneratedOutputClaimSource other) =>
            other != null &&
            string.Equals(Title, other.Title, StringComparison.Ordinal) &&
            Equals(_collectClaims, other._collectClaims);
    }

    /// <summary>
    /// 可选生成器之间的输出占用 Catalog。Module 自注册 claim 来源；预览只消费已有快照且不会冷启动
    /// Collector，真正写盘前强制重采集，因而窗口不会暗中扫描工程，动作层也不会拿过期声明冒充安全证据。
    /// </summary>
    public static class FrameworkGeneratedOutputClaimCatalog
    {
        private static readonly Dictionary<string, FrameworkGeneratedOutputClaimSource> Sources =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<string, IReadOnlyList<FrameworkGeneratedOutputClaim>> CachedClaims =
            new(StringComparer.Ordinal);

        static FrameworkGeneratedOutputClaimCatalog()
        {
            EditorApplication.projectChanged += Invalidate;
        }

        /// <summary>
        /// 登记一个 claim 来源。完全相同的静态 Collector 可安全重入；相同 id 的不同来源直接失败，避免后加载
        /// Module 静默替换清理边界。
        /// </summary>
        public static void Register(FrameworkGeneratedOutputClaimSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (Sources.TryGetValue(source.Id, out FrameworkGeneratedOutputClaimSource existing))
            {
                if (existing.HasSameRegistration(source)) return;
                throw new InvalidOperationException(
                    $"输出 claim 来源 id '{source.Id}' 已由“{existing.Title}”注册，" +
                    $"不能再由“{source.Title}”覆盖。请为不同生成器使用稳定且唯一的 id。");
            }

            Sources.Add(source.Id, source);
            CachedClaims.Remove(source.Id);
        }

        /// <summary>返回按 id 稳定排序的独立来源快照；不会执行 Collector。</summary>
        public static IReadOnlyList<FrameworkGeneratedOutputClaimSource> SnapshotSources() => Sources.Values
            .OrderBy(source => source.Id, StringComparer.Ordinal)
            .ToArray();

        /// <summary>清除只读 claim 快照；后续预览会报告证据待采集，下一次写盘检查会强制重新采集。</summary>
        public static void Invalidate() => CachedClaims.Clear();

        /// <summary>
        /// 用已有缓存的其它来源声明检查当前来源，适合窗口重绘；没有快照的来源只会在消息中标为待核对，
        /// 绝不因预览冷启动 Collector。当前来源的 <paramref name="claims"/> 始终由调用方现场提供，因此正在
        /// 编辑的本 Module Profile 不会被自己的缓存覆盖。调用方必须展示成功消息中的证据缺口。
        /// </summary>
        public static bool TryValidateForPreview(
            string sourceId,
            IReadOnlyList<FrameworkGeneratedOutputClaim> claims,
            out string message) =>
            TryValidate(sourceId, claims, refreshOtherSources: false, out message);

        /// <summary>
        /// 在任何创建、覆盖或清理前强制重采集其它来源并检查。Collector 失败或 claim 冲突都返回
        /// <c>false</c>；调用方不得继续写盘。
        /// </summary>
        public static bool TryValidateBeforeWrite(
            string sourceId,
            IReadOnlyList<FrameworkGeneratedOutputClaim> claims,
            out string message) =>
            TryValidate(sourceId, claims, refreshOtherSources: true, out message);

        private static bool TryValidate(
            string sourceId,
            IReadOnlyList<FrameworkGeneratedOutputClaim> claims,
            bool refreshOtherSources,
            out string message)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("当前 claim 来源 id 不能为空。", nameof(sourceId));
            if (claims == null) throw new ArgumentNullException(nameof(claims));
            if (claims.Any(claim => claim == null))
            {
                message = $"输出 claim 来源 '{sourceId}' 返回了空声明；为避免漏检，生成已停止。";
                return false;
            }
            if (TryFindDuplicateClaimId(claims, out string duplicateClaimId))
            {
                message =
                    $"输出 claim 来源 '{sourceId}' 重复声明 id '{duplicateClaimId}'；" +
                    "无法可靠区分或替换产物，生成已停止。";
                return false;
            }

            var externalClaims = new List<FrameworkGeneratedOutputClaim>();
            var pendingSources = new List<string>();
            foreach (FrameworkGeneratedOutputClaimSource source in Sources.Values
                         .Where(source => !string.Equals(source.Id, sourceId, StringComparison.Ordinal))
                         .OrderBy(source => source.Id, StringComparer.Ordinal))
            {
                if (!refreshOtherSources)
                {
                    if (!CachedClaims.TryGetValue(source.Id, out
                            IReadOnlyList<FrameworkGeneratedOutputClaim> cached))
                    {
                        pendingSources.Add(source.Title);
                        continue;
                    }
                    externalClaims.AddRange(cached);
                    continue;
                }

                if (!TryCollect(source,
                        out IReadOnlyList<FrameworkGeneratedOutputClaim> collected, out string collectError))
                {
                    message = collectError;
                    return false;
                }
                externalClaims.AddRange(collected);
            }

            if (!TryValidateAgainst(claims, externalClaims, out message)) return false;
            if (pendingSources.Count == 0) return true;

            message =
                $"{claims.Count} 项当前输出 claim 已与 {externalClaims.Count} 项缓存声明核对；" +
                $"另有 {pendingSources.Count} 个 Module 尚无预览快照（{string.Join("、", pendingSources)}）。" +
                "窗口没有为此暗中扫描工程；真正写盘前会强制重采全部来源并阻止冲突。";
            return true;
        }

        private static bool TryCollect(
            FrameworkGeneratedOutputClaimSource source,
            out IReadOnlyList<FrameworkGeneratedOutputClaim> claims,
            out string error)
        {
            try
            {
                claims = source.CollectClaims();
                if (claims == null || claims.Any(claim => claim == null))
                    throw new InvalidOperationException("Collector 返回了 null 列表或空声明项。");
                claims = claims.ToArray();
                if (TryFindDuplicateClaimId(claims, out string duplicateClaimId))
                    throw new InvalidOperationException($"Collector 重复声明 claim id '{duplicateClaimId}'。");
                CachedClaims[source.Id] = claims;
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                CachedClaims.Remove(source.Id);
                claims = Array.Empty<FrameworkGeneratedOutputClaim>();
                error =
                    $"无法读取“{source.Title}”的输出 claim：{exception.GetType().Name}: {exception.Message}\n" +
                    "为避免遗漏其它生成器的清理范围，本次生成未开始。";
                return false;
            }
        }

        internal static bool TryValidateAgainst(
            IReadOnlyList<FrameworkGeneratedOutputClaim> claims,
            IReadOnlyList<FrameworkGeneratedOutputClaim> externalClaims,
            out string message)
        {
            if (claims == null) throw new ArgumentNullException(nameof(claims));
            if (externalClaims == null) throw new ArgumentNullException(nameof(externalClaims));
            if (TryFindFirstConflict(claims, externalClaims,
                    out FrameworkGeneratedOutputClaim left,
                    out FrameworkGeneratedOutputClaim right))
            {
                message =
                    $"输出所有权冲突：【{left.OwnerLabel}】{Describe(left)} 与" +
                    $"【{right.OwnerLabel}】{Describe(right)} 覆盖同一写入或清理范围。\n" +
                    "后执行的生成器可能覆盖或删除前一项产物；请调整输出，使独占目录、递归清理范围与精确文件彼此兼容。";
                return false;
            }

            message = $"{claims.Count} 项当前输出 claim 已与 {externalClaims.Count} 项其它 Module 声明核对。";
            return true;
        }

        internal static bool TryFindFirstConflict(
            IReadOnlyList<FrameworkGeneratedOutputClaim> claims,
            IReadOnlyList<FrameworkGeneratedOutputClaim> externalClaims,
            out FrameworkGeneratedOutputClaim left,
            out FrameworkGeneratedOutputClaim right)
        {
            for (int i = 0; i < claims.Count; i++)
            for (int j = i + 1; j < claims.Count; j++)
            {
                if (!ClaimsConflict(claims[i], claims[j])) continue;
                left = claims[i];
                right = claims[j];
                return true;
            }

            foreach (FrameworkGeneratedOutputClaim local in claims)
            foreach (FrameworkGeneratedOutputClaim external in externalClaims)
            {
                if (!ClaimsConflict(local, external)) continue;
                left = local;
                right = external;
                return true;
            }

            left = null;
            right = null;
            return false;
        }

        private static bool ClaimsConflict(
            FrameworkGeneratedOutputClaim left,
            FrameworkGeneratedOutputClaim right)
        {
            if (left.Kind == FrameworkGeneratedOutputClaimKind.ExclusiveDirectory ||
                right.Kind == FrameworkGeneratedOutputClaimKind.ExclusiveDirectory)
                return FrameworkProjectPath.DirectoriesOverlap(left.AbsolutePath, right.AbsolutePath);

            if (left.Kind == FrameworkGeneratedOutputClaimKind.ExactFile &&
                right.Kind == FrameworkGeneratedOutputClaimKind.ExactFile)
                return FrameworkProjectPath.PathsEqual(left.AbsolutePath, right.AbsolutePath);

            if (left.Kind == FrameworkGeneratedOutputClaimKind.RecursiveFileSuffix &&
                right.Kind == FrameworkGeneratedOutputClaimKind.RecursiveFileSuffix)
                return SuffixesOverlap(left.FileSuffix, right.FileSuffix) &&
                       FrameworkProjectPath.DirectoriesOverlap(left.AbsolutePath, right.AbsolutePath);

            FrameworkGeneratedOutputClaim recursive =
                left.Kind == FrameworkGeneratedOutputClaimKind.RecursiveFileSuffix ? left : right;
            FrameworkGeneratedOutputClaim exact = ReferenceEquals(recursive, left) ? right : left;
            if (!FrameworkProjectPath.DirectoriesOverlap(recursive.AbsolutePath, exact.AbsolutePath))
                return false;
            if (FrameworkProjectPath.PathsEqual(recursive.AbsolutePath, exact.AbsolutePath))
                return true;
            if (FrameworkProjectPath.ContainsPath(recursive.AbsolutePath, exact.AbsolutePath))
                return exact.AssetPath.EndsWith(recursive.FileSuffix, StringComparison.OrdinalIgnoreCase);

            // 精确文件路径不能同时充当递归目录的祖先，即使文件名本身不匹配清理后缀。
            return true;
        }

        private static bool SuffixesOverlap(string left, string right) =>
            left.EndsWith(right, StringComparison.OrdinalIgnoreCase) ||
            right.EndsWith(left, StringComparison.OrdinalIgnoreCase);

        private static bool TryFindDuplicateClaimId(
            IEnumerable<FrameworkGeneratedOutputClaim> claims,
            out string duplicateClaimId)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (FrameworkGeneratedOutputClaim claim in claims)
            {
                if (ids.Add(claim.ClaimId)) continue;
                duplicateClaimId = claim.ClaimId;
                return true;
            }

            duplicateClaimId = string.Empty;
            return false;
        }

        private static string Describe(FrameworkGeneratedOutputClaim claim) => claim.Kind switch
        {
            FrameworkGeneratedOutputClaimKind.ExclusiveDirectory =>
                $"独占并整理目录 {claim.AssetPath}",
            FrameworkGeneratedOutputClaimKind.RecursiveFileSuffix =>
                $"在 {claim.AssetPath} 递归清理 *{claim.FileSuffix}",
            _ => $"写入文件 {claim.AssetPath}",
        };

        internal static bool Unregister(string id)
        {
            CachedClaims.Remove(id);
            return !string.IsNullOrEmpty(id) && Sources.Remove(id);
        }
    }
}
