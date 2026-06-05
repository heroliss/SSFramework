using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor; // BundleBuilderHelper（构建输出 / 内置首包根目录）
using Debug = UnityEngine.Debug;

namespace Game.Framework.Build
{
    /// <summary>
    /// 统一资源构建菜单 <c>SSFramework/资源构建/*</c>——把「构建 / 部署 / 起服务 / 打开目录 / 配置」收到一棵菜单，
    /// 三个核心步骤**刻意拆开**（不再有「一键构建+部署+起服务」的捆绑黑盒）：
    /// <list type="number">
    ///   <item>构建资源包 —— 跑 SBP，产 YooAsset 原生输出。</item>
    ///   <item>部署到本地 CDN —— 把最新构建产物平铺到 项目根/CDN（仅本机联调）。</item>
    ///   <item>启动本地 CDN 服务 —— python 起 HTTP 服务伺服 项目根/CDN。</item>
    /// </list>
    /// 构建/部署逻辑全在 <see cref="FrameworkAssetBuilder"/>（菜单只是它的交互外壳）；「打哪些包 + 每包参数」读
    /// <see cref="FrameworkAssetBuildProfile"/>。<b>demo 不在菜单里</b>——demo 的本地联调就是「把 profile 喂成样例包，走这同一套流程」。
    ///
    /// <para>为什么是编辑器菜单而非运行时按钮：AssetBundle 构建管线（SBP）不能在 Play 模式跑。
    /// 「本地起服务」是开发期联调专属，正式发版里这步换成 CI 上传到真实 CDN。</para>
    /// </summary>
    public static class AssetBuildMenu
    {
        private const string Root = "SSFramework/资源构建/";

        // YooAsset 编辑器期把下载缓存放在「项目根/<YooFolderName>」（默认 yoo），方便调试查看。
        // 内置首包根目录用 BundleBuilderHelper 的公开 API；缓存根目录无公开 API，按默认约定拼。
        private const string YooFolderName = "yoo";

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
            if (ok) EditorUtility.RevealInFinder(BundleBuilderHelper.GetDefaultBuildOutputRoot());
            EditorUtility.DisplayDialog(ok ? "构建完成" : "构建失败", message, "好");
        }

        [MenuItem(Root + "2. 部署到本地 CDN（联调）", priority = 2)]
        private static void Menu_DeployLocal()
        {
            var profile = FrameworkAssetBuildProfile.Resolve();
            var packages = profile.EnabledPackageNames.ToList();
            string cdnDir = LocalCdnDir(profile);

            var (ok, message) = FrameworkAssetBuilder.Deploy(packages, cdnDir);
            Debug.Log("[资源构建] 部署到本地 CDN：\n" + message);
            if (ok) EditorUtility.RevealInFinder(cdnDir);
            EditorUtility.DisplayDialog(ok ? "部署完成" : "部署失败", message, "好");
        }

        [MenuItem(Root + "3. 启动本地 CDN 服务", priority = 3)]
        private static void Menu_StartServer()
        {
            string msg = StartServer(FrameworkAssetBuildProfile.Resolve());
            Debug.Log("[资源构建] " + msg);
        }

        [MenuItem(Root + "构建全部并整理生产产物（CI 同款）", priority = 4)]
        private static void Menu_BuildProduction()
        {
            if (!FrameworkAssetBuilder.EnsureReadyToBuild()) return;

            var profile = FrameworkAssetBuildProfile.Resolve();
            var packages = profile.EnabledPackageNames.ToList();
            string version = DateTime.Now.ToString(profile.VersionFormat);

            var (ok, message) = FrameworkAssetBuilder.Build(profile, packages, version);
            string outDir = ProjectPath(profile.ProductionOutputDir);
            if (ok)
            {
                var (deployOk, deployMsg) = FrameworkAssetBuilder.Deploy(packages, outDir);
                ok &= deployOk;
                message += "\n" + deployMsg;
            }
            Debug.Log("[资源构建] 生产构建：\n" + message);
            if (ok) EditorUtility.RevealInFinder(outDir);
            EditorUtility.DisplayDialog(ok ? "生产构建完成（待 CI 上传）" : "生产构建失败", message, "好");
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

        // ───────────── 打开目录（中文 + 分类） ─────────────

        [MenuItem(Root + "打开目录/构建输出 (Bundles)", priority = 40)]
        private static void Menu_OpenBuildOutput()
            => Reveal(BundleBuilderHelper.GetDefaultBuildOutputRoot(), createIfMissing: true);

        [MenuItem(Root + "打开目录/内置首包 (StreamingAssets·yoo)", priority = 41)]
        private static void Menu_OpenBuiltin()
            => Reveal(BundleBuilderHelper.GetStreamingAssetsRoot(), createIfMissing: true);

        [MenuItem(Root + "打开目录/下载缓存 (项目根·yoo)", priority = 42)]
        private static void Menu_OpenCache()
            => Reveal(ProjectPath(YooFolderName), createIfMissing: false,
                      missingHint: "尚无下载缓存（Host/Web 模式下载资源后才会生成）。");

        // 下面两个目录名可配（profile.LocalCdnDirName / ProductionOutputDir）。Unity 的 [MenuItem] 名是编译期常量、
        // 没法跟着配置动态显示，所以菜单只写「用途」、不写死具体文件夹名；实际路径由 handler 运行时读 profile 解析，
        // 并在 Reveal 里 log 出来（点开就能在资源管理器看到真实目录）。
        [MenuItem(Root + "打开目录/本地 CDN 部署目录", priority = 43)]
        private static void Menu_OpenLocalCdn()
            => Reveal(LocalCdnDir(FrameworkAssetBuildProfile.Resolve()), createIfMissing: true);

        [MenuItem(Root + "打开目录/生产产物目录（待 CI 上传）", priority = 44)]
        private static void Menu_OpenProductionOutput()
            => Reveal(ProjectPath(FrameworkAssetBuildProfile.Resolve().ProductionOutputDir), createIfMissing: true);

        // ───────────── 本地服务（联调专属，生产=CI 上传 CDN） ─────────────

        /// <summary>
        /// 在本地 CDN 目录起 <c>python -m http.server &lt;port&gt;</c>（端口取自 profile）。端口已监听则复用现有进程。
        /// Play 模式安全（不涉及构建管线）。⚠ 端口须与场景 AssetSystemConfigModel.MainCdnUrl 一致，Host 才下得到。
        /// </summary>
        public static string StartServer(FrameworkAssetBuildProfile profile)
        {
            try
            {
                string cdnDir = LocalCdnDir(profile);
                int port = profile.LocalServePort;

                if (!Directory.Exists(cdnDir))
                    return $"③ 本地 CDN 目录不存在：{cdnDir}（先执行「构建资源包」+「部署到本地 CDN」）。";
                if (IsPortOpen(port))
                    return $"③ 本地服务已在 127.0.0.1:{port} 运行（复用现有进程，目录 {cdnDir}）。";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"-m http.server {port}",
                    WorkingDirectory = cdnDir,
                    UseShellExecute = true, // 开独立控制台常驻；false 进程会随 Editor 退出
                });
                return $"③ 已启动本地 CDN 服务（python -m http.server {port}，目录 {cdnDir}）。";
            }
            catch (Exception e)
            {
                return $"③ 起服务失败（{e.Message}）。确认 python 在 PATH，或手动在本地 CDN 目录起 HTTP 服务。";
            }
        }

        // ───────────── 内部工具 ─────────────

        // 项目根 = <项目根>/Assets 的父目录。零绝对路径、可移植。
        private static string ProjectRoot() => Path.GetDirectoryName(Application.dataPath);

        // 项目根下的相对路径（profile 里的目录都相对项目根）。
        private static string ProjectPath(string relative) => Path.Combine(ProjectRoot(), relative);

        private static string LocalCdnDir(FrameworkAssetBuildProfile profile) => ProjectPath(profile.LocalCdnDirName);

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
