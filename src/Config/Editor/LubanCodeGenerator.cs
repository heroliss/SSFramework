using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Game.Framework.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Framework.Config.Editor
{
    /// <summary>
    /// 配置表生成管线：封装 Luban CLI 调用——读表定义与数据（路径由 <see cref="LubanConfigProfile"/> 提供），
    /// 产出配置 C# 代码 + 二进制数据文件，并附带生成<b>表清单类</b>（LubanTableManifest.g.cs）。
    ///
    /// <para><b>为什么要清单</b>：生成的表根（Tables）构造函数按文件名同步向 loader 要字节，而运行时资源加载是异步的——
    /// 初始化 System 需要先知道「有哪些数据文件」才能并行预载。清单由已验证的数据快照生成，并与代码 / 数据
    /// 经同一事务发布，不存在手工维护漏表（机制同热更代码包的 manifest）。</para>
    ///
    /// <para>CLI 只写工程临时目录；完整产物通过校验后，<see cref="LubanGenerationTransaction"/> 才把
    /// 代码、数据与清单差量发布为同一代。CLI 或发布失败不会把正式目录留在半新半旧状态。</para>
    /// </summary>
    public static class LubanCodeGenerator
    {
        internal const string OutputClaimSourceId = "luban";
        internal const string CodeTarget = "cs-bin";
        internal const string DataTarget = "bin";

        /// <summary>一次受控 CLI 调用的不可变参数；输出目录只能来自当前事务的 staging。</summary>
        internal readonly struct LubanCliInvocation
        {
            internal string ToolPath { get; }
            internal string WorkingDirectory { get; }
            internal string Target { get; }
            internal string ConfPath { get; }
            internal string OutputCodeDirectory { get; }
            internal string OutputDataDirectory { get; }
            internal IReadOnlyList<string> ExtraArguments { get; }

            internal LubanCliInvocation(
                string toolPath,
                string workingDirectory,
                string target,
                string confPath,
                string outputCodeDirectory,
                string outputDataDirectory,
                IReadOnlyList<string> extraArguments)
            {
                ToolPath = toolPath;
                WorkingDirectory = workingDirectory;
                Target = target;
                ConfPath = confPath;
                OutputCodeDirectory = outputCodeDirectory;
                OutputDataDirectory = outputDataDirectory;
                ExtraArguments = extraArguments ?? Array.Empty<string>();
            }
        }

        /// <summary>
        /// 外部 Luban 进程的窄 Seam。生产 Implementation 负责参数转义、双路输出与超时；
        /// 测试 fake 可只写 staging 并返回指定 exit code，不需要抽象整个文件系统。
        /// </summary>
        internal interface ILubanCliRunner
        {
            (int exitCode, string log) Run(LubanCliInvocation invocation);
        }

        internal readonly struct GenerationPrerequisiteReport
        {
            internal bool CanGenerate { get; }
            internal string Message { get; }
            internal string ToolPath { get; }
            internal string ConfPath { get; }
            internal string OutputCodeAssetPath { get; }
            internal string OutputCodeDirectory { get; }
            internal string OutputDataAssetPath { get; }
            internal string OutputDataDirectory { get; }
            internal IReadOnlyList<string> ExtraArguments { get; }

            internal GenerationPrerequisiteReport(
                bool canGenerate,
                string message,
                string toolPath = "",
                string confPath = "",
                string outputCodeAssetPath = "",
                string outputCodeDirectory = "",
                string outputDataAssetPath = "",
                string outputDataDirectory = "",
                IReadOnlyList<string> extraArguments = null)
            {
                CanGenerate = canGenerate;
                Message = message;
                ToolPath = toolPath;
                ConfPath = confPath;
                OutputCodeAssetPath = outputCodeAssetPath;
                OutputCodeDirectory = outputCodeDirectory;
                OutputDataAssetPath = outputDataAssetPath;
                OutputDataDirectory = outputDataDirectory;
                ExtraArguments = extraArguments ?? Array.Empty<string>();
            }
        }

        private const int TimeoutMs = 120_000;
        private static readonly ILubanCliRunner DefaultCliRunner = new ProcessLubanCliRunner();

        /// <summary>
        /// 执行一次完整生成（代码 + 数据 + 清单），返回是否成功与人类可读摘要。
        /// 产物有变化时，成功后已 <c>AssetDatabase.Refresh()</c>，Unity 侧产物立即可用；
        /// 内容完全未变时不触发刷新与无谓重编译。
        /// </summary>
        public static (bool ok, string message) Generate(LubanConfigProfile profile) =>
            Generate(profile, DefaultCliRunner);

        /// <summary>允许 Editor 测试替换外部进程，完整生成、校验与发布路径仍走生产 Implementation。</summary>
        internal static (bool ok, string message) Generate(
            LubanConfigProfile profile,
            ILubanCliRunner cliRunner)
        {
            if (profile == null)
                return (false, "没有 LubanConfigProfile，无法生成。");
            if (cliRunner == null)
                return (false, "Luban CLI runner 不能为空。");

            var ownershipProfiles = LubanConfigProfile.ResolveAll().Concat(new[] { profile }).Distinct().ToArray();
            var (ownershipOk, ownershipMessage) = ValidateOutputOwnership(
                ownershipProfiles, beforeWrite: true);
            if (!ownershipOk) return (false, ownershipMessage);
            GenerationPrerequisiteReport prerequisites = InspectGenerationPrerequisites(profile);
            if (!prerequisites.CanGenerate) return (false, prerequisites.Message);

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            string transactionRoot = Path.GetFullPath(FileUtil.GetUniqueTempPathInProject());
            LubanGenerationTransaction transaction = null;
            LubanGenerationTransaction.PublishReport? publishReport = null;
            string cliLog = string.Empty;
            try
            {
                transaction = new LubanGenerationTransaction(
                    transactionRoot,
                    prerequisites.OutputCodeDirectory,
                    prerequisites.OutputDataDirectory,
                    Application.dataPath);
                var invocation = new LubanCliInvocation(
                    prerequisites.ToolPath,
                    projectRoot,
                    profile.Target,
                    prerequisites.ConfPath,
                    transaction.StagingCodeDirectory,
                    transaction.StagingDataDirectory,
                    prerequisites.ExtraArguments);
                var (exitCode, log) = cliRunner.Run(invocation);
                cliLog = log ?? string.Empty;
                if (exitCode != 0)
                    return (false,
                        $"Luban 生成失败（exit {exitCode}）；暂存产物已丢弃，正式代码与数据未修改。" +
                        $"\nCLI 输出：\n{cliLog}");

                // CLI 可能运行较久；真正提交前重新采集全部 owner，不能沿用点击时的所有权快照。
                var commitProfiles = LubanConfigProfile.ResolveAll().Concat(new[] { profile }).Distinct().ToArray();
                var (commitOwnershipOk, commitOwnershipMessage) = ValidateOutputOwnership(
                    commitProfiles, beforeWrite: true);
                if (!commitOwnershipOk)
                    return (false,
                        "Luban CLI 已完成，但发布前输出所有权重新检查失败；暂存产物已丢弃，正式目录未修改。\n" +
                        commitOwnershipMessage);

                AssetDatabase.DisallowAutoRefresh();
                try
                {
                    publishReport = transaction.ValidateAndPublish(profile.ManifestNamespace);
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                }
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException)
            {
                return (false, "Luban 进程、产物校验或事务发布未能完成：" + exception.Message);
            }
            finally
            {
                transaction?.Dispose();
                if (!string.IsNullOrEmpty(transaction?.CleanupWarning))
                    Debug.LogWarning("[配置表构建] " + transaction.CleanupWarning);
            }

            if (!publishReport.HasValue)
                return (false, "Luban CLI 已结束，但没有得到可发布的产物报告。");

            LubanGenerationTransaction.PublishReport report = publishReport.Value;
            if (report.HasChanges)
            {
                try
                {
                    AssetDatabase.Refresh();
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    return (false,
                        "Luban 代码、数据与清单已一致发布，但 Unity 资产刷新失败；请手动执行 Assets/Refresh。" +
                        $"\n原因：{exception.Message}");
                }
            }

            string changeSummary = report.HasChanges ? "已差量发布" : "内容未变，未写盘也未刷新";
            return (true,
                $"生成完成（{changeSummary}）。\n" +
                $"  代码 → {prerequisites.OutputCodeAssetPath}（{report.Code}）\n" +
                $"  数据 → {prerequisites.OutputDataAssetPath}（{report.Data}）\n" +
                $"  {report.ManifestSummary}" +
                (cliLog.Length > 0 ? $"\n\nCLI 输出：\n{cliLog}" : string.Empty));
        }

        /// <summary>
        /// 只读检查 Luban 字段、路径、CLI 与 luban.conf 是否就绪。不创建输出目录、不启动 CLI，
        /// 也不解析 conf；生成动作会在点击后重新执行同一检查。
        /// </summary>
        internal static GenerationPrerequisiteReport InspectGenerationPrerequisites(
            LubanConfigProfile profile)
        {
            if (profile == null)
                return new GenerationPrerequisiteReport(false, "LubanConfigProfile 不能为空。");

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(profile.LubanToolPath)) missing.Add("Luban CLI 路径");
            if (string.IsNullOrWhiteSpace(profile.ConfPath)) missing.Add("luban.conf 路径");
            if (string.IsNullOrWhiteSpace(profile.OutputCodeDir)) missing.Add("代码输出目录");
            if (string.IsNullOrWhiteSpace(profile.OutputDataDir)) missing.Add("数据输出目录");
            if (string.IsNullOrWhiteSpace(profile.Target)) missing.Add("生成目标");
            if (missing.Count > 0)
                return new GenerationPrerequisiteReport(
                    false,
                    "Profile 尚未配置完整：" + string.Join("、", missing) + "。");
            if (!string.IsNullOrEmpty(profile.ManifestNamespace) &&
                !FrameworkCSharpSyntax.TryValidateNamespace(
                    profile.ManifestNamespace, out string namespaceError))
                return new GenerationPrerequisiteReport(
                    false,
                    "清单命名空间无效：" + namespaceError);
            if (!TryParseExtraArguments(profile.ExtraArgs, out IReadOnlyList<string> extraArguments, out string extraError))
                return new GenerationPrerequisiteReport(false, "附加 CLI 参数无效：" + extraError);

            if (!FrameworkProjectPath.TryResolve(
                    profile.LubanToolPath, out _, out string toolPath, out string toolError))
                return new GenerationPrerequisiteReport(false, "Luban CLI 路径无效：" + toolError);
            if (!FrameworkProjectPath.TryResolve(
                    profile.ConfPath, out _, out string confPath, out string confError))
                return new GenerationPrerequisiteReport(false, "luban.conf 路径无效：" + confError);
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputCodeDir,
                    out string outputCodeAssetPath,
                    out string outputCodeDirectory,
                    out string codeError))
                return new GenerationPrerequisiteReport(false, "代码输出目录无效：" + codeError);
            if (!FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputDataDir,
                    out string outputDataAssetPath,
                    out string outputDataDirectory,
                    out string dataError))
                return new GenerationPrerequisiteReport(false, "数据输出目录无效：" + dataError);

            var issues = new List<string>();
            if (!File.Exists(toolPath))
                issues.Add(
                    $"Luban CLI 不存在：{toolPath}\n" +
                    "下一步：从 Luban 官方 release 下载后，将可执行文件放到配置路径。");
            if (!File.Exists(confPath))
                issues.Add($"luban.conf 不存在：{confPath}");
            if (issues.Count > 0)
                return new GenerationPrerequisiteReport(
                    false,
                    string.Join("\n", issues),
                    toolPath,
                    confPath,
                    outputCodeAssetPath,
                    outputCodeDirectory,
                    outputDataAssetPath,
                    outputDataDirectory,
                    extraArguments);

            return new GenerationPrerequisiteReport(
                true,
                "Luban CLI、luban.conf 与代码/数据输出路径均已就绪。",
                toolPath,
                confPath,
                outputCodeAssetPath,
                outputCodeDirectory,
                outputDataAssetPath,
                outputDataDirectory,
                extraArguments);
        }

        /// <summary>
        /// 比较所有已经成立的代码 / 数据输出声明。缺失或无效路径不形成声明，留给所属 Profile 的就绪检查；
        /// 因此空白新配置不会冻结其它配置，但未就绪 Profile 中任何有效的单项输出仍参与比较。
        /// 任意两个声明（包括同一 Profile 的代码与数据）不得相同或互为父子，并会与其它 Module 的目录、
        /// 后缀清理和精确文件 claim 共同检查；失败不创建目录、不启动 CLI。
        /// </summary>
        public static (bool ok, string message) ValidateOutputOwnership(
            System.Collections.Generic.IReadOnlyList<LubanConfigProfile> profiles) =>
            ValidateOutputOwnership(profiles, beforeWrite: false);

        private static (bool ok, string message) ValidateOutputOwnership(
            System.Collections.Generic.IReadOnlyList<LubanConfigProfile> profiles,
            bool beforeWrite)
        {
            if (profiles == null || profiles.Count == 0)
                return (false, "没有可验证的 LubanConfigProfile。");

            var claims = new List<FrameworkGeneratedOutputClaim>();
            int unresolvedCount = 0;
            foreach (LubanConfigProfile candidate in profiles)
            {
                if (candidate == null) return (false, "配置列表含已删除或空的 LubanConfigProfile。");
                unresolvedCount += AddOutputClaims(candidate, claims);
            }

            string ownershipMessage;
            bool ownershipOk = beforeWrite
                ? FrameworkGeneratedOutputClaimCatalog.TryValidateBeforeWrite(
                    OutputClaimSourceId, claims, out ownershipMessage)
                : FrameworkGeneratedOutputClaimCatalog.TryValidateForPreview(
                    OutputClaimSourceId, claims, out ownershipMessage);
            if (!ownershipOk) return (false, ownershipMessage);

            string previewEvidence = beforeWrite ? string.Empty : "\n" + ownershipMessage;
            if (claims.Count == 0)
                return (true,
                    "尚无有效代码 / 数据输出声明；缺失或无效路径会在对应配置卡片中提示。" +
                    previewEvidence);

            string unresolvedNote = unresolvedCount > 0
                ? $"；另有 {unresolvedCount} 项尚未形成有效声明，由各自就绪检查提示"
                : string.Empty;
            return (true, $"{claims.Count} 项代码 / 数据输出声明当前有效{unresolvedNote}。{previewEvidence}");
        }

        /// <summary>供共享 Catalog 按需读取当前 Module 已成立的目录声明；不执行生成或完整就绪检查。</summary>
        internal static IReadOnlyList<FrameworkGeneratedOutputClaim> CollectRegisteredOutputClaims()
        {
            var claims = new List<FrameworkGeneratedOutputClaim>();
            foreach (LubanConfigProfile profile in LubanConfigProfile.ResolveAll())
                AddOutputClaims(profile, claims);
            return claims;
        }

        private static int AddOutputClaims(
            LubanConfigProfile profile,
            ICollection<FrameworkGeneratedOutputClaim> claims)
        {
            int unresolvedCount = 0;
            string profileId = ProfileClaimId(profile);
            if (FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputCodeDir,
                    out string codeAssetPath,
                    out string codeAbsolutePath,
                    out _))
                claims.Add(FrameworkGeneratedOutputClaim.ExclusiveDirectory(
                    profileId + ":code",
                    $"Luban【{profile.name}】代码",
                    codeAssetPath,
                    codeAbsolutePath));
            else
                unresolvedCount++;

            if (FrameworkProjectPath.TryResolveAssetsDirectory(
                    profile.OutputDataDir,
                    out string dataAssetPath,
                    out string dataAbsolutePath,
                    out _))
                claims.Add(FrameworkGeneratedOutputClaim.ExclusiveDirectory(
                    profileId + ":data",
                    $"Luban【{profile.name}】数据",
                    dataAssetPath,
                    dataAbsolutePath));
            else
                unresolvedCount++;
            return unresolvedCount;
        }

        private static string ProfileClaimId(LubanConfigProfile profile)
        {
            string assetPath = AssetDatabase.GetAssetPath(profile);
            return string.IsNullOrEmpty(assetPath)
                ? $"transient:{profile.name}:{profile.GetInstanceID()}"
                : assetPath;
        }

        /// <summary>
        /// 把 Profile 中便于编辑的命令行文本解析为 <see cref="ProcessStartInfo.ArgumentList"/> 项；
        /// 拒绝未闭合引号、畸形 xargs、会进入常驻 watch 的参数，以及试图覆盖本生成器所拥有
        /// target / conf / 输出目录的参数。
        /// </summary>
        internal static bool TryParseExtraArguments(
            string commandLine,
            out IReadOnlyList<string> arguments,
            out string error)
        {
            var parsed = new List<string>();
            var current = new StringBuilder();
            char quote = '\0';
            bool tokenStarted = false;
            string source = commandLine ?? string.Empty;

            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                        tokenStarted = true;
                    }
                    else if (character == '\\' && index + 1 < source.Length &&
                             (source[index + 1] == quote || source[index + 1] == '\\'))
                    {
                        current.Append(source[++index]);
                        tokenStarted = true;
                    }
                    else
                    {
                        current.Append(character);
                        tokenStarted = true;
                    }
                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    if (!tokenStarted) continue;
                    parsed.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
                else if (character is '"' or '\'')
                {
                    quote = character;
                    tokenStarted = true;
                }
                else if (character == '\\' && index + 1 < source.Length &&
                         (source[index + 1] is '"' or '\''))
                {
                    current.Append(source[++index]);
                    tokenStarted = true;
                }
                else
                {
                    current.Append(character);
                    tokenStarted = true;
                }
            }

            if (quote != '\0')
            {
                arguments = Array.Empty<string>();
                error = $"存在未闭合的 {quote} 引号。";
                return false;
            }
            if (tokenStarted) parsed.Add(current.ToString());

            var xargKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < parsed.Count; index++)
            {
                string token = parsed[index];
                if (IsGeneratorOwnedOption(token))
                {
                    arguments = Array.Empty<string>();
                    error =
                        "target / codeTarget / dataTarget / conf / validationFailAsError 由 LubanConfigProfile 与生成事务统一提供，不能在附加参数中重复设置。";
                    return false;
                }
                if (IsWatchOption(token))
                {
                    arguments = Array.Empty<string>();
                    error = "watchDir 会让 Luban 进入常驻循环，不适用于一次性 Editor 生成事务。";
                    return false;
                }
                if (IsUnsupportedCompactShortOption(token))
                {
                    arguments = Array.Empty<string>();
                    error =
                        $"不支持 compact / bundled 短参数：{token}。除 -xkey=value 外，请把短选项与值分开书写。";
                    return false;
                }
                if (token.Equals("-x", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("--x", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("--xargs", StringComparison.OrdinalIgnoreCase))
                {
                    if (++index >= parsed.Count)
                    {
                        arguments = Array.Empty<string>();
                        error = $"{token} 后缺少 key=value。";
                        return false;
                    }
                    if (!TryRegisterXarg(parsed[index], xargKeys, out string xargError))
                    {
                        arguments = Array.Empty<string>();
                        error = xargError;
                        return false;
                    }
                    continue;
                }

                string inlineAssignment = null;
                if (IsAttachedShortOption(token, 'x'))
                    inlineAssignment = token[2] == '=' ? token.Substring(3) : token.Substring(2);
                else if (token.StartsWith("--x=", StringComparison.OrdinalIgnoreCase))
                    inlineAssignment = token.Substring(4);
                else if (token.StartsWith("--xargs=", StringComparison.OrdinalIgnoreCase))
                    inlineAssignment = token.Substring(8);
                if (inlineAssignment != null &&
                    !TryRegisterXarg(inlineAssignment, xargKeys, out string inlineError))
                {
                    arguments = Array.Empty<string>();
                    error = inlineError;
                    return false;
                }
                if (inlineAssignment == null && IsReservedOutputAssignment(token))
                {
                    arguments = Array.Empty<string>();
                    error = "outputCodeDir / outputDataDir 由事务强制指向暂存目录，不能在附加参数中覆盖。";
                    return false;
                }
            }

            arguments = parsed;
            error = string.Empty;
            return true;
        }

        private static bool IsReservedOutputAssignment(string argument) =>
            argument.StartsWith("outputCodeDir=", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("outputDataDir=", StringComparison.OrdinalIgnoreCase);

        private static bool TryRegisterXarg(
            string assignment,
            ISet<string> keys,
            out string error)
        {
            int separator = assignment.IndexOf('=');
            if (separator <= 0)
            {
                error = $"xargs 必须是非空 key=value，当前为：{assignment}";
                return false;
            }

            string key = assignment.Substring(0, separator);
            if (key.Any(char.IsWhiteSpace))
            {
                error = $"xargs key 不能包含空白字符，当前为：{key}";
                return false;
            }
            if (IsReservedOutputAssignment(assignment))
            {
                error = "outputCodeDir / outputDataDir 由事务强制指向暂存目录，不能在附加参数中覆盖。";
                return false;
            }
            if (!keys.Add(key))
            {
                error = $"xargs key 不能重复：{key}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool IsGeneratorOwnedOption(string token) =>
            IsOption(token, "-t") ||
            IsAttachedShortOption(token, 't') ||
            IsOption(token, "--target") ||
            IsOption(token, "-c") ||
            IsAttachedShortOption(token, 'c') ||
            IsOption(token, "--codeTarget") ||
            IsOption(token, "-d") ||
            IsAttachedShortOption(token, 'd') ||
            IsOption(token, "--dataTarget") ||
            IsOption(token, "--conf") ||
            IsOption(token, "--validationFailAsError");

        private static bool IsWatchOption(string token) =>
            IsOption(token, "-w") ||
            IsAttachedShortOption(token, 'w') ||
            IsOption(token, "--watchDir");

        private static bool IsAttachedShortOption(string token, char option) =>
            token.Length > 2 &&
            token[0] == '-' &&
            token[1] != '-' &&
            char.ToUpperInvariant(token[1]) == char.ToUpperInvariant(option);

        private static bool IsUnsupportedCompactShortOption(string token) =>
            token.Length > 2 &&
            token[0] == '-' &&
            token[1] != '-' &&
            !IsAttachedShortOption(token, 'x');

        private static bool IsOption(string token, string option) =>
            token.Equals(option, StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase);

        private sealed class ProcessLubanCliRunner : ILubanCliRunner
        {
            public (int exitCode, string log) Run(LubanCliInvocation invocation)
            {
                var startInfo = new ProcessStartInfo(invocation.ToolPath)
                {
                    WorkingDirectory = invocation.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (string extraArgument in invocation.ExtraArguments)
                    startInfo.ArgumentList.Add(extraArgument);
                // 管线拥有的参数只追加一次；ExtraArgs 预检拒绝同名项，避免多 target、重复 xargs 或 watch 常驻。
                startInfo.ArgumentList.Add("-t");
                startInfo.ArgumentList.Add(invocation.Target);
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(CodeTarget);
                startInfo.ArgumentList.Add("-d");
                startInfo.ArgumentList.Add(DataTarget);
                startInfo.ArgumentList.Add("--conf");
                startInfo.ArgumentList.Add(invocation.ConfPath);
                // Luban 默认只记录 validator 失败；事务必须把语义校验失败提升为非零退出，不能只验证产物形状。
                startInfo.ArgumentList.Add("--validationFailAsError");
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add("outputCodeDir=" + invocation.OutputCodeDirectory);
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add("outputDataDir=" + invocation.OutputDataDirectory);

                // Luban CLI 按 .NET 8 发布；本机只装更高版本时允许向上滚动运行，避免强制再装一套旧运行时。
                startInfo.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";

                var log = new StringBuilder();
                using var process = new Process { StartInfo = startInfo };
                // 异步读两路输出（同步 ReadToEnd 在另一路缓冲填满时会互相等死）。
                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null) lock (log) log.AppendLine(eventArgs.Data);
                };
                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null) lock (log) log.AppendLine(eventArgs.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(TimeoutMs))
                {
                    process.Kill();
                    process.WaitForExit();
                    lock (log)
                        return (-1, log + $"\n（超过 {TimeoutMs / 1000}s 未结束，进程已终止）");
                }

                // 带超时的 WaitForExit 返回后再无参等待一次，确保异步输出回调全部排空（.NET 的既定用法）。
                process.WaitForExit();
                lock (log) return (process.ExitCode, log.ToString().Trim());
            }
        }
    }
}
