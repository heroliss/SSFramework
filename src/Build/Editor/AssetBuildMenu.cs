using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Sockets;
using UnityEditor;
using YooAsset.Editor; // BundleBuilderHelper（内置首包根目录）
using Debug = UnityEngine.Debug;

namespace Game.Framework.Build
{
    /// <summary>
    /// 统一资源构建菜单 <c>SSFramework/资源构建/*</c>——构建 / 部署 / 起服务 / 打开目录 / 配置，**步骤刻意拆开**（不捆绑）：
    /// <list type="number">
    ///   <item>构建资源包 —— 跑 SBP，产 YooAsset 原生输出（AssetBuild/Bundles）。</item>
    ///   <item>部署 —— 平铺最新产物到 AssetBuild/Deploy（本地 python 伺服 + CI 上传共用同一目录）。</item>
    ///   <item>启动本地 CDN 服务 —— python 起 HTTP（可限速模拟弱网）伺服 AssetBuild/Deploy。</item>
    /// </list>
    /// 目录名见 <see cref="AssetBuildLayout"/>；构建/部署逻辑全在 <see cref="FrameworkAssetBuilder"/>（菜单只是交互外壳）；
    /// 「打哪些包 + 每包参数」读 <see cref="FrameworkAssetBuildProfile"/>。<b>demo 不在菜单里</b>——demo 的本地联调就是「把 profile 喂成样例包，走这同一套流程」。
    ///
    /// <para>为什么是编辑器菜单而非运行时按钮：AssetBundle 构建管线（SBP）不能在 Play 模式跑。
    /// 「本地起服务」是开发期联调专属，正式发版里这步换成 CI 上传到真实 CDN。</para>
    /// </summary>
    public static class AssetBuildMenu
    {
        private const string Root = "SSFramework/资源构建/";

        // ───────────── 1/2/3：构建 → 部署 → 起服务（拆开） ─────────────

        [MenuItem(Root + "1. 构建资源包", priority = 1)]
        private static void Menu_Build()
        {
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var profile = FrameworkAssetBuildProfile.Resolve();
            var packages = profile.EnabledPackageNames.ToList();
            string version = DateTime.Now.ToString(profile.VersionFormat);

            var (ok, message) = FrameworkAssetBuilder.Build(profile, packages, version);
            Debug.Log("[资源构建] 构建：\n" + message);
            if (ok) EditorUtility.RevealInFinder(AssetBuildLayout.BundlesRoot);
            EditorUtility.DisplayDialog(ok ? "构建完成" : "构建有失败项", message, "好");
        }

        [MenuItem(Root + "2. 部署（平铺到 Deploy）", priority = 2)]
        private static void Menu_Deploy()
        {
            var profile = FrameworkAssetBuildProfile.Resolve();
            var packages = profile.EnabledPackageNames.ToList();
            string deployDir = AssetBuildLayout.DeployRoot;

            var (ok, message) = FrameworkAssetBuilder.Deploy(packages, deployDir);
            Debug.Log("[资源构建] 部署：\n" + message);
            if (ok) EditorUtility.RevealInFinder(deployDir);
            EditorUtility.DisplayDialog(ok ? "部署完成（本地伺服 / CI 上传共用）" : "部署失败", message, "好");
        }

        [MenuItem(Root + "3. 启动本地 CDN 服务", priority = 3)]
        private static void Menu_StartServer()
        {
            string msg = StartServer(FrameworkAssetBuildProfile.Resolve());
            Debug.Log("[资源构建] " + msg);
        }

        // ───────────── 构建配置 ─────────────

        [MenuItem(Root + "构建配置 (Build Profile)", priority = 20)]
        private static void Menu_SelectProfile()
        {
            var profile = FrameworkAssetBuildProfile.Resolve();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
        }

        [MenuItem(Root + "同步收集器包列表", priority = 21)]
        private static void Menu_SyncProfile()
        {
            var profile = FrameworkAssetBuildProfile.Resolve();
            string summary = profile.SyncFromCollector();
            Debug.Log("[资源构建] 同步收集器包列表：\n" + summary);
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            EditorUtility.DisplayDialog("同步完成", summary, "好");
        }

        // ───────────── 打开目录（菜单名只写用途，不写死文件夹名） ─────────────
        // Unity 的 [MenuItem] 名是编译期常量、跟不了配置；真实路径运行时由 AssetBuildLayout 解析、在 Reveal 里 log，点开直接看到。

        [MenuItem(Root + "打开目录/构建输出", priority = 40)]
        private static void Menu_OpenBuildOutput() => Reveal(AssetBuildLayout.BundlesRoot, createIfMissing: true);

        [MenuItem(Root + "打开目录/部署", priority = 41)]
        private static void Menu_OpenDeploy() => Reveal(AssetBuildLayout.DeployRoot, createIfMissing: true);

        [MenuItem(Root + "打开目录/下载缓存", priority = 42)]
        private static void Menu_OpenDownloaded()
            => Reveal(AssetBuildLayout.DownloadedRoot, createIfMissing: false,
                      missingHint: "尚无下载缓存（Host 模式下载资源后才会生成；仅 YooAsset 重定向到此，真机在 persistentDataPath）。");

        [MenuItem(Root + "打开目录/内置首包", priority = 43)]
        private static void Menu_OpenBuiltin() => Reveal(BundleBuilderHelper.GetStreamingAssetsRoot(), createIfMissing: true);

        // ───────────── 本地服务（联调专属，生产=CI 上传 CDN）─────────────

        /// <summary>
        /// 在 <c>AssetBuild/Deploy</c> 起 HTTP 服务（端口取自 profile）。<c>LocalServeThrottleKBps</c>&gt;0 时用限速脚本模拟弱网，
        /// 否则用 <c>python -m http.server</c>。端口已监听则复用现有进程（改限速要先关掉它）。Play 模式安全（不涉及构建管线）。
        /// ⚠ 端口须与场景 AssetSystemConfigModel.MainCdnUrl 一致，Host 才下得到。
        /// </summary>
        public static string StartServer(FrameworkAssetBuildProfile profile)
        {
            try
            {
                string deployDir = AssetBuildLayout.DeployRoot;
                int port = profile.LocalServePort;
                int kbps = profile.LocalServeThrottleKBps;

                if (!Directory.Exists(deployDir))
                    return $"③ 部署目录不存在：{deployDir}（先执行「1. 构建资源包」+「2. 部署」）。";
                if (IsPortOpen(port))
                    return $"③ 本地服务已在 127.0.0.1:{port} 运行（复用现有进程，目录 {deployDir}）。改限速请先关掉它再启动。";

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
                return $"③ 已启动本地 CDN 服务（{mode}，端口 {port}，目录 {deployDir}）。";
            }
            catch (Exception e)
            {
                return $"③ 起服务失败（{e.Message}）。确认 python 在 PATH，或手动在部署目录起 HTTP 服务。";
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

        // 打开目录；createIfMissing=false 且目录不存在时只提示、不建空目录（如运行时才写的下载缓存）。
        private static void Reveal(string dir, bool createIfMissing, string missingHint = null)
        {
            if (!Directory.Exists(dir))
            {
                if (!createIfMissing)
                {
                    Debug.Log("[资源构建] " + (missingHint ?? $"目录不存在：{dir}"));
                    return;
                }
                Directory.CreateDirectory(dir);
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
    }
}
