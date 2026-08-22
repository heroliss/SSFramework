using Game.Framework;
using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·YooAsset 底层实现：讲「资源加载」章背后、当前默认后端 YooAsset 的原理——四种 PlayMode、各目录、各清单文件、
    /// 各构建管线的对比表，以及 EditorSimulate / Host 两条「模式 → 操作 → 管线 → 产物 → 运行时读取」的底层流程。
    /// <b>这是可替换的后端实现，不是框架契约</b>：框架经 IAssetProvider 解耦，换库只改 AssetProviderFactory，本章内容随之替换。
    /// </summary>
    public sealed class YooAssetModule : DemoModuleBase
    {
        public override string Id => "yooasset-internals";
        public override string Title => "YooAsset · 底层实现";
        public override string Category => "进阶";
        public override int Order => 10;
        public override string Summary =>
            "「资源加载」章的底层：当前默认后端 YooAsset 的 PlayMode / 目录 / 清单文件 / 构建管线对比，以及 EditorSimulate 与 Host 的底层流程。可替换、非框架契约。";

        public override void Build(DemoModuleHost host)
        {
            var settingsModel = UnityEngine.Object.FindFirstObjectByType<AssetSystemConfigModel>();

            // ── 定位 ──
            host.AddSectionTitle("定位：YooAsset 是「当前默认后端」，不是框架契约");
            host.AddNote("框架把所有 YooAsset 接触面收口在 `IAssetProvider`，业务与「资源加载」章只认接口。YooAsset 只是 `AssetProviderFactory` 里 new 出来的默认实现——换 Addressables / 自研只改那一行，本章这些底层随之替换。所以本章是「了解当前后端怎么运转」，不是框架必须知识。",
                new CodeRef("Assets/Game/Framework/Core/Asset/AssetProviderFactory.cs", "CreateDefault", "provider 工厂（换库就改这）"));

            // ── 表①·四种 PlayMode ──
            host.AddSectionTitle("四种 PlayMode 对比");
            host.AddTable(
                new[] { "模式", "资源从哪来", "要先构建吗", "构建管线", "运行时读", "适用" },
                new[] { "`EditorSimulate`", "工程源资源(AssetDatabase)", "否（自动模拟）", "ESBP（自动）", "模拟清单", "开发期默认，改完即时生效" },
                new[] { "`Offline`", "随包内置(StreamingAssets)", "是", "SBP", "内置清单 + 版本清单 + bundle", "单机 / 无热更" },
                new[] { "`Host`", "内置首包 + 远端 CDN", "是", "SBP", "内置清单→版本→清单→按需下载", "需热更的主流方案" },
                new[] { "`Web`", "纯远端 HTTP", "是", "SBP", "远端清单 / bundle（不落缓存）", "WebGL" });
            if (settingsModel != null)
                host.AddSubNote($"当前场景配置：PlayMode = {settingsModel.PlayMode}（WebGL 构建会强制 Web）。");

            // ── 表②·目录 ──
            host.AddSectionTitle("目录都装什么（构建相关目录都收在 AssetBuild/ 下）");
            host.AddTable(
                new[] { "目录", "作用", "谁写 / 谁读", "入库" },
                new[] { "`AssetBuild/Bundles/<平台>/`", "构建输出：原始 bundle + 清单", "构建写 / 部署时拷走", "否(gitignore)" },
                new[] { "`Assets/StreamingAssets/yoo/`", "首包内置（随安装包）", "构建写 / 运行时只读(`Offline`·`Host`)", "否(构建产物)" },
                new[] { "`AssetBuild/Downloaded/<包>/`", "下载缓存（`Host` 下载落地）", "运行时下载写 / 运行时读", "否(gitignore)" },
                new[] { "`AssetBuild/Deploy/`", "本地联调部署目录（python 服务的根）", "构建部署写 / python 服务读", "否(gitignore)" });
            host.AddSubNote("这些构建目录名都在单一真源 `AssetBuildLayout` 里定义（构建工具与运行时 provider 共用），改名只改一处。编辑器期 YooAsset 的下载缓存被重定向进 `AssetBuild/Downloaded`（否则它默认会在项目根冒出 yoo/、每次 `Host` Play 重生）；真机改用各平台 persistentDataPath，这个重定向纯属编辑器便利。",
                new CodeRef("Assets/Game/Framework/Core/Asset/AssetBuildLayout.cs", "class AssetBuildLayout", "构建目录单一真源"));
#if UNITY_EDITOR
            host.AddActionRow("打开下载缓存目录（AssetBuild/Downloaded）", RevealCacheDir,
                new CodeRef("Assets/Game/Framework/Core/Asset/AssetBuildLayout.cs", "DownloadedRoot", "下载缓存目录（单一真源）"));
            host.AddActionRow("打开本地 CDN 部署目录（AssetBuild/Deploy）", RevealCdnDir,
                new CodeRef("Assets/Game/Framework/Core/Asset/AssetBuildLayout.cs", "DeployRoot", "部署目录（单一真源）"));
#endif

            // ── 表③·清单文件 ──
            host.AddSectionTitle("三类清单文件各管一事");
            host.AddTable(
                new[] { "文件", "作用", "位置", "谁读 / 顺序" },
                new[] { "`BuiltinCatalog.bytes`", "内置目录：列出随包内置了哪些文件", "`StreamingAssets/yoo/<包>`", "`Offline`·`Host` 初始化最早读" },
                new[] { "`<包>.version`", "版本号（一行文本＝最新版本）", "CDN(`Host`) / 内置(`Offline`)", "`Host` 先读它知道最新版" },
                new[] { "`<包>_<版本>.bytes`", "版本清单：地址→bundle 解析图 + 哈希/依赖", "CDN / 内置", "读完版本号后读（真正解析靠它）" });
            host.AddSubNote("注意：`BuiltinCatalog` 是「内置目录」（回答“本地已有啥”），不是地址解析清单（那是 `<包>_<版本>.bytes`）；且只有 `Offline` / `Host` 读它，`EditorSimulate` / `Web` 不读。");

            // ── 表④·构建管线 ──
            host.AddSectionTitle("构建管线（一个包由且仅由一种管线构建）");
            host.AddTable(
                new[] { "管线", "效果", "产物", "对应运行时加载" },
                new[] { "`EditorSimulate` (ESBP)", "不打包，生成模拟清单", "映射到 AssetDatabase", "`EditorSimulate` 模式（自动）" },
                new[] { "`Scriptable` (SBP)", "打真 AssetBundle，增量/确定性好", "bundle + 清单", "`Load<T>` / `LoadScene`，生产首选" },
                new[] { "`RawFile` (RFBP)", "原样拷贝、不打包成 AB", "原生文件 + 清单", "`LoadText` / `LoadBytes`（按包类型自动路由，AB 包的文本类资产也走这俩）" },
                new[] { "`Legacy` (LBP)", "旧版 AB 构建", "bundle + 清单", "同 SBP，无增量优势，新项目少用" },
                new[] { "`Archive` (AFBP)", "同名 bundle 多文件合并归档", "归档文件", "专用 / 进阶" });
            host.AddSubNote("本框架构建器只用 SBP（`FrameworkAssetBuilder`，不提供 Legacy）；上表列全只为了解 YooAsset 全貌。");
            host.AddSubNote("SBP 内置 shader 包（坑）：YooAsset 窗口构建总要打「内置 shader 包」把引擎内置 shader 去重，但当前 SBP 版本那个任务已 obsolete，遇到「包里没有任何引用内置 shader 的资产」（如纯 Sprite 的样例包）会取空 layout 直接崩。所以构建器把它做成按包开关：真实有材质/UI 的包开（正确去重）、零 shader 的包关。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkAssetBuilder.cs", "ResolveBuiltinShaderBundleName", "内置 shader 包开关 + 原委"));

            // ── 底层流程：EditorSimulate ──
            host.AddSectionTitle("底层流程 · EditorSimulate（开发期）");
            host.AddStep("①", "进 Play → `AssetInitSystem` 触发初始化 → provider 在 `EditorSimulate` 分支自动跑一次 ESBP（模拟构建）。",
                new CodeRef("Assets/Game/Framework/Asset.Yoo/YooAssetProvider.cs", "case AssetPlayMode.EditorSimulate", "EditorSimulate 自动模拟构建"));
            host.AddStep("②", "ESBP 生成「地址→AssetDatabase 路径」模拟清单 → 运行时直接读工程源资源：免打包、免下载、改完即时生效，也与平台无关。");

            // ── 底层流程：Host ──
            host.AddSectionTitle("底层流程 · Host（构建 → 部署 → 服务 → 下载缓存）");
            host.AddStep("①", "构建：菜单 SSFramework/资源构建/「1. 构建资源包」或 CI 调 `FrameworkAssetBuilder`（都用 SBP）→ bundle 进 `AssetBuild/Bundles/<平台>/`，内置清单写 `StreamingAssets/yoo`。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkAssetBuilder.cs", "public static (bool ok, string message) Build", "唯一构建实现"));
            host.AddSubNote("打哪些包、每包首包策略 / 内置 shader 包开关，都读构建配置 `FrameworkAssetBuildProfile`（单一配置源）。首包：含指定 tag 的 bundle 拷进 `StreamingAssets` 当首包（多 tag 用分号 `;` 分隔），其余运行时从 CDN 下；样例包用空 tag → 0 内置、只出内置清单，于是全从 CDN 真实下载。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkAssetBuildProfile.cs", "class FrameworkAssetBuildProfile", "构建配置（按包）"));
            host.AddStep("②", "部署：产物按「每个包一个子目录」拷到 项目根/`AssetBuild/Deploy`（本地联调）或 CI 上传真实 CDN。`GameRemoteService` 按 {CDN}/{包名}/{文件} 取址。",
                new CodeRef("Assets/Game/Framework/Asset.Yoo/YooAssetProvider.cs", "class GameRemoteService", "远端取址实现"));
            host.AddStep("③", "起服务：菜单「3. 启动本地 CDN 服务」= python -m http.server（端口取自构建 profile 的 `LocalServePort`，须与场景 `AssetSystemConfigModel.CdnUrls` 第一条端口一致）；生产里这步换成 CDN 厂商。",
                new CodeRef("Assets/Game/Framework/Build/Editor/AssetBuildMenu.cs", "StartServer", "本地起服务（仅联调）"));
            host.AddStep("④", "进 Play(`Host`)：先读 `StreamingAssets` 的 `BuiltinCatalog` → 拉远端 `.version` / 对应清单；远端不可用则激活随包内置版本清单 → 缺的非内置 bundle 按需从 CDN 下载并缓存到 项目根/`AssetBuild/Downloaded/<包>`。",
                new CodeRef("Assets/Game/Framework/Asset.Yoo/YooAssetProvider.cs", "case AssetPlayMode.Host", "Host 初始化实现"));
            host.AddSubNote("`CdnUrls` 是候选列表：本地联调通常只填 `http://127.0.0.1:8080/`，多条时版本号 / 清单请求会随 YooAsset 的失败计数轮转重试；候选必须是等价镜像。包级「启用按需下载」（默认勾选）只影响 Host 下未缓存 bundle 的 `Load`：取消勾选后直接失败，强制先显式跑下载器。",
                new CodeRef("Assets/Game/Framework/Core/Asset/AssetSystemConfigModel.cs", "CdnUrls", "运行时 CDN 配置"));
#if UNITY_EDITOR
            host.AddActionRow("定位 Collector 分包配置（构建按它执行）", () =>
                DemoEditorNav.PingAsset("Assets/Game/Framework/Settings/AssetBundleCollectorSetting.asset"));
            host.AddActionRow("定位 AssetSystem 配置节点（切 PlayMode 在这）", () =>
            {
                if (settingsModel != null) DemoEditorNav.PingSceneObject(settingsModel.gameObject);
            }, new CodeRef("Assets/Game/Framework/Core/Asset/AssetSystemConfigModel.cs", "class AssetSystemConfigModel", "资源系统配置(Model)"));
#endif
            host.AddTip("两个最常踩的坑：① 平台——AssetBundle 按平台区分，且编辑器进程本身是 Windows，加载不了为 Android 等移动平台构建的 bundle；要在编辑器里测 Host，先把 Build Target 切到 Standalone Windows 再重新构建，测移动平台请上真机。② 顺序——至少先构建再进 Play；要验证远端更新或加载非内置 bundle，还需部署并启动 CDN。Host 可以在远端失败时回退内置清单，但补构建后仍要重进 Play 才会重新初始化。");

            // ── 机制：清缓存 ──
            host.AddSectionTitle("机制：清缓存");
            host.AddSubNote("清缓存（`ClearCache`，正式游戏也用）：清的是 项目根/`AssetBuild/Downloaded` 里已下载的 bundle，同步内存缓存记录后 `IsNeedDownload` 重新变真。`All` = 设置里「清除缓存」/ 损坏恢复 / 强制全量重下；`Unused` = 热更到新版本后回收旧版本残留（省空间）。它不卸载已加载到内存的资源（那是另一回事）。");

            // ── 机制：加密（可选，两端成对）──
            host.AddSectionTitle("机制：资源加密（可选）");
            host.AddSubNote("加密分构建侧（写加密产物）与运行时侧（读时解密），必须【成对、参数一致】。框架内置「偏移加密」开箱即用：构建配置 `FrameworkAssetBuildProfile.FileOffset` 设 N(>0) → 构建在每个 bundle 头插入 N 字节，挡住直接用 AB 提取工具打开；运行时 `AssetSystemConfigModel.FileOffset` 设【相同】 N → 加载时跳过这 N 字节。两值对不上会读坏所有 bundle。",
                new CodeRef("Assets/Game/Framework/Build/Editor/GameBundleOffsetEncryptor.cs", "class GameBundleOffsetEncryptor", "构建侧偏移加密器"));
            host.AddSubNote("偏移是【弱】加密（只换文件头、不动 bundle 正文，解密近乎零成本）。要真加密（XOR/AES 等打乱正文）需自实现 `IBundleEncryptor` + 运行时对应解密器，两端一起换——完整步骤（含清单加密、流式解密、密钥/热更注意点）见 `docs/asset-encryption.md`。",
                new CodeRef("Assets/Game/Framework/Asset.Yoo/YooAssetProvider.cs", "ApplyDecryptor", "运行时侧解密器注册（按 FileOffset）"));

            // ── 操作：本地联调 vs 生产 ──
            host.AddSectionTitle("操作：本地联调 vs 生产构建");
            host.AddNote("本地联调（编辑器内测 `Host`）：菜单 SSFramework/资源构建 下依次「1. 构建资源包 → 2. 部署到本地 CDN → 3. 启动本地 CDN 服务」，再把场景 `PlayMode` 切 `Host` 重进 Play。三步拆开、用的就是生产同款构建，只有「本地起服务」是联调专属。",
                new CodeRef("Assets/Game/Framework/Build/Editor/AssetBuildMenu.cs", "class AssetBuildMenu", "统一构建菜单"));
            host.AddNote("正式发版：`FrameworkAssetBuilder` 是全工程唯一构建/部署实现，逐包构建 + 整理 CDN 待上传产物，可被 CI 的 `-executeMethod` 调用；上传 CDN 与版本管理交给 CI（见 build-assets.yml）。本地联调与生产用的是同一套构建，不是两套。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkAssetBuilder.cs", "class FrameworkAssetBuilder", "生产构建入口"));
        }

#if UNITY_EDITOR
        // 部署目录 / 下载缓存目录都取自 AssetBuildLayout（构建目录单一真源），避免在 demo 里另写死路径再次漂移。
        private static void RevealCdnDir() =>
            UnityEditor.EditorUtility.RevealInFinder(AssetBuildLayout.DeployRoot);

        private static void RevealCacheDir() =>
            UnityEditor.EditorUtility.RevealInFinder(AssetBuildLayout.DownloadedRoot);
#endif
    }
}
