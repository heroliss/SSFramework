using System;
using System.IO;
using System.Diagnostics;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;        // Application.dataPath / Object.FindFirstObjectByType
using YooAsset;           // EBundleType / EFileNameStyle（运行时 YooAsset 程序集，命名空间 YooAsset）
using YooAsset.Editor;    // 构建管线：ScriptableBuildParameters / EBuildPipeline / ECompressOption / BundleBuilderHelper
using Debug = UnityEngine.Debug;

namespace Game.Framework.Demo.Editor
{
    /// <summary>
    /// 资源加载章配套的「本地 CDN」编辑器工具，以 <c>Tools/Framework Demo/CDN/*</c> 菜单项暴露。
    ///
    /// 为什么是编辑器菜单、而不是 demo 运行时按钮：
    /// AssetBundle 构建管线（YooAsset SBP → Unity ScriptableBuildPipeline）**不能在 Play 模式运行**
    /// （会抛 <c>This cannot be used during play mode</c>）。demo 跑在 Play 模式，所以构建必须移到 Edit 模式的菜单里。
    /// 顺手把「构建+部署」与「起服务」拆成独立步骤（外加一个一键），职责清晰、失败也好定位。
    ///
    /// 可配置 / 可移植（面向框架以后抽成 unitypackage 给不同项目复用）：
    /// - 部署目录默认在「项目根/CDN」（<see cref="ResolveDeployDir"/> 从 <c>Application.dataPath</c> 推导，无绝对路径）；
    /// - 端口从场景里 <c>AssetSystemConfigModel</c> 的 CDN URL 解析（<see cref="ResolveServerPort"/>）——唯一配置源：
    ///   每个项目在 Inspector 配自己的 URL，工具跟着走，不用改源码；
    /// - 不依赖任何外部 .bat，直接调用 python（需在 PATH 上）。
    ///
    /// 用法：① 退出 Play →「构建并部署样例包」/「一键」→ ② AssetSystemConfigModel 切 PlayMode=Host → 重进 Play 看真实下载。
    /// </summary>
    public static class DemoCdnBuilder
    {
        /// <summary>构建的样例包名，与场景里 AssetSystemConfigModel 的默认包对齐。</summary>
        public const string PackageName = "FrameworkSamplesPackage";

        /// <summary>部署目录在项目根下的文件夹名（与 YooAsset 的 Bundles/ 同级，已加进 .gitignore）。</summary>
        private const string CdnFolderName = "CDN";

        /// <summary>解析不到 AssetSystemConfigModel 时的兜底端口（与 python -m http.server 默认习惯一致）。</summary>
        private const int DefaultPort = 8080;

        private const string MenuRoot = "SSFramework/CDN/";

        // ───────────── 菜单项 ─────────────

        [MenuItem(MenuRoot + "构建并部署样例包", priority = 1)]
        public static void Menu_BuildAndDeploy()
        {
            if (!EnsureEditMode()) return;
            Report("构建并部署", BuildAndDeploy());
        }

        [MenuItem(MenuRoot + "启动本地服务器", priority = 2)]
        public static void Menu_StartServer()
        {
            Debug.Log("[CDN] " + StartServer());
        }

        [MenuItem(MenuRoot + "一键：构建 + 部署 + 起服务", priority = 3)]
        public static void Menu_BuildDeployServe()
        {
            if (!EnsureEditMode()) return;
            var build = BuildAndDeploy();
            string msg = build.StartsWith("① 构建失败") ? build : build + "\n" + StartServer();
            Report("一键：构建 + 部署 + 起服务", msg);
        }

        [MenuItem(MenuRoot + "打开部署目录", priority = 20)]
        public static void Menu_OpenDeployDir()
        {
            var dir = ResolveDeployDir();
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        // ───────────── 步骤实现（也可被其它编辑器代码直接调用）─────────────

        /// <summary>
        /// 构建样例包并把产物部署到本地 CDN 的同名包子目录（<c>{CDN}/{包名}/</c>）。返回多行结果文案（含失败原因，绝不抛）。
        /// ⚠ 仅 Edit 模式：SBP 构建管线不能在 Play 模式运行。
        /// </summary>
        public static string BuildAndDeploy()
        {
            try
            {
                string deployDir = ResolveDeployDir();
                string version = DateTime.Now.ToString("yyyyMMddHHmmss");

                var buildResult = BuildPackage(version);
                if (!buildResult.Success)
                    return $"① 构建失败：[{buildResult.FailedTask}] {buildResult.ErrorInfo}";

                // GameRemoteService 的下载 URL 是 {CDN}/{包名}/{fileName}，所以产物部署到 CDN 下的同名包子目录。
                string packageDir = DeployToPackageDir(buildResult.OutputPackageDirectory, deployDir, out int copied);
                return $"① 构建成功：{PackageName} v{version}（平台 {EditorUserBuildSettings.activeBuildTarget}）\n"
                     + $"② 已部署 {copied} 个文件 → {packageDir}\n"
                     + "   注意：AssetBundle 按平台区分，编辑器内 Host 加载需平台与编辑器一致（Android 等请先切 Windows 或真机验证）。";
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return $"① 构建/部署异常：{e.Message}（详见 Console）";
            }
        }

        /// <summary>
        /// 在部署目录起 <c>python -m http.server &lt;port&gt;</c>（端口取自场景 AssetSystemConfigModel 的 CDN URL）。
        /// 端口已监听则复用现有进程。Play 模式安全（不涉及构建管线）。
        /// </summary>
        public static string StartServer()
        {
            try
            {
                string deployDir = ResolveDeployDir();
                int port = ResolveServerPort();

                if (!Directory.Exists(deployDir))
                    return $"③ 部署目录不存在：{deployDir}（先执行「构建并部署样例包」）。";
                if (IsPortOpen(port))
                    return $"③ 本地服务已在 127.0.0.1:{port} 运行（复用现有进程）。";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"-m http.server {port}",
                    WorkingDirectory = deployDir,
                    UseShellExecute = true, // 开独立控制台常驻；false 进程会随 Editor 退出
                });
                return $"③ 已启动本地 CDN 服务（python -m http.server {port}，目录 {deployDir}）。";
            }
            catch (Exception e)
            {
                return $"③ 起服务失败（{e.Message}）。确认 python 在 PATH，或手动在部署目录起 HTTP 服务。";
            }
        }

        // ───────────── 内部工具 ─────────────

        // 部署目录 = 项目根/CDN。Application.dataPath 指向 <项目根>/Assets，取其父目录即项目根，零绝对路径、可移植。
        private static string ResolveDeployDir()
            => Path.Combine(Directory.GetParent(Application.dataPath).FullName, CdnFolderName);

        // 端口取自场景 AssetSystemConfigModel 的 CDN URL（单一配置源）；缺失或解析失败兜底到 DefaultPort。
        private static int ResolveServerPort()
        {
            var model = UnityEngine.Object.FindFirstObjectByType<AssetSystemConfigModel>();
            if (model != null
                && Uri.TryCreate(model.MainCdnUrl, UriKind.Absolute, out var uri)
                && uri.Port > 0)
                return uri.Port;
            return DefaultPort;
        }

        // YooAsset 3.x 可编程构建管线（SBP）的标准参数装配，对齐官方 ScriptableBuildPipelineViewer。
        private static BuildResult BuildPackage(string version)
        {
            var buildParameters = new ScriptableBuildParameters
            {
                BuildOutputRoot = BundleBuilderHelper.GetDefaultBuildOutputRoot(),
                BundledFileRoot = BundleBuilderHelper.GetStreamingAssetsRoot(),
                BuildPipeline = EBuildPipeline.ScriptableBuildPipeline.ToString(),
                BuildBundleType = (int)EBundleType.AssetBundle,
                BuildTarget = EditorUserBuildSettings.activeBuildTarget,
                PackageName = PackageName,
                PackageVersion = version,
                EnableSharePackRule = true,
                VerifyBuildingResult = true,
                FileNameStyle = EFileNameStyle.HashName,
                CompressOption = ECompressOption.LZ4,
                // Host 模式初始化时要先读 StreamingAssets 里的内置清单（BuiltinCatalog）——没有就 404 起不来。
                // 按一个没有任何资源带的 tag 拷贝：只生成内置清单、0 个 bundle 进 StreamingAssets，
                // 于是 Host 能初始化、而所有 bundle 仍从 CDN 真实下载（这正是要演示的）。
                BundledCopyOption = EBundledCopyOption.ClearAndCopyByTags,
                BundledCopyParams = "__builtin_none__",
            };
            var pipeline = new ScriptableBuildPipeline();
            return pipeline.Run(buildParameters, true);
        }

        // 把版本输出目录里的文件平铺拷到「CDN 根/包名」子目录；与 GameRemoteService 的 {CDN}/{包名}/{文件} 取址对齐。
        // 只清空本包子目录（不动 CDN 根，也不动其它包的子目录）——python 服务器的工作目录是 CDN 根，删根会被占用拒绝；
        // 删本包子目录则安全，于是多包可以各自独立重新部署、互不影响。返回本包部署目录的完整路径。
        private static string DeployToPackageDir(string outputPackageDir, string deployDir, out int count)
        {
            string packageDir = Path.Combine(deployDir, PackageName);
            if (Directory.Exists(packageDir)) Directory.Delete(packageDir, true);
            Directory.CreateDirectory(packageDir);

            count = 0;
            foreach (var file in Directory.GetFiles(outputPackageDir, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(packageDir, Path.GetFileName(file)), true);
                count++;
            }
            return packageDir;
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

        // 构建相关菜单的 Play 模式守卫：SBP 不能在 Play 模式跑，提前拦下并提示。
        private static bool EnsureEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("需退出 Play 模式",
                    "AssetBundle 构建管线不能在 Play 模式运行。请先停止 Play，再执行 CDN 构建 / 部署。", "好");
                return false;
            }
            return true;
        }

        private static void Report(string title, string msg)
        {
            Debug.Log("[CDN] " + title + "：\n" + msg);
            EditorUtility.DisplayDialog("CDN · " + title, msg, "好");
        }
    }
}
