using Game.Framework.Demo.Core;
using Game.Framework.Logging;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·模块化：把首次接入不需要理解的程序集、Linker、热更根和真实构建证据集中到发布期章节。
    /// </summary>
    public sealed class ModuleBoundariesModule : DemoModuleBase
    {
        private const string ModuleAuditMenu = "SSFramework/诊断与分析/模块与依赖";
        private const string BuildSizeProbeMenu = "SSFramework/诊断与分析/真实构建体积";
        private const string ConfigCenterMenu = "SSFramework/配置中心";

        public override string Id => "module-boundaries";
        public override string Title => "模块化 · 依赖与裁剪";
        public override string Category => "进阶";
        public override int Order => 50;
        public override DemoTeachingKind TeachingKind => DemoTeachingKind.Concept;
        public override string Summary =>
            "接入完成、准备控制发布体积时再读：分清 asmdef 编译依赖、DLL 真实引用、link.xml / 热更根与目标平台 Build 证据，" +
            "再按审计结果做可验证的结构裁剪。";

        public override void Build(DemoModuleHost host)
        {
            host.AddPositioning("发布期的模块边界与裁剪证据，不属于第一次接入");
            host.AddNote("「接入你的项目」只负责让最小闭环跑起来；当项目开始关心 Web 包体、可选后端或热更部署时，再用本章判断一个 Module 为什么被保留、能否安全移除。先收集证据，再改依赖图，最后用目标平台构建验证。");

            host.AddSectionTitle("先分清五种证据：它们回答的不是同一个问题");
            host.AddTable(
                new[] { "证据", "真正回答什么", "不要误读成" },
                new[] { "源码 / Package 存在", "目录、导入器、asmdef 已安装", "已经被业务使用" },
                new[] { "参与 Player 编译", "当前平台会产出 DLL", "最终 Player 一定保留" },
                new[] { "DLL 真实引用", "Framework / 项目谁直接消费它", "能看见字符串反射或场景根" },
                new[] { "linker / 热更根", "link.xml 是否保留；Profile 是否部署完整 DLL", "已经完成同步和 Generate" },
                new[] { "目标平台 Build", "IL2CPP、引擎模块、压缩后的最终组合", "能从一个平台外推到另一个平台" });
            host.AddTip("最常见的误判是把“asmdef 没有直接引用”当成“发布物里一定不存在”。编译图、UnityLinker 根和热更部署是三套机制，必须分别看证据。");

            host.AddSectionTitle("程序集接线：显式依赖只解决可见性");
            host.AddNote("框架程序集 `Game.Framework` 是 `autoReferenced:false`——业务 asmdef 必须显式加入 references 才能使用；业务代码直接使用 `R3`（如 `RP<T>`）或 `UniTask` 类型时，也显式引用对应程序集。这样能看清依赖方向，但不会自动完成包体裁剪。");
            host.AddSubNote("一个关键例外：当前可选 Runtime Module 都引用 Core。若 Core 热更，只要某个 Module 仍参与 Player 编译，它就不能被单独留在 AOT；否则会形成 `AOT → 热更` 引用，校验器会拒绝。",
                new CodeRef("Assets/Game/Framework/Build/HybridCLR/Editor/HotUpdateAssemblyGraph.cs", "class HotUpdateAssemblyGraph", "热更传播约束 · AOT 不引用热更"));

            host.AddSectionTitle("可选 Editor 工具也应跟随所属 Module");
            host.AddNote("配置中心不是一张写死全部 Profile 类型的中央名单：每个可选 Editor Module 只登记自己拥有的配置卡片。删除 Module 并完成域重载后，对应注册和卡片会一起消失；中央窗口不会为了显示一张卡片反向依赖所有可选实现。");
            host.AddConcept("这种设计换来了什么", "中央窗口只依赖很窄的 Registry Seam，可选 Build、Fonts、Proto、UGUI 等实现保持可删除；代价是每个新配置类型都要写一条本地注册，并由契约测试检查 id、顺序和菜单入口。");
#if UNITY_EDITOR
            host.AddActionRow("打开配置中心（观察已安装 Module 的卡片）",
                () => RunMenu(ConfigCenterMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkConfigRegistry.cs", "public static class FrameworkConfigRegistry", "FrameworkConfigRegistry · Module 自注册 Seam"));
#endif

            host.AddSectionTitle("安全裁剪工作流：原因 → 变更 → 目标平台证据");
            host.AddStep("①", "从 Core-only 起步，只按需加入 UI Core 与一个后端；Bridge、Fonts、Yoo、Proto 等由真实需求驱动，不为“也许会用”提前接入。");
            host.AddStep("②", "先用模块裁剪审计查看 Player 真实消费者、全 asmdef 删除阻塞者、热更传播与 `link.xml` 根；准备移除时先迁移直接消费者。");
            host.AddStep("③", "把退出 Player 编译图、清理热更 Profile、删除 Module 自有 `link.xml` 与重新 Generate 视作同一次变更，避免中间状态制造 AOT / 热更反向引用。");
            host.AddStep("④", "最后跑 Module Audit、Unity 测试与目标平台真实构建；隔离构建探针只能给可比较上界，发布判断仍以真实 BuildReport 为准。");
#if UNITY_EDITOR
            host.AddActionRow("打开模块裁剪审计（逐 Module 保留原因 / 任意入口）",
                () => RunMenu(ModuleAuditMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkModuleAudit.cs", "internal static class FrameworkModuleAudit", "Framework Module Audit · 真实引用闭包"));
            host.AddActionRow("打开真实构建体积证据（隔离删除 / 任意 Module）",
                () => RunMenu(BuildSizeProbeMenu),
                new CodeRef("Assets/Game/Framework/Editor/FrameworkBuildSizeProbe.cs", "internal static class FrameworkBuildSizeProbe", "Framework Build Size Probe · 隔离删除构建"));
#endif

            host.AddSectionTitle("容易混淆：结构裁剪、成员裁剪与包管理");
            host.AddTable(
                new[] { "机制", "负责什么", "典型动作" },
                new[] { "asmdef / 结构裁剪", "程序集是否进入编译图，依赖方向是否成立", "迁移消费者后删除或排除整个 Module" },
                new[] { "UnityLinker / 成员裁剪", "已进入 Player 的程序集里，哪些类型和成员仍需保留", "维护反射入口与 `link.xml`，做 IL2CPP 回归" },
                new[] { "HybridCLR Profile", "哪些完整 DLL 作为热更程序集部署", "同步 Profile、Generate 并校验 AOT → 热更边界" },
                new[] { "UPM", "Package 的安装、版本与粗粒度分发", "模块边界稳定后再抽独立 package" });
            host.AddCaution("不要为了“看起来模块化”机械拆 asmdef，也不要在没有目标平台体积证据时删除可选能力。只有模块具有独立变化原因、明确消费者和可单独验证的删除路径时，拆分才降低成本；否则只是把一次修改变成多处接线。");
            host.AddSubNote("Module 的职责、依赖方向与删除测试集中记录在 `docs/framework-module-map.md`；完整工作流见 framework-guide §26 / ADR-0039。");
        }

#if UNITY_EDITOR
        private static void RunMenu(string path)
        {
            if (!UnityEditor.EditorApplication.ExecuteMenuItem(path))
                Log.Warning($"[Demo] 菜单不存在：{path}");
        }
#endif
    }
}
