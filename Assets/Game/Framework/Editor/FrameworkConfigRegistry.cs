using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Framework.Editor
{
    /// <summary>
    /// 一个 Editor Module 向配置中心贡献的资产配置描述。描述符保存真实 <see cref="Type"/>，因此中央窗口
    /// 不需要维护可选 Module 的程序集限定类型名；Module 被删除后，其注册和卡片会在下次域重载后一同消失。
    /// </summary>
    public sealed class FrameworkConfigDescriptor
    {
        /// <summary>创建一张配置资产卡片；主类型和附属类型都必须是具体 <see cref="ScriptableObject"/>。</summary>
        public FrameworkConfigDescriptor(
            string id,
            int order,
            string title,
            Type profileType,
            bool singleton,
            string note,
            string menuPath,
            string menuLabel = "打开工作台",
            Type secondaryProfileType = null,
            string secondaryLabel = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("配置 id 不能为空。", nameof(id));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("配置标题不能为空。", nameof(title));
            ValidateProfileType(profileType, nameof(profileType));
            if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("配置说明不能为空。", nameof(note));
            if (string.IsNullOrWhiteSpace(menuPath)) throw new ArgumentException("工作台菜单路径不能为空。", nameof(menuPath));
            if (string.IsNullOrWhiteSpace(menuLabel)) throw new ArgumentException("工作台按钮文字不能为空。", nameof(menuLabel));
            if (secondaryProfileType != null)
            {
                ValidateProfileType(secondaryProfileType, nameof(secondaryProfileType));
                if (string.IsNullOrWhiteSpace(secondaryLabel))
                    throw new ArgumentException("登记附属配置类型时必须提供显示名称。", nameof(secondaryLabel));
            }
            else if (!string.IsNullOrWhiteSpace(secondaryLabel))
            {
                throw new ArgumentException("没有附属配置类型时不能单独提供显示名称。", nameof(secondaryLabel));
            }

            Id = id;
            Order = order;
            Title = title;
            ProfileType = profileType;
            Singleton = singleton;
            Note = note;
            MenuPath = menuPath;
            MenuLabel = menuLabel;
            SecondaryProfileType = secondaryProfileType;
            SecondaryLabel = secondaryLabel;
        }

        /// <summary>跨域重载稳定、跨 Module 唯一的配置身份。</summary>
        public string Id { get; }
        /// <summary>配置中心的稳定显示顺序；相同时再按标题排序。</summary>
        public int Order { get; }
        /// <summary>配置用途标题。</summary>
        public string Title { get; }
        /// <summary>由当前 Module 拥有的配置资产类型。</summary>
        public Type ProfileType { get; }
        /// <summary><c>true</c> 表示全工程应只有一份，多份时配置中心会明确警告。</summary>
        public bool Singleton { get; }
        /// <summary>数量语义、创建方式和关键前置条件。</summary>
        public string Note { get; }
        /// <summary>所属 Module 工作台的菜单路径；配置中心只导航，不执行生成或构建。</summary>
        public string MenuPath { get; }
        /// <summary>导航按钮文字。</summary>
        public string MenuLabel { get; }
        /// <summary>需要在同一卡片列出的附属配置类型，例如 UI 目录级覆盖；没有时为 <c>null</c>。</summary>
        public Type SecondaryProfileType { get; }
        /// <summary>附属配置类型的显示名称；没有附属类型时为 <c>null</c>。</summary>
        public string SecondaryLabel { get; }

        private static void ValidateProfileType(Type type, string parameterName)
        {
            if (type == null) throw new ArgumentNullException(parameterName);
            if (!typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
                throw new ArgumentException($"配置类型必须是具体 ScriptableObject：{type.FullName}。", parameterName);
        }
    }

    /// <summary>
    /// 可选 Editor Module 与通用配置中心之间的窄 Seam。配置知识由拥有 Profile 的 Module 登记，中央窗口只消费
    /// 稳定快照；相同 id + 相同元数据可安全重入，同 id 的不同配置会直接失败，避免后加载 Module 静默覆盖卡片。
    /// </summary>
    public static class FrameworkConfigRegistry
    {
        private static readonly Dictionary<string, FrameworkConfigDescriptor> Configurations =
            new(StringComparer.Ordinal);

        /// <summary>配置集合发生真实增删时触发；完全相同的重入注册不会触发。</summary>
        public static event Action Changed;

        /// <summary>登记当前 Module 拥有的一类配置资产。</summary>
        public static void Register(FrameworkConfigDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (Configurations.TryGetValue(descriptor.Id, out var existing))
            {
                if (HasSameMetadata(existing, descriptor)) return;
                throw new InvalidOperationException(
                    $"配置 id '{descriptor.Id}' 已由“{existing.Title}”注册为 {existing.ProfileType.FullName}；" +
                    $"不能再由“{descriptor.Title}”注册为 {descriptor.ProfileType.FullName}。请为不同配置使用稳定且唯一的 id。");
            }

            Configurations[descriptor.Id] = descriptor;
            Changed?.Invoke();
        }

        /// <summary>返回独立、按顺序与标题稳定排列的数组快照。</summary>
        public static IReadOnlyList<FrameworkConfigDescriptor> Snapshot() => Configurations.Values
            .OrderBy(configuration => configuration.Order)
            .ThenBy(configuration => configuration.Title, StringComparer.Ordinal)
            .ToArray();

        internal static bool Unregister(string id)
        {
            if (string.IsNullOrEmpty(id) || !Configurations.Remove(id)) return false;
            Changed?.Invoke();
            return true;
        }

        private static bool HasSameMetadata(FrameworkConfigDescriptor left, FrameworkConfigDescriptor right) =>
            left.Order == right.Order &&
            string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
            left.ProfileType == right.ProfileType &&
            left.Singleton == right.Singleton &&
            string.Equals(left.Note, right.Note, StringComparison.Ordinal) &&
            string.Equals(left.MenuPath, right.MenuPath, StringComparison.Ordinal) &&
            string.Equals(left.MenuLabel, right.MenuLabel, StringComparison.Ordinal) &&
            left.SecondaryProfileType == right.SecondaryProfileType &&
            string.Equals(left.SecondaryLabel, right.SecondaryLabel, StringComparison.Ordinal);
    }
}
