namespace Game.Framework.Editor
{
    /// <summary>
    /// SSFramework 人工工具的稳定菜单路径。顶层菜单只负责打开窗口；会写项目、生成代码、构建、
    /// 清理或启动外部进程的操作，必须在窗口内说明影响后再由用户点击。
    /// </summary>
    public static class FrameworkMenuPaths
    {
        /// <summary>全部框架顶部菜单的根前缀。</summary>
        public const string Root = "SSFramework/";

        /// <summary>按使用意图浏览全部已安装 Module 工作台的入口。</summary>
        public const string Tools = Root + "工具中心";
        /// <summary>只读发现工程内 Framework 配置资产的入口。</summary>
        public const string Configuration = Root + "配置中心";

        /// <summary>资源构建、部署与本地服务工作台。</summary>
        public const string AssetBuild = Root + "构建与发布/资源构建";
        /// <summary>HybridCLR 同步、代码包构建与部署工作台。</summary>
        public const string HotUpdateBuild = Root + "构建与发布/代码热更新";

        /// <summary>Luban 多配置生成工作台。</summary>
        public const string Luban = Root + "代码生成/配置表 (Luban)";
        /// <summary>Protobuf 多配置生成工作台。</summary>
        public const string Protobuf = Root + "代码生成/Protobuf";
        /// <summary>服务安装器扫描与代码生成工作台。</summary>
        public const string ServiceInstaller = Root + "代码生成/服务安装器";
        /// <summary>UGUI 绑定配置、预览与生成工作台。</summary>
        public const string UIBinding = Root + "代码生成/UI 绑定";
        /// <summary>字体常用字集扫描与生成工作台。</summary>
        public const string FontCharset = Root + "代码生成/字体字集";

        /// <summary>配置驱动场景导航与 Boot Play 策略工作台。</summary>
        public const string SceneShortcuts = Root + "开发辅助/场景快捷入口";
        /// <summary>解释并打开工程、缓存和日志目录的导航窗口。</summary>
        public const string ProjectFolders = Root + "开发辅助/常用目录";
        /// <summary>可选 Odin Editor Adapter 的能力说明窗口。</summary>
        public const string OdinAdapter = Root + "开发辅助/Odin Inspector 适配";

        /// <summary>运行时 Context 与服务状态诊断窗口。</summary>
        public const string RuntimeDiagnostics = Root + "诊断与分析/运行时诊断";
        /// <summary>程序集、可选 Module 与第三方依赖审计窗口。</summary>
        public const string ModuleAudit = Root + "诊断与分析/模块与依赖";
        /// <summary>隔离 Player Build 的真实构建体积分析窗口。</summary>
        public const string BuildSizeProbe = Root + "诊断与分析/真实构建体积";

        /// <summary>MCP / CI 使用的稳定机器菜单根；它是“人工菜单只导航”规则的显式例外。</summary>
        public const string AutomationRoot = Root + "诊断/AI 自动化/";
        /// <summary>保存有路径的脏场景并拒绝未命名场景的外部自动化契约；不得随人工信息架构改名。</summary>
        public const string PlayModeTestPreflight = AutomationRoot + "PlayMode 测试预检（保存脏场景）";
        /// <summary>Core 隔离 Player Build 的外部自动化契约；不得随人工信息架构改名。</summary>
        public const string CoreBuildSizeProbe = AutomationRoot + "Core 隔离构建（Player Build）";
        /// <summary>常用 Module 档位隔离 Player Build 的外部自动化契约；不得随人工信息架构改名。</summary>
        public const string CommonBuildSizeProbe = AutomationRoot + "常用档位隔离构建（Core + UGUI + Toolkit）";
    }
}
