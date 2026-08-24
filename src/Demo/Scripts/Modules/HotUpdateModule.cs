using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·热更机制：讲 HybridCLR 热更的列表驱动设计——热更范围是部署决策不是代码属性、程序集三层、
    /// 构建菜单分工与迭代边界、Boot 引导流程。编辑器恒走旁路（程序集本就在 AppDomain），
    /// 真实「下载 → Assembly.Load」只在 IL2CPP 真机发生，所以本章以讲解 + 源码跳转为主，
    /// 唯一可现场执行的是构建期校验（真实调用引用图校验器）。
    /// </summary>
    public sealed class HotUpdateModule : DemoModuleBase
    {
        private const string ValidateMenu = "SSFramework/热更构建/校验热更程序集列表";
        private const string ProfileMenu = "SSFramework/热更构建/热更配置 (HotUpdate Profile)";

        public override string Id => "hotupdate";
        public override string Title => "热更 · HybridCLR";
        public override string Category => "进阶";
        public override int Order => 20;
        public override string Summary =>
            "列表驱动热更范围（框架本体也可热更）+ 薄 Boot 程序集引导。编辑器恒走旁路、真机才走下载加载，" +
            "本章讲原理与构建分工，校验按钮真实可跑；深度见 framework-guide §15 / ADR-0008。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("编辑器看不到热更，因为它被刻意旁路了");
            host.AddNote("编辑器下所有程序集本就在 AppDomain 里，引导器直接反射进入口——没有下载、没有 Assembly.Load，开发期对热更机制零感知（这是设计目标，不是缺演示）。真实链路只在 IL2CPP 真机发生：改完热更代码只重打代码包，玩家包重启即用新逻辑。",
                new CodeRef("Assets/Game/Framework/Boot/HotUpdateLauncher.cs", "class HotUpdateLauncher", "Boot 引导器（编辑器旁路 + 真机全流程）"));

            // ── 心智模型 ──
            host.AddSectionTitle("心智模型：热更范围是部署决策，不是代码属性");
            host.AddNote("哪些程序集热更，由热更配置（FrameworkHotUpdateProfile）里的列表决定——谁进列表谁热更，按版本可调。所以目录与程序集按领域命名（Game.Main / Game.X 模块 / Game.DLC.Y），永远不出现「HotUpdate」这种按部署属性起的名字；框架本体默认也在列表里（可热修框架 bug），性能敏感项目把它移出列表退回 AOT，业务代码零改动。");
            host.AddTable(
                new[] { "层", "程序集", "热更？" },
                new[] { "引导", "`Game.Framework.Boot`（薄壳：下载/补元数据/加载/反射入口）", "永不（鸡生蛋）" },
                new[] { "框架", "`Game.Framework`（内核）、`Game.Framework.Asset.Yoo`（YooAsset 适配）", "默认热更，可退 AOT" },
                new[] { "业务", "`Game.Main` 及未来模块 / DLC", "热更（主战场）" });
            host.AddSubNote("热更程序集一律 `autoReferenced:false`（防止散落脚本隐式引用构成「AOT→热更」违规）；铁律「AOT 不能引用热更」由构建期校验器机器执行，不靠人脑记。");

            // ── 引导流程 ──
            host.AddSectionTitle("真机引导流程（Boot 场景是唯一随包场景）");
            host.AddNote("初始化代码包（专用 RawFile 包，与资源包彻底分家）→ 检查/下载代码更新（按 hash 增量，改哪个程序集下哪个）→ 读热更清单 → 逐个补 AOT 泛型元数据 → 按引用图拓扑序 Assembly.Load → 反射调入口 GameEntry.Enter()。入口之后就是热更世界：创建全局 Context、初始化资源系统、加载真实首场景都从这往下走。",
                new CodeRef("Assets/Game/Main/GameEntry.cs", "class GameEntry", "入口约定（当前为 IL2CPP 真机框架自检）"));

            // ── 构建分工 ──
            host.AddSectionTitle("构建菜单分工与迭代边界");
            host.AddTable(
                new[] { "菜单（SSFramework/热更构建）", "何时执行", "耗时" },
                new[] { "1. 同步热更设置", "改了热更列表后", "秒" },
                new[] { "2. 生成桥接与裁剪文件（Generate All）", "首次接入 / 升级 Unity 或 HybridCLR / 增删第三方库 / 改档位", "分钟" },
                new[] { "3. 构建代码包", "日常每次热更迭代", "几十秒" },
                new[] { "4. 部署代码包", "跟在 3 后面（平铺到 Deploy，与资源包同套 CDN 结构）", "秒" });
            host.AddSubNote("迭代边界（真机实测）：热更代码新增跨 AOT 泛型用法（Odin 序列化热更类型、新 R3 订阅泛型等）也只需 3+4——SuperSet 补元数据 + 解释器兜底已覆盖；真正需要 2 + 重出安装包的只有 AOT 集合本身的变化。",
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkHotUpdateBuilder.cs", "class FrameworkHotUpdateBuilder", "构建实现（CompileDll → 清单 → RawFile 包）"));

            // ── 可现场执行的部分:构建期校验 ──
            host.AddSectionTitle("现场可跑：构建期校验（真实调用，与菜单同源）");
            host.AddNote("校验「AOT 不引用热更」（违规逐条指出元凶与修法）并展示自动拓扑排序的加载顺序——这是构建管线在编辑器侧真实存在的部分，与真机无关，可以现场跑。");
            host.AddActionRow("校验热更程序集列表（弹窗显示结果）", () => RunMenu(ValidateMenu),
                new CodeRef("Assets/Game/Framework/Build/Editor/HotUpdateAssemblyGraph.cs", "class HotUpdateAssemblyGraph", "引用图校验 + 拓扑排序"));
            host.AddActionRow("定位热更配置（FrameworkHotUpdateProfile）", () => RunMenu(ProfileMenu),
                new CodeRef("Assets/Game/Framework/Build/Editor/FrameworkHotUpdateProfile.cs", "class FrameworkHotUpdateProfile", "热更配置定义"));

            host.AddTip("深度阅读：docs/framework-guide.md §15（用法手册）、docs/adr/0008（设计取舍与已验证边界）。");
        }

        private static void RunMenu(string path)
        {
            if (!UnityEditor.EditorApplication.ExecuteMenuItem(path))
                Debug.LogWarning($"[HotUpdateModule] 菜单执行失败：{path}（菜单路径变更？）");
        }
    }
}
