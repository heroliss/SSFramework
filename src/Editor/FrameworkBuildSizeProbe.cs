using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Process = System.Diagnostics.Process;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 在 <c>Library</c> 下的隔离 Unity 工程中执行 Framework Module 删除构建，并汇总真实 Player BuildReport。
    /// </summary>
    /// <remarks>
    /// 主工程里的 HybridCLR 清单、业务场景和未选 Module 的 <c>link.xml</c> 都会污染“Core-only”结果，
    /// 所以本探针不在当前工程里伪装裁剪。它只把所选 Module 及真实第三方依赖复制进一次性工程，
    /// 再用当前目标平台、脚本后端与裁剪级别构建空场景。所选程序集以 <c>preserve="all"</c> 保留，
    /// 数字表示确定性的体积上界，而不是某个具体游戏实际使用部分的精确增量。
    /// </remarks>
    internal static class FrameworkBuildSizeProbe
    {
        internal const string RunsRoot = "Library/SSFramework/BuildSizeProbe";
        internal const string ChildTemplateFileName = "FrameworkBuildSizeProbeChild.cs.txt";
        internal const string UnityIl2CppPathEnvironmentVariable = "UNITY_IL2CPP_PATH";

        private const string LatestRunPreferencePrefix = "SSFramework.BuildSizeProbe.LatestRun.";
        private static readonly string[] AlwaysRequiredPackages =
        {
            "com.cysharp.r3",
            "com.cysharp.unitask",
        };

        private static readonly Regex DependencyEntryRegex = new(
            "\\\"(?<id>[^\\\"]+)\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
            RegexOptions.Compiled);

        private static ActiveRun _activeRun;
        private static Process _childProcess;
        private static ProfilePlan _activeProfile;
        private static bool _stopAfterCurrent;

        internal static event Action Changed;

        [Serializable]
        internal sealed class ModuleSourcePlan
        {
            public string AssemblyName;
            public string AssetDirectory;
            [NonSerialized]
            public string PhysicalDirectory;
            public string PackageName;
            public string PackageVersion;
            public string PackageId;
            public string SourceFingerprint;
        }

        [Serializable]
        internal sealed class ProfilePlan
        {
            public string Key;
            public string Title;
            public string Description;
            public string[] RootAssemblies = Array.Empty<string>();
            public string[] Assemblies = Array.Empty<string>();
            public ModuleSourcePlan[] Sources = Array.Empty<ModuleSourcePlan>();
            public bool IsAdvanced;
        }

        [Serializable]
        internal sealed class OutputFileRecord
        {
            public string Path;
            public string Role;
            public long Bytes;
        }

        [Serializable]
        internal sealed class ProfileRecord
        {
            public string Key;
            public string Title;
            public string Status;
            public string Message;
            public string[] Assemblies = Array.Empty<string>();
            public ModuleSourcePlan[] Sources = Array.Empty<ModuleSourcePlan>();
            public string OutputPath;
            public string ResultPath;
            public string LogPath;
            public long BuildReportBytes;
            public long RawOutputBytes;
            public long OutputBytes;
            public double DurationSeconds;
            public int Errors;
            public int Warnings;
            public int ExitCode;
            public int ChildProcessId;
            public OutputFileRecord[] LargestFiles = Array.Empty<OutputFileRecord>();
        }

        [Serializable]
        internal sealed class RunReport
        {
            public int FormatVersion = 4;
            public string CreatedUtc;
            public string CompletedUtc;
            public string UnityVersion;
            public string Target;
            public string ScriptingBackend;
            public string StrippingLevel;
            public bool DevelopmentBuild;
            public string EvidenceScope;
            public string RunDirectory;
            public ProfileRecord[] Profiles = Array.Empty<ProfileRecord>();
        }

        private sealed class ActiveRun
        {
            internal string ProjectDirectory;
            internal string RunDirectory;
            internal Queue<ProfilePlan> Pending;
            internal RunReport Report;
        }

        [InitializeOnLoadMethod]
        private static void ScheduleRecovery()
        {
            EditorApplication.update -= RecoverWhenEditorReady;
            EditorApplication.update += RecoverWhenEditorReady;
        }

        private static void RecoverWhenEditorReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= RecoverWhenEditorReady;
            RecoverInterruptedRun();
        }

        internal static bool IsRunning => _activeRun != null;
        internal static bool StopAfterCurrentRequested => _stopAfterCurrent;
        internal static RunReport CurrentReport => _activeRun?.Report;

        /// <summary>
        /// 无窗口状态的 Core 删除测试入口，供 CI / AI 自动化复用与人工快速回归。
        /// 完整组合选择仍使用“真实构建体积证据”窗口。
        /// </summary>
        [MenuItem("SSFramework/诊断/AI 自动化/Core 隔离构建（Player Build）", priority = 31)]
        private static void StartCoreOnlyFromMenu()
        {
            try
            {
                Start(new[] { "core" });
                FrameworkEditorFeedback.ReportSummary(
                    "Core 隔离构建",
                    "已在 Library/SSFramework/BuildSizeProbe 启动独立 Player Build；结果完成后可在“真实构建体积证据”窗口查看。");
            }
            catch (Exception exception)
            {
                FrameworkEditorFeedback.ReportResult("Core 隔离构建", false, exception.Message);
            }
        }

        internal static string LatestRunDirectory =>
            EditorPrefs.GetString(LatestRunPreferencePrefix + HashProjectPath(), string.Empty);

        internal static RunReport LoadLatestReport()
        {
            string directory = LatestRunDirectory;
            string path = string.IsNullOrWhiteSpace(directory) ? string.Empty : Path.Combine(directory, "report.json");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var report = JsonUtility.FromJson<RunReport>(File.ReadAllText(path, Encoding.UTF8));
                NormalizeShippingEvidence(report);
                return report;
            }
            catch
            {
                return null;
            }
        }

        internal static void RevealLatestRun()
        {
            string directory = LatestRunDirectory;
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                EditorUtility.RevealInFinder(directory);
        }

        internal static void RebuildLatestReportsFromOutputs()
        {
            RunReport report = LoadLatestReport();
            if (report == null) return;
            report.FormatVersion = 4;
            WriteReports(report);
            Changed?.Invoke();
        }

        internal static ProfilePlan[] CreatePlans()
        {
            var result = FrameworkModuleAudit.Analyze(FrameworkModuleAudit.Capture());
            var profiles = result.CommonProfiles
                .Select(profile => (profile, advanced: false))
                .Concat(new[] { (profile: result.FullProfile, advanced: false) })
                .Concat(result.ModuleProfiles.Select(profile => (profile, advanced: true)));
            var runtimeByName = result.RuntimeModules.ToDictionary(module => module.Name, StringComparer.Ordinal);
            var sourceByName = runtimeByName.Values
                .Select(module => new ModuleSourcePlan
                {
                    AssemblyName = module.Name,
                    AssetDirectory = NormalizeAssetPath(Path.GetDirectoryName(module.AsmdefPath)),
                    PhysicalDirectory = module.SourceDirectory,
                    PackageName = module.PackageName,
                    PackageVersion = module.PackageVersion,
                    PackageId = module.PackageId,
                    SourceFingerprint = ComputeModuleSourceFingerprint(module.SourceDirectory),
                })
                .ToDictionary(source => source.AssemblyName, StringComparer.Ordinal);
            ValidateDisjointSourceDirectories(sourceByName.Values);
            return profiles.Select(item =>
            {
                FrameworkModuleAudit.AuditProfile profile = item.profile;
                string[] assemblies = profile.Footprint.FrameworkAssemblies
                    .Where(runtimeByName.ContainsKey)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                return new ProfilePlan
                {
                    Key = profile.Key,
                    Title = profile.Title,
                    Description = profile.Description,
                    RootAssemblies = profile.Roots,
                    Assemblies = assemblies,
                    Sources = assemblies.Select(name => sourceByName[name]).ToArray(),
                    IsAdvanced = item.advanced,
                };
            }).ToArray();
        }

        internal static void Start(IEnumerable<string> selectedKeys)
        {
            if (selectedKeys == null) throw new ArgumentNullException(nameof(selectedKeys));
            if (IsRunning) throw new InvalidOperationException("已有一轮真实构建体积探针正在运行。");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Unity 正在编译或刷新资源，请完成后再启动构建探针。");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先退出 Play Mode，再启动隔离构建探针。");
            if (BuildPipeline.isBuildingPlayer)
                throw new InvalidOperationException("当前已有 Player Build 正在运行。");

            var selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
            ProfilePlan[] plans = CreatePlans().Where(plan => selected.Contains(plan.Key)).ToArray();
            if (plans.Length == 0)
                throw new InvalidOperationException("至少选择一个构建组合。");

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + target;
            string runDirectory = FullPath(Path.Combine(RunsRoot, runId));
            string projectDirectory = Path.Combine(runDirectory, "Project");

            var report = new RunReport
            {
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                UnityVersion = Application.unityVersion,
                Target = target.ToString(),
                ScriptingBackend = PlayerSettings.GetScriptingBackend(namedTarget).ToString(),
                StrippingLevel = PlayerSettings.GetManagedStrippingLevel(namedTarget).ToString(),
                DevelopmentBuild = EditorUserBuildSettings.development,
                EvidenceScope =
                    "隔离空工程只包含所选 Framework Module；程序集完整保留，表示链接、AOT 与平台压缩后的体积上界。",
                RunDirectory = runDirectory,
                Profiles = plans.Select(plan => new ProfileRecord
                {
                    Key = plan.Key,
                    Title = plan.Title,
                    Status = "等待",
                    Message = "等待前序组合完成。",
                    Assemblies = plan.Assemblies,
                    Sources = plan.Sources,
                }).ToArray(),
            };

            PrepareWorkspace(projectDirectory);
            _activeRun = new ActiveRun
            {
                ProjectDirectory = projectDirectory,
                RunDirectory = runDirectory,
                Pending = new Queue<ProfilePlan>(plans),
                Report = report,
            };
            _stopAfterCurrent = false;
            EditorPrefs.SetString(LatestRunPreferencePrefix + HashProjectPath(), runDirectory);
            WriteReports(_activeRun.Report);
            EditorApplication.update += PollChildProcess;
            StartNextProfile();
        }

        internal static void RequestStopAfterCurrent()
        {
            if (!IsRunning) return;
            _stopAfterCurrent = true;
            foreach (var record in _activeRun.Report.Profiles.Where(record => record.Status == "等待"))
                record.Message = "当前组合结束后停止，不再启动新的 Unity 子进程。";
            WriteReports(_activeRun.Report);
            Changed?.Invoke();
        }

        internal static string CreateLinkXml(IEnumerable<string> assemblyNames)
        {
            if (assemblyNames == null) throw new ArgumentNullException(nameof(assemblyNames));
            var sb = new StringBuilder(512);
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<linker>");
            foreach (string name in assemblyNames
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                sb.Append("  <assembly fullname=\"")
                    .Append(SecurityElement.Escape(name))
                    .AppendLine("\" preserve=\"all\" />");
            }
            sb.AppendLine("</linker>");
            return sb.ToString();
        }

        internal static string CreateMinimalManifest(string sourceManifest, IEnumerable<string> assemblies)
        {
            if (sourceManifest == null) throw new ArgumentNullException(nameof(sourceManifest));
            if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));

            var assemblySet = new HashSet<string>(assemblies, StringComparer.Ordinal);
            var required = new HashSet<string>(AlwaysRequiredPackages, StringComparer.Ordinal);
            if (assemblySet.Contains(FrameworkModuleAudit.SharedUiAssemblyName))
                required.Add("com.unity.inputsystem");
            if (assemblySet.Contains(FrameworkModuleAudit.UGuiAssemblyName) ||
                assemblySet.Contains(FrameworkModuleAudit.BridgeAssemblyName) ||
                assemblySet.Contains("Game.Framework.Fonts"))
                required.Add("com.unity.ugui");
            if (assemblySet.Contains("Game.Framework.Asset.Yoo"))
                required.Add("com.tuyoogame.yooasset");

            var dependencies = ReadDependencyEntries(sourceManifest);
            foreach (string module in dependencies.Keys.Where(id => id.StartsWith("com.unity.modules.", StringComparison.Ordinal)))
                required.Add(module);

            string[] missing = required.Where(id => !dependencies.ContainsKey(id)).OrderBy(id => id).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("主工程 Packages/manifest.json 缺少探针所需依赖：" +
                                                    string.Join(", ", missing));

            var sb = new StringBuilder(4096);
            sb.AppendLine("{");
            sb.AppendLine("  \"dependencies\": {");
            string[] ordered = required.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                string suffix = i + 1 < ordered.Length ? "," : string.Empty;
                sb.Append("    \"").Append(ordered[i]).Append("\": \"")
                    .Append(dependencies[ordered[i]]).Append('"').AppendLine(suffix);
            }
            sb.AppendLine("  },");
            sb.AppendLine("  \"scopedRegistries\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"name\": \"package.openupm.com\",");
            sb.AppendLine("      \"url\": \"https://package.openupm.com\",");
            sb.AppendLine("      \"scopes\": [\"com.tuyoogame.yooasset\"]");
            sb.AppendLine("    }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        internal static bool ShouldSkipModulePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string normalized = relativePath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            return segments.Any(segment =>
                segment.Equals("Editor", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Tests", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Editor.meta", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Tests.meta", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Test.meta", StringComparison.OrdinalIgnoreCase));
        }

        private static void PrepareWorkspace(string projectDirectory)
        {
            if (Directory.Exists(projectDirectory))
                throw new IOException("隔离构建目录已经存在，拒绝覆盖：" + projectDirectory);

            Directory.CreateDirectory(Path.Combine(projectDirectory, "Assets", "Editor"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Packages"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "ProjectSettings"));

            CopyDirectory(
                FrameworkModuleSourceCatalog.Resolve("Packages/nuget-packages").PhysicalPath,
                Path.Combine(projectDirectory, "Packages", "nuget-packages"),
                _ => false);

            string templateSource = ResolveChildTemplate().PhysicalPath;
            if (!File.Exists(templateSource))
                throw new FileNotFoundException("找不到隔离构建子进程模板。", templateSource);
            File.Copy(templateSource,
                Path.Combine(projectDirectory, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs"));

            string projectVersion = FullPath("ProjectSettings/ProjectVersion.txt");
            File.Copy(projectVersion, Path.Combine(projectDirectory, "ProjectSettings", "ProjectVersion.txt"));
        }

        private static void StartNextProfile()
        {
            if (_activeRun == null) return;
            if (_stopAfterCurrent || _activeRun.Pending.Count == 0)
            {
                CompleteRun(_stopAfterCurrent ? "按请求在当前组合完成后停止。" : null);
                return;
            }

            _activeProfile = _activeRun.Pending.Dequeue();
            ProfileRecord record = FindRecord(_activeProfile.Key);
            record.Status = "准备";
            record.Message = "正在复制所选 Module 并准备隔离工程。";
            Changed?.Invoke();

            try
            {
                PrepareProfileSources(_activeRun.ProjectDirectory, _activeProfile);
                string outputPath = Path.Combine(_activeRun.RunDirectory, "Output", _activeProfile.Key);
                string resultPath = Path.Combine(_activeRun.RunDirectory, "Results", _activeProfile.Key + ".json");
                string logPath = Path.Combine(_activeRun.RunDirectory, "Logs", _activeProfile.Key + ".log");
                Directory.CreateDirectory(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath) ?? _activeRun.RunDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? _activeRun.RunDirectory);

                record.OutputPath = outputPath;
                record.ResultPath = resultPath;
                record.LogPath = logPath;
                record.Status = "构建中";
                record.Message = $"Unity 子进程正在构建 {_activeProfile.Title}；主工程可继续查看，但不要重复启动探针。";
                WriteReports(_activeRun.Report);
                Changed?.Invoke();

                _childProcess = StartUnityChild(_activeRun, _activeProfile, outputPath, resultPath, logPath);
                record.ChildProcessId = _childProcess.Id;
                WriteReports(_activeRun.Report);
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                record.Status = "失败";
                record.Message = ex.Message;
                record.Errors = Math.Max(record.Errors, 1);
                WriteReports(_activeRun.Report);
                Debug.LogException(ex);
                StartNextProfile();
            }
        }

        private static Process StartUnityChild(
            ActiveRun run,
            ProfilePlan profile,
            string outputPath,
            string resultPath,
            string logPath)
        {
            var arguments = new List<string>
            {
                "-batchmode",
                "-quit",
                "-nographics",
                "-accept-apiupdate",
                "-projectPath", run.ProjectDirectory,
                "-buildTarget", run.Report.Target,
                "-executeMethod", "SSFrameworkBuildProbeChild.Run",
                "-ssProbeProfile", profile.Key,
                "-ssProbeOutput", outputPath,
                "-ssProbeResult", resultPath,
                "-ssProbeBackend", run.Report.ScriptingBackend,
                "-ssProbeStripping", run.Report.StrippingLevel,
                "-ssProbeDevelopment", run.Report.DevelopmentBuild ? "true" : "false",
                "-logFile", logPath,
            };
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = EditorApplication.applicationPath,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = run.ProjectDirectory,
            };
            SanitizeChildEnvironment(startInfo);
            Process process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("无法启动隔离 Unity 子进程。");
            return process;
        }

        private static void PollChildProcess()
        {
            if (_activeRun == null)
            {
                EditorApplication.update -= PollChildProcess;
                return;
            }
            if (_childProcess == null || !_childProcess.HasExited) return;

            int exitCode = _childProcess.ExitCode;
            _childProcess.Dispose();
            _childProcess = null;
            ProfileRecord record = FindRecord(_activeProfile.Key);
            record.ExitCode = exitCode;
            record.ChildProcessId = 0;
            ApplyChildResult(record, exitCode);
            WriteReports(_activeRun.Report);
            Changed?.Invoke();
            StartNextProfile();
        }

        private static void ApplyChildResult(ProfileRecord record, int exitCode)
        {
            if (!string.IsNullOrWhiteSpace(record.ResultPath) && File.Exists(record.ResultPath))
            {
                try
                {
                    var child = JsonUtility.FromJson<ProfileRecord>(
                        File.ReadAllText(record.ResultPath, Encoding.UTF8));
                    record.Status = child.Status;
                    record.Message = child.Message;
                    record.BuildReportBytes = child.BuildReportBytes;
                    record.RawOutputBytes = child.RawOutputBytes > 0
                        ? child.RawOutputBytes
                        : child.OutputBytes;
                    record.OutputBytes = child.OutputBytes;
                    record.DurationSeconds = child.DurationSeconds;
                    record.Errors = child.Errors;
                    record.Warnings = child.Warnings;
                    record.LargestFiles = child.LargestFiles ?? Array.Empty<OutputFileRecord>();
                    if (!string.IsNullOrWhiteSpace(child.OutputPath)) record.OutputPath = child.OutputPath;
                    NormalizeShippingEvidence(record);
                    if (exitCode != 0 && record.Status == "成功")
                    {
                        record.Status = "失败";
                        record.Message = "子进程返回非零退出码：" + exitCode;
                    }
                    return;
                }
                catch (Exception ex)
                {
                    record.Message = "结果文件无法解析：" + ex.Message;
                }
            }

            record.Status = "失败";
            record.Errors = Math.Max(record.Errors, 1);
            string logExcerpt = ReadDiagnosticLogExcerpt(record.LogPath, 12);
            record.Message = string.IsNullOrWhiteSpace(logExcerpt)
                ? $"Unity 子进程退出码 {exitCode}，且没有生成结果文件。"
                : $"Unity 子进程退出码 {exitCode}。关键日志：\n{logExcerpt}";
        }

        private static void CompleteRun(string note)
        {
            if (_activeRun == null) return;
            foreach (var record in _activeRun.Report.Profiles.Where(record => record.Status == "等待"))
            {
                record.Status = "跳过";
                record.Message = note ?? "未运行。";
            }
            _activeRun.Report.CompletedUtc = DateTime.UtcNow.ToString("O");
            WriteReports(_activeRun.Report);
            _activeRun = null;
            _activeProfile = null;
            _stopAfterCurrent = false;
            EditorApplication.update -= PollChildProcess;
            Changed?.Invoke();
        }

        private static void RecoverInterruptedRun()
        {
            if (IsRunning) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleRecovery();
                return;
            }
            RunReport report = LoadLatestReport();
            if (report == null || !string.IsNullOrWhiteSpace(report.CompletedUtc)) return;

            try
            {
                var plans = CreatePlans().ToDictionary(plan => plan.Key, StringComparer.Ordinal);
                string sourceDrift = FindRecoveryDrift(report, plans);
                foreach (var record in report.Profiles.Where(record => record.Status == "准备"))
                {
                    record.Status = "等待";
                    record.Message = "主 Unity 重载后恢复到等待队列。";
                }
                var pending = new Queue<ProfilePlan>(report.Profiles
                    .Where(record => record.Status == "等待")
                    .Select(record => plans.TryGetValue(record.Key, out var plan) ? plan : null)
                    .Where(plan => plan != null));
                _activeRun = new ActiveRun
                {
                    ProjectDirectory = Path.Combine(report.RunDirectory, "Project"),
                    RunDirectory = report.RunDirectory,
                    Pending = pending,
                    Report = report,
                };
                _stopAfterCurrent = false;
                EditorApplication.update -= PollChildProcess;
                EditorApplication.update += PollChildProcess;

                ProfileRecord building = report.Profiles.FirstOrDefault(record => record.Status == "构建中");
                if (!string.IsNullOrEmpty(sourceDrift))
                {
                    if (building != null && TryAttachUnityProcess(building.ChildProcessId, out _childProcess))
                    {
                        _activeProfile = plans.TryGetValue(building.Key, out ProfilePlan currentPlan)
                            ? currentPlan
                            : new ProfilePlan { Key = building.Key, Title = building.Title };
                        _stopAfterCurrent = true;
                        building.Message = "检测到源码身份漂移；已重新附着当前子进程，完成后停止：" + sourceDrift;
                        foreach (ProfileRecord waiting in report.Profiles.Where(record => record.Status == "等待"))
                            waiting.Message = "检测到源码身份漂移；当前组合完成后跳过，避免混合两套来源。";
                        WriteReports(report);
                        Changed?.Invoke();
                        return;
                    }

                    if (building != null)
                    {
                        building.Status = "失败";
                        building.Errors = Math.Max(building.Errors, 1);
                        building.ChildProcessId = 0;
                        building.Message = sourceDrift;
                    }
                    else
                    {
                        ProfileRecord invalidPending = report.Profiles.FirstOrDefault(
                            record => record.Status == "等待");
                        if (invalidPending != null)
                        {
                            invalidPending.Status = "失败";
                            invalidPending.Errors = Math.Max(invalidPending.Errors, 1);
                            invalidPending.Message = sourceDrift;
                        }
                    }
                    _stopAfterCurrent = true;
                    Debug.LogError("[BuildSizeProbe] " + sourceDrift);
                    CompleteRun(sourceDrift);
                    return;
                }
                if (building == null)
                {
                    WriteReports(report);
                    StartNextProfile();
                    return;
                }
                if (!plans.TryGetValue(building.Key, out _activeProfile))
                    throw new InvalidOperationException("恢复时找不到构建组合：" + building.Key);

                if (TryAttachUnityProcess(building.ChildProcessId, out _childProcess))
                {
                    building.Message = "主 Unity 重载后已重新附着正在运行的隔离构建子进程。";
                    WriteReports(report);
                    Changed?.Invoke();
                    return;
                }

                if (File.Exists(building.ResultPath))
                {
                    building.ChildProcessId = 0;
                    ApplyChildResult(building, building.ExitCode);
                }
                else
                {
                    building.Status = "失败";
                    building.Errors = Math.Max(building.Errors, 1);
                    building.ChildProcessId = 0;
                    building.Message = "主 Unity 重载后没有找到原子进程或结果文件；未猜测成功，继续后续组合。";
                }
                WriteReports(report);
                StartNextProfile();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _activeRun = null;
                _activeProfile = null;
                _childProcess = null;
                EditorApplication.update -= PollChildProcess;
            }
        }

        private static bool TryAttachUnityProcess(int processId, out Process process)
        {
            process = null;
            if (processId <= 0) return false;
            try
            {
                Process candidate = Process.GetProcessById(processId);
                if (candidate.HasExited ||
                    candidate.ProcessName.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    candidate.Dispose();
                    return false;
                }
                process = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void PrepareProfileSources(string projectDirectory, ProfilePlan profile)
        {
            string childTemplate = ResolveChildTemplate().PhysicalPath;
            File.Copy(childTemplate,
                Path.Combine(projectDirectory, "Assets", "Editor", "FrameworkBuildSizeProbeChild.cs"), true);

            string sourceManifest = File.ReadAllText(FullPath("Packages/manifest.json"), Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(projectDirectory, "Packages", "manifest.json"),
                CreateMinimalManifest(sourceManifest, profile.Assemblies),
                new UTF8Encoding(false));

            string frameworkDestination = Path.Combine(projectDirectory, "Assets", "Framework");
            DeleteDirectoryInsideWorkspace(frameworkDestination, projectDirectory);
            Directory.CreateDirectory(frameworkDestination);

            foreach (ModuleSourcePlan module in profile.Sources)
            {
                if (module == null || string.IsNullOrWhiteSpace(module.PhysicalDirectory) ||
                    !Directory.Exists(module.PhysicalDirectory))
                    throw new DirectoryNotFoundException(
                        $"找不到 {module?.AssemblyName ?? "未知 Module"} 的物理源码目录：" +
                        (module?.PhysicalDirectory ?? "（空）"));
                string source = Path.GetFullPath(module.PhysicalDirectory);
                string destination = Path.Combine(
                    frameworkDestination,
                    SafeDirectoryName(module.AssemblyName));
                CopyDirectory(source, destination, ShouldSkipModulePath);
            }

            string rootDirectory = Path.Combine(projectDirectory, "Assets", "ProbeRoot");
            Directory.CreateDirectory(rootDirectory);
            File.WriteAllText(Path.Combine(rootDirectory, "link.xml"), CreateLinkXml(profile.Assemblies),
                new UTF8Encoding(false));
        }

        private static void WriteReports(RunReport report)
        {
            NormalizeShippingEvidence(report);
            Directory.CreateDirectory(report.RunDirectory);
            File.WriteAllText(Path.Combine(report.RunDirectory, "report.json"),
                JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(report.RunDirectory, "report.md"),
                CreateMarkdownReport(report), new UTF8Encoding(false));
        }

        internal static string CreateMarkdownReport(RunReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var sb = new StringBuilder(4096);
            sb.AppendLine("# SSFramework 真实构建体积证据");
            sb.AppendLine();
            sb.AppendLine($"- Unity：{report.UnityVersion}");
            sb.AppendLine($"- 目标：{report.Target} / {report.ScriptingBackend} / stripping {report.StrippingLevel}");
            sb.AppendLine($"- 证据口径：{report.EvidenceScope}");
            sb.AppendLine();
            sb.AppendLine("| 组合 | 状态 | 可发布输出 | BuildReport 总量 | 非发布构建证据 | 相对 Core | 用时 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            ProfileRecord core = report.Profiles.FirstOrDefault(record =>
                record.Key == "core" && record.Status == "成功");
            foreach (var record in report.Profiles)
            {
                string delta = core == null || record.Status != "成功"
                    ? "—"
                    : FormatSignedBytes(record.OutputBytes - core.OutputBytes);
                long nonShipping = Math.Max(0L, record.RawOutputBytes - record.OutputBytes);
                sb.AppendLine($"| {record.Title} | {record.Status} | {FormatBytes(record.OutputBytes)} | " +
                              $"{FormatBytes(record.BuildReportBytes)} | {FormatBytes(nonShipping)} | " +
                              $"{delta} | {record.DurationSeconds:F1}s |");
            }
            sb.AppendLine();
            sb.AppendLine("## 解释");
            sb.AppendLine();
            sb.AppendLine("- 每个组合在隔离空工程中构建，未选 Module 的源码、link.xml、业务场景和 HybridCLR 生成物都不在工程内。");
            sb.AppendLine("- 所选程序集完整保留，因此差值是可重复的体积上界；真实游戏只使用部分类型时通常会更小。");
            sb.AppendLine("- 可发布输出排除 Unity 明确标记为 BackUp/DoNotShip 的 IL2CPP 中间目录与调试符号；BuildReport 总量保留作诊断。 ");
            sb.AppendLine("- 不同 Unity、目标平台、脚本后端、裁剪级别或依赖版本之间不能直接横向比较。");
            foreach (var record in report.Profiles.Where(record => record.Status != "等待"))
            {
                sb.AppendLine();
                sb.AppendLine("## " + record.Title);
                sb.AppendLine();
                sb.AppendLine(record.Message ?? string.Empty);
                if (record.Assemblies?.Length > 0)
                    sb.AppendLine("\nModule：" + string.Join(", ", record.Assemblies));
                if (record.Sources?.Length > 0)
                {
                    sb.AppendLine("\n源码证据：");
                    foreach (ModuleSourcePlan source in record.Sources)
                    {
                        string owner = SourceOwner(source);
                        sb.AppendLine($"- {source.AssemblyName} ← {source.AssetDirectory} ({owner})");
                        if (!string.IsNullOrWhiteSpace(source.SourceFingerprint))
                            sb.AppendLine($"  - 实际复制内容 SHA-256：`{source.SourceFingerprint}`");
                    }
                }
                if (record.LargestFiles?.Length > 0)
                {
                    sb.AppendLine("\n较大的输出文件：");
                    foreach (var file in record.LargestFiles)
                        sb.AppendLine($"- {file.Path}：{FormatBytes(file.Bytes)} ({file.Role})");
                }
            }
            return sb.ToString();
        }

        internal static string FormatBytes(long bytes)
        {
            double value = Math.Abs((double)bytes);
            string sign = bytes < 0 ? "-" : string.Empty;
            if (value >= 1024d * 1024d * 1024d) return $"{sign}{value / (1024d * 1024d * 1024d):F2} GiB";
            if (value >= 1024d * 1024d) return $"{sign}{value / (1024d * 1024d):F2} MiB";
            if (value >= 1024d) return $"{sign}{value / 1024d:F1} KiB";
            return sign + value.ToString("F0") + " B";
        }

        private static string FormatSignedBytes(long bytes) =>
            bytes > 0 ? "+" + FormatBytes(bytes) : FormatBytes(bytes);

        internal static bool IsShippingOutputPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string normalized = relativePath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            if (segments.Any(segment =>
                    segment.IndexOf("BackUpThisFolder_ButDontShipItWithYourGame",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    segment.IndexOf("DoNotShip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    segment.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)))
                return false;
            return !normalized.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.EndsWith(".symbols.zip", StringComparison.OrdinalIgnoreCase);
        }

        private static void NormalizeShippingEvidence(RunReport report)
        {
            if (report?.Profiles == null) return;
            foreach (var record in report.Profiles) NormalizeShippingEvidence(record);
        }

        private static void NormalizeShippingEvidence(ProfileRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.OutputPath) ||
                !Directory.Exists(record.OutputPath)) return;
            string[] files = Directory.GetFiles(record.OutputPath, "*", SearchOption.AllDirectories);
            record.RawOutputBytes = files.Sum(path => new FileInfo(path).Length);
            var shipping = files.Select(path => new
                {
                    Path = path,
                    Relative = RelativePath(record.OutputPath, path),
                    Bytes = new FileInfo(path).Length,
                })
                .Where(file => IsShippingOutputPath(file.Relative))
                .ToArray();
            record.OutputBytes = shipping.Sum(file => file.Bytes);
            record.LargestFiles = shipping.OrderByDescending(file => file.Bytes)
                .Take(10)
                .Select(file => new OutputFileRecord
                {
                    Path = file.Relative,
                    Role = string.IsNullOrEmpty(Path.GetExtension(file.Path))
                        ? "data"
                        : Path.GetExtension(file.Path).TrimStart('.'),
                    Bytes = file.Bytes,
                }).ToArray();
        }

        private static Dictionary<string, string> ReadDependencyEntries(string manifest)
        {
            int dependencies = manifest.IndexOf("\"dependencies\"", StringComparison.Ordinal);
            if (dependencies < 0) throw new InvalidDataException("Packages/manifest.json 缺少 dependencies。");
            int openBrace = manifest.IndexOf('{', dependencies);
            int closeBrace = manifest.IndexOf('}', openBrace + 1);
            if (openBrace < 0 || closeBrace < 0)
                throw new InvalidDataException("Packages/manifest.json 的 dependencies 结构无法解析。");
            string block = manifest.Substring(openBrace + 1, closeBrace - openBrace - 1);
            return DependencyEntryRegex.Matches(block).Cast<Match>().ToDictionary(
                match => match.Groups["id"].Value,
                match => match.Groups["value"].Value,
                StringComparer.Ordinal);
        }

        private static void CopyDirectory(string source, string destination, Func<string, bool> skip)
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("找不到隔离探针依赖目录：" + source);
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = RelativePath(source, directory);
                if (skip(relative)) continue;
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = RelativePath(source, file);
                if (skip(relative)) continue;
                string destinationFile = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destination);
                File.Copy(file, destinationFile, true);
            }
        }

        private static string RelativePath(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            var rootUri = new Uri(normalizedRoot);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static void DeleteDirectoryInsideWorkspace(string directory, string workspace)
        {
            if (!Directory.Exists(directory)) return;
            string resolvedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            string resolvedWorkspace = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) +
                                       Path.DirectorySeparatorChar;
            if (!resolvedDirectory.StartsWith(resolvedWorkspace, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除隔离工作区之外的目录：" + resolvedDirectory);
            Directory.Delete(resolvedDirectory, true);
        }

        private static ProfileRecord FindRecord(string key) =>
            _activeRun.Report.Profiles.First(record => record.Key == key);

        internal static void SanitizeChildEnvironment(System.Diagnostics.ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));

            // HybridCLR 会在主 Unity 进程中写入进程级 UNITY_IL2CPP_PATH。Process.Start 默认继承该值，
            // 但隔离工程刻意不安装 HybridCLR；若不移除，连普通脚本编译也会错误访问主工程的本地
            // libil2cpp，最终只留下误导性的“executeMethod class could not be found”。
            startInfo.EnvironmentVariables.Remove(UnityIl2CppPathEnvironmentVariable);
        }

        internal static string ReadDiagnosticLogExcerpt(string path, int lines)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            try
            {
                string[] all = File.ReadAllLines(path);
                string[] signals = all.Where(IsDiagnosticLogLine).Take(lines).ToArray();
                return signals.Length > 0
                    ? string.Join("\n", signals)
                    : string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsDiagnosticLogLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            return line.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   Regex.IsMatch(line, @"\berror\s+CS\d+\b", RegexOptions.IgnoreCase) ||
                   line.IndexOf("Compilation failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("executeMethod class", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("BuildFailedException", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string QuoteArgument(string value) =>
            string.IsNullOrEmpty(value) ? "\"\"" : "\"" + value.Replace("\"", "\\\"") + "\"";

        private static string FullPath(string projectRelativePath) =>
            Path.GetFullPath(Path.Combine(ProjectRoot, projectRelativePath));

        private static FrameworkModuleSourceCatalog.SourceLocation ResolveChildTemplate() =>
            FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                ChildTemplateFileName, FrameworkModuleAudit.CoreAssemblyName + ".Editor");

        internal static void ValidateDisjointSourceDirectories(
            IEnumerable<ModuleSourcePlan> sourcePlans)
        {
            ModuleSourcePlan[] sources = sourcePlans?
                .Where(source => source != null && !string.IsNullOrWhiteSpace(source.PhysicalDirectory))
                .OrderBy(source => source.AssemblyName, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<ModuleSourcePlan>();
            for (int i = 0; i < sources.Length; i++)
            for (int j = i + 1; j < sources.Length; j++)
            {
                if (!FrameworkModuleSourceCatalog.IsPhysicalPathInside(
                        sources[i].PhysicalDirectory, sources[j].PhysicalDirectory) &&
                    !FrameworkModuleSourceCatalog.IsPhysicalPathInside(
                        sources[j].PhysicalDirectory, sources[i].PhysicalDirectory))
                    continue;
                throw new InvalidDataException(
                    "隔离构建要求每个 Runtime Module 拥有互不嵌套的源码目录；否则复制一个 Module 会夹带另一个：" +
                    $"{sources[i].AssemblyName} ({sources[i].AssetDirectory}) ↔ " +
                    $"{sources[j].AssemblyName} ({sources[j].AssetDirectory})");
            }
        }

        internal static string FindSourceIdentityMismatch(
            IEnumerable<ModuleSourcePlan> recorded,
            IEnumerable<ModuleSourcePlan> current)
        {
            string[] oldKeys = SourceIdentityKeys(recorded);
            string[] currentKeys = SourceIdentityKeys(current);
            return oldKeys.SequenceEqual(currentKeys, StringComparer.Ordinal)
                ? string.Empty
                : "Module 源码身份已变化；原报告为 [" + string.Join(", ", oldKeys) +
                  "]，当前为 [" + string.Join(", ", currentKeys) + "]。";
        }

        internal static string FindRecoveryDrift(
            RunReport report,
            IReadOnlyDictionary<string, ProfilePlan> currentPlans)
        {
            if (report == null) return "恢复报告为空，无法验证 Module 源码身份。";
            if (report.FormatVersion < 4)
                return "报告格式早于 v4，缺少实际复制内容的指纹，拒绝跨 Domain Reload 猜测续跑。";
            if (currentPlans == null) return "当前 Module 拓扑为空，无法验证恢复报告。";

            var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProfileRecord record in report.Profiles ?? Array.Empty<ProfileRecord>())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Key))
                    return "恢复报告包含没有稳定 Key 的构建组合，拒绝猜测续跑。";
                if (!recordedKeys.Add(record.Key))
                    return $"恢复报告重复记录构建组合 {record.Key}，拒绝猜测续跑。";
                if (!currentPlans.TryGetValue(record.Key, out ProfilePlan current))
                    return $"构建组合 {record.Key} 已不在当前 Module 拓扑中，拒绝静默跳过。";

                string mismatch = FindSourceIdentityMismatch(record.Sources, current.Sources);
                if (!string.IsNullOrEmpty(mismatch)) return $"构建组合 {record.Key}：{mismatch}";
            }

            return string.Empty;
        }

        private static string[] SourceIdentityKeys(IEnumerable<ModuleSourcePlan> sources) =>
            (sources ?? Array.Empty<ModuleSourcePlan>())
            .Where(source => source != null)
            .Select(source => string.Join("|",
                source.AssemblyName ?? string.Empty,
                NormalizeAssetPath(source.AssetDirectory),
                SourceOwner(source),
                source.SourceFingerprint ?? string.Empty))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        internal static string ComputeModuleSourceFingerprint(string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException("无法为不存在的 Module 源码目录生成内容指纹：" +
                                                     (sourceDirectory ?? "（空）"));

            string root = Path.GetFullPath(sourceDirectory);
            string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    PhysicalPath = path,
                    RelativePath = RelativePath(root, path).Replace('\\', '/'),
                })
                .Where(file => !ShouldSkipModulePath(file.RelativePath))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => file.PhysicalPath)
                .ToArray();

            using var sha256 = SHA256.Create();
            using (var hashingStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(hashingStream, new UTF8Encoding(false), true))
            {
                foreach (string file in files)
                {
                    string relativePath = RelativePath(root, file).Replace('\\', '/');
                    using FileStream input = File.OpenRead(file);
                    writer.Write(relativePath);
                    writer.Write(input.Length);
                    writer.Flush();
                    input.CopyTo(hashingStream);
                }
                writer.Flush();
                hashingStream.FlushFinalBlock();
            }

            return BitConverter.ToString(sha256.Hash ?? Array.Empty<byte>())
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private static string SourceOwner(ModuleSourcePlan source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.PackageName)) return "Assets";
            if (!string.IsNullOrWhiteSpace(source.PackageId)) return source.PackageId;
            return source.PackageName +
                   (string.IsNullOrWhiteSpace(source.PackageVersion)
                       ? string.Empty
                       : "@" + source.PackageVersion);
        }

        private static string SafeDirectoryName(string value)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? "Module" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                candidate = candidate.Replace(invalid, '_');
            return candidate;
        }

        private static string NormalizeAssetPath(string path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');

        private static string ProjectRoot =>
            Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();

        private static string HashProjectPath()
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in ProjectRoot.ToUpperInvariant()) hash = hash * 31 + c;
                return hash.ToString("X8");
            }
        }
    }
}
