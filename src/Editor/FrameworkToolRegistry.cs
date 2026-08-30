using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Game.Framework.Editor
{
    /// <summary>工具中心使用的意图分类；分类面向使用者，不映射运行时程序集层次。</summary>
    public enum FrameworkToolCategory
    {
        BuildAndRelease,
        CodeGeneration,
        Diagnostics,
        Development,
    }

    /// <summary>
    /// 一个可选 Editor Module 对工具中心贡献的导航描述。描述符只含展示信息和菜单路径，中央窗口不反向引用
    /// Module 的具体类型；删除整个 Module 后，它的注册自然消失。
    /// </summary>
    public sealed class FrameworkToolDescriptor
    {
        /// <summary>
        /// 创建不可变导航描述。<paramref name="id"/> 是跨域重载稳定的 Module 工具身份；标题与菜单路径不能为空，
        /// 摘要允许为空但工具中心通常应提供能帮助新用户判断用途的一句话。
        /// </summary>
        public FrameworkToolDescriptor(
            string id,
            FrameworkToolCategory category,
            int order,
            string title,
            string summary,
            string menuPath)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("工具 id 不能为空。", nameof(id));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("工具标题不能为空。", nameof(title));
            if (string.IsNullOrWhiteSpace(menuPath)) throw new ArgumentException("工具菜单路径不能为空。", nameof(menuPath));
            Id = id;
            Category = category;
            Order = order;
            Title = title;
            Summary = summary ?? string.Empty;
            MenuPath = menuPath;
        }

        /// <summary>跨 Module 唯一、在同一工具重入注册时保持不变的身份。</summary>
        public string Id { get; }
        /// <summary>面向使用者的工具分类，不代表程序集依赖层次。</summary>
        public FrameworkToolCategory Category { get; }
        /// <summary>同一分类内的稳定显示顺序；相同时再按标题排序。</summary>
        public int Order { get; }
        /// <summary>工具中心卡片标题。</summary>
        public string Title { get; }
        /// <summary>工具用途、前置条件或影响的简短说明；永不为 <c>null</c>。</summary>
        public string Summary { get; }
        /// <summary>点击卡片时执行的窗口菜单路径；注册不执行该菜单。</summary>
        public string MenuPath { get; }
    }

    /// <summary>
    /// 可选 Editor Module 与通用工具中心之间的窄 Seam。相同 id + 相同元数据可安全重入；同 id 的不同工具直接抛错，
    /// 避免卡片被静默覆盖。快照按分类、顺序和标题稳定排序，便于 UI 与测试消费。
    /// </summary>
    public static class FrameworkToolRegistry
    {
        private static readonly Dictionary<string, FrameworkToolDescriptor> Tools =
            new(StringComparer.Ordinal);

        /// <summary>工具集合发生真实增删时触发；完全相同的重入注册不会触发。</summary>
        public static event Action Changed;

        /// <summary>
        /// 登记一个 Module 工具。相同 id 与相同元数据可安全重入；相同 id 但元数据不同会抛
        /// <see cref="InvalidOperationException"/>，避免后加载 Module 静默覆盖已有导航。
        /// </summary>
        public static void Register(FrameworkToolDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (Tools.TryGetValue(descriptor.Id, out var existing))
            {
                if (HasSameMetadata(existing, descriptor)) return;
                throw new InvalidOperationException(
                    $"工具 id '{descriptor.Id}' 已由“{existing.Title}”注册到 {existing.MenuPath}；" +
                    $"不能再由“{descriptor.Title}”注册到 {descriptor.MenuPath}。请为不同工具使用稳定且唯一的 id。");
            }
            Tools[descriptor.Id] = descriptor;
            Changed?.Invoke();
        }

        /// <summary>
        /// 返回当前描述符的独立数组快照，调用者可枚举但不能改变注册表；结果按分类、顺序、标题稳定排序。
        /// </summary>
        public static IReadOnlyList<FrameworkToolDescriptor> Snapshot() => Tools.Values
            .OrderBy(tool => tool.Category)
            .ThenBy(tool => tool.Order)
            .ThenBy(tool => tool.Title, StringComparer.Ordinal)
            .ToArray();

        internal static bool Unregister(string id)
        {
            if (string.IsNullOrEmpty(id) || !Tools.Remove(id)) return false;
            Changed?.Invoke();
            return true;
        }

        private static bool HasSameMetadata(FrameworkToolDescriptor left, FrameworkToolDescriptor right) =>
            left.Category == right.Category &&
            left.Order == right.Order &&
            string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
            string.Equals(left.Summary, right.Summary, StringComparison.Ordinal) &&
            string.Equals(left.MenuPath, right.MenuPath, StringComparison.Ordinal);
    }

    internal static class FrameworkBuiltInToolRegistration
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "runtime-diagnostics", FrameworkToolCategory.Diagnostics, 10,
                "运行时诊断", "查看 Context、服务注册、事件、对象池和 Command 流水；只观察，不触发业务解析。",
                FrameworkMenuPaths.RuntimeDiagnostics));
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "module-audit", FrameworkToolCategory.Diagnostics, 20,
                "模块与依赖", "解释程序集闭包、可选 Module、第三方依赖与裁剪边界，并给出下一步建议。",
                FrameworkMenuPaths.ModuleAudit));
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "build-size-probe", FrameworkToolCategory.Diagnostics, 30,
                "真实构建体积", "用隔离 Player Build 验证模块组合的真实包体影响；结果写入 Library，不改主工程资产。",
                FrameworkMenuPaths.BuildSizeProbe));
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "ai-automation-guide", FrameworkToolCategory.Diagnostics, 40,
                "AI 自动化接口说明", "解释三个机器菜单为何点击即执行、各自影响与可验证完成判据；本入口只打开说明窗口。",
                FrameworkMenuPaths.AutomationGuide));
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "project-folders", FrameworkToolCategory.Development, 20,
                "常用目录", "查看路径含义与当前解析结果，再按需在资源管理器中打开。",
                FrameworkMenuPaths.ProjectFolders));
            FrameworkToolRegistry.Register(new FrameworkToolDescriptor(
                "scene-shortcuts", FrameworkToolCategory.Development, 10,
                "场景快捷入口", "维护常用场景菜单和 Boot 启动策略；动态场景项仍保留为直接导航。",
                FrameworkMenuPaths.SceneShortcuts));
        }
    }
}
