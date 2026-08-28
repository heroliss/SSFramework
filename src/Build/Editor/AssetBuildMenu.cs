using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Sockets;
using Game.Framework.Editor;
using UnityEditor;
using YooAsset.Editor; // BundleBuilderHelper（内置首包根目录）
using Debug = UnityEngine.Debug;

namespace Game.Framework.Build
{
    /// <summary>
    /// 资源构建工作台的动作层——构建 / 部署 / 起服务 / 打开目录 / 配置，**步骤刻意拆开**（不捆绑）：
    /// <list type="number">
    ///   <item>构建资源包 —— 跑 SBP，产 YooAsset 原生输出（AssetBuild/Bundles）。</item>
    ///   <item>部署 —— 平铺最新产物到 AssetBuild/Deploy（本地 python 伺服 + CI 上传共用同一目录）。</item>
    ///   <item>启动本地 CDN 服务 —— python 起 HTTP（可限速模拟弱网）伺服 AssetBuild/Deploy。</item>
    /// </list>
    /// 目录名见 <see cref="AssetBuildLayout"/>；构建/部署逻辑全在 <see cref="FrameworkAssetBuilder"/>（本类只是交互外壳）；
    /// 「打哪些包 + 每包参数」完全读取 <see cref="FrameworkAssetBuildProfile"/>；动作层不猜测业务包名或目录。
    ///
    /// <para>为什么是编辑器工作台而非运行时按钮：AssetBundle 构建管线（SBP）不能在 Play 模式跑。
    /// 「本地起服务」是开发期联调专属，正式发版里这步换成 CI 上传到真实 CDN。</para>
    /// </summary>
    public static class AssetBuildMenu
    {
        // 「构建用资源依赖数据库（加速收集）」的持久开关：本机构建过程的提速旋钮，不进产物、不入库，
        // 所以放 EditorPrefs（每机器一份）而非构建 profile（profile 只放会随产物发布的内容配置）。
        // key 带工程路径限定，避免同机器多工程互相串台。
        private static string UseDependencyDBPrefKey => "SSFramework.AssetBuild.UseAssetDependencyDB." + UnityEngine.Application.dataPath;
        internal static bool UseDependencyDB
        {
            get => EditorPrefs.GetBool(UseDependencyDBPrefKey, false);
            set => EditorPrefs.SetBool(UseDependencyDBPrefKey, value);
        }

        // ───────────── 1/2/3：构建 → 部署 → 起服务（拆开） ─────────────

        internal static void Build()
        {
            if (!TryPrepareBuild("资源包构建", out var profile, out var packages)) return;
            RunBuild(profile, packages, clearBuildCache: false);
        }

        internal static void FullRebuild()
        {
            if (!TryPrepareBuild("资源包全量重建", out var profile, out var packages)) return;
            if (!EditorUtility.DisplayDialog("全量重建",
                    "将清掉 SBP 增量构建缓存后【全量】重建所有启用的包——比平时慢得多，仅在怀疑增量缓存损坏 / 产物异常时用。继续？",
                    "全量重建", "取消"))
            {
                FrameworkEditorFeedback.Info("资源包全量重建已取消", "没有清理缓存，也没有启动构建。");
                return;
            }
            RunBuild(profile, packages, clearBuildCache: true);
        }

        private static bool TryPrepareBuild(
            string operation,
            out FrameworkAssetBuildProfile profile,
            out System.Collections.Generic.List<string> packages)
        {
            packages = null;
            if (!TryGetProfile(operation, out profile)) return false;
            if (!FrameworkEditorOperationGate.EnsureCanStart(operation)) return false;
            return TryGetEnabledPackages(operation, profile, out packages);
        }

        // 构建实操（两个构建入口共用）：动作前已经预检一次，真正触碰 SBP 前仍二次检查并处理脏场景竞态。
        private static void RunBuild(
            FrameworkAssetBuildProfile profile,
            System.Collections.Generic.IReadOnlyList<string> packages,
            bool clearBuildCache)
        {
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            string version = profile.ResolveVersionNow();

            var (ok, message) = FrameworkAssetBuilder.Build(
                profile, packages, version, clearBuildCache, UseDependencyDB);
            FrameworkEditorFeedback.ReportResult("资源包构建", ok, message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.BundlesRoot);
        }

        internal static void Deploy()
        {
            if (!TryGetProfile("资源包部署", out var profile)) return;
            if (!FrameworkEditorOperationGate.EnsureCanStart("资源包部署", requireEditMode: false)) return;
            if (!TryGetEnabledPackages("资源包部署", profile, out var packages)) return;
            string deployDir = AssetBuildLayout.DeployRoot;

            var (ok, message) = FrameworkAssetBuilder.Deploy(packages, deployDir);
            FrameworkEditorFeedback.ReportResult("资源包部署", ok, message);
            if (ok) EditorUtility.RevealInFinder(deployDir);
        }

        private static bool TryGetEnabledPackages(
            string operation,
            FrameworkAssetBuildProfile profile,
            out System.Collections.Generic.List<string> packages)
        {
            packages = profile?.EnabledPackageNames.ToList() ?? new System.Collections.Generic.List<string>();
            if (packages.Count > 0) return true;
            FrameworkEditorFeedback.Warn(
                operation + "未启动",
                "影响：没有构建、部署或清理任何资源产物。\n" +
                "原因：资源构建配置中没有启用的普通 AssetBundle 包。\n" +
                "下一步：定位资源构建配置，至少开启一个包的“参与构建”，或先同步 Collector 包列表。");
            return false;
        }

        internal static void StartLocalServer()
        {
            if (!TryGetProfile("启动本地 CDN 服务", out var profile)) return;
            if (!FrameworkEditorOperationGate.EnsureCanStart("启动本地 CDN 服务", requireEditMode: false)) return;
            string msg = StartServer(profile);
            FrameworkEditorFeedback.ReportSummary("启动本地 CDN 服务", msg);
        }

        // ───────────── 构建配置 ─────────────

        internal static void SelectProfile()
        {
            if (!FrameworkAssetBuildProfile.TryResolve(out _) &&
                !FrameworkEditorOperationGate.EnsureCanStart("创建资源构建配置")) return;
            var profile = FrameworkAssetBuildProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        internal static void SyncProfile()
        {
            if (!TryGetProfile("同步资源包列表", out var profile)) return;
            if (!FrameworkEditorOperationGate.EnsureCanStart("同步资源包列表")) return;
            string summary = profile.SyncFromCollector();
            FrameworkEditorFeedback.ReportSummary("同步资源包列表", summary);
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        internal static void GeneratePackageConstants()
        {
            if (!TryGetProfile("生成资源包名常量", out var profile)) return;
            if (!FrameworkEditorOperationGate.EnsureCanStart("生成资源包名常量")) return;
            var (ok, message) = AssetPackageConstantsGenerator.Generate(profile);
            FrameworkEditorFeedback.ReportResult("生成资源包名常量", ok, message);
            if (ok)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(profile.PackageConstantsPath);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            }
        }

        // 勾选式开关：构建时是否用「资源依赖缓存数据库」加速收集阶段（YooAsset UseAssetDependencyDB）。
        // 本机持久（EditorPrefs），影响上面两个构建入口；勾上提速、产物不变。CI 上用 -useAssetDependencyDB 单独控制（EditorPrefs 不随仓库走）。
        internal static void SetUseDependencyDB(bool value) => UseDependencyDB = value;

        // ───────────── 打开目录（菜单名只写用途，不写死文件夹名） ─────────────
        // Unity 的 [MenuItem] 名是编译期常量、跟不了配置；真实路径运行时由 AssetBuildLayout 解析、在 Reveal 里 log，点开直接看到。

        internal static void OpenBuildOutput() => Reveal(AssetBuildLayout.BundlesRoot,
            "尚无构建输出。先在工作台执行“构建资源包”。");

        internal static void OpenDeploy() => Reveal(AssetBuildLayout.DeployRoot,
            "尚无部署目录。先构建资源包，再执行“部署到本地目录”。");

        internal static void OpenDownloaded() => Reveal(AssetBuildLayout.DownloadedRoot,
            "尚无下载缓存（Host 模式下载资源后才会生成；真机默认位于 persistentDataPath）。");

        internal static void OpenBuiltin() => Reveal(BundleBuilderHelper.GetStreamingAssetsRoot(),
            "尚无内置首包目录。构建启用了首包拷贝的资源包后才会生成。");

        // ───────────── 本地服务（联调专属，生产=CI 上传 CDN）─────────────

        /// <summary>
        /// 在 <c>AssetBuild/Deploy</c> 起 HTTP 服务（端口取自 profile）。<c>LocalServeThrottleKBps</c>&gt;0 时用限速脚本模拟弱网，
        /// 否则用 <c>python -m http.server</c>。端口已监听则复用现有进程（改限速要先关掉它）。Play 模式安全（不涉及构建管线）。
        /// ⚠ 端口须与场景 AssetSystemConfigModel.CdnUrls 第一条（主）一致，Host 才下得到。
        /// </summary>
        public static string StartServer(FrameworkAssetBuildProfile profile)
        {
            if (profile == null)
                return "✗ 缺少资源构建 Profile；请先在资源构建工作台明确创建配置。";
            try
            {
                string deployDir = AssetBuildLayout.DeployRoot;
                int port = profile.LocalServePort;
                int kbps = profile.LocalServeThrottleKBps;

                if (!Directory.Exists(deployDir))
                    return $"⚠ ③ 部署目录不存在：{deployDir}（先执行「1. 构建资源包」+「2. 部署」）。";
                if (IsPortOpen(port))
                    return $"✓ ③ 本地服务已在 127.0.0.1:{port} 运行（复用现有进程，目录 {deployDir}）。改限速请先关掉它再启动。";

                // http.server 不支持限速：限速>0 时跑一个分块+sleep 的小脚本，否则用标准 http.server。
                string args = kbps > 0 ? $"\"{WriteThrottleScript()}\" {port} {kbps}" : $"-m http.server {port}";
                string mode = kbps > 0 ? $"限速 {kbps} KB/s/连接" : "不限速";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = args,
                    WorkingDirectory = deployDir,
                    UseShellExecute = true, // 开独立控制台常驻；false 进程会随 Editor 退出
                });
                return $"✓ ③ 已启动本地 CDN 服务（{mode}，端口 {port}，目录 {deployDir}）。";
            }
            catch (Exception e)
            {
                return $"✗ ③ 起服务失败（{e.Message}）。确认 python 在 PATH，或手动在部署目录起 HTTP 服务。";
            }
        }

        // 限速 HTTP 服务脚本（http.server 不支持限速）：分块发送 + 按 KB/s sleep，写到临时目录后 python &lt;脚本&gt; &lt;port&gt; &lt;kbps&gt;。
        // 限速是每连接的（总带宽≈值×并发）；ThreadingHTTPServer 支撑 YooAsset 的并发下载。脚本内容用单引号、避免和 C# verbatim 字符串冲突。
        private static string WriteThrottleScript()
        {
            string path = Path.Combine(Path.GetTempPath(), "ss_throttled_http_server.py");
            const string py =
@"import sys, time
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
port = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
kbps = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
CHUNK = 16384
class H(SimpleHTTPRequestHandler):
    def copyfile(self, source, outputfile):
        if kbps <= 0:
            super().copyfile(source, outputfile)
            return
        rate = kbps * 1024.0
        while True:
            buf = source.read(CHUNK)
            if not buf:
                break
            outputfile.write(buf)
            time.sleep(len(buf) / rate)
if __name__ == '__main__':
    print('throttled http server on 127.0.0.1:%d @ %s KB/s' % (port, kbps))
    ThreadingHTTPServer(('127.0.0.1', port), H).serve_forever()
";
            File.WriteAllText(path, py);
            return path;
        }

        // ───────────── 内部工具 ─────────────

        // 打开派生产物目录只做只读探测；查看动作不应暗中创建空目录。
        private static void Reveal(string dir, string missingHint)
        {
            if (!Directory.Exists(dir))
            {
                FrameworkEditorFeedback.Info("目录尚不存在", $"{missingHint}\n解析路径：{dir}");
                return;
            }
            Debug.Log("[资源构建] 打开目录：" + dir);
            EditorUtility.RevealInFinder(dir);
        }

        // 300ms 超时的 TCP 探测，判断本地 HTTP 服务是否已在监听。
        private static bool IsPortOpen(int port)
        {
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect("127.0.0.1", port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
                if (ok) client.EndConnect(ar);
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetProfile(string operation, out FrameworkAssetBuildProfile profile)
        {
            if (FrameworkAssetBuildProfile.TryResolve(out profile)) return true;
            FrameworkEditorFeedback.Warn(
                operation + "未启动",
                "影响：没有创建配置，也没有执行操作。\n原因：工程里还没有资源构建 Profile。\n" +
                $"下一步：打开“{FrameworkMenuPaths.AssetBuild}”，点击“创建默认构建配置”并复核后重试。");
            return false;
        }
    }
}
