using System;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// Container 运行时层级注册扩展。
    /// Model/System/Utility 的动态注册统一走这里，Mono 与纯 C# 路径共享同一套重复检测和反注册语义。
    /// </summary>
    internal static class ContainerLayerExtensions
    {
        /// <summary>
        /// 注册实例的具体类型 + 所有派生自 TLayer 的接口（不含 TLayer 自身）。
        /// </summary>
        public static void RegisterFor<TLayer>(this Container container, object instance, string label) where TLayer : class
        {
            var concreteType = instance.GetType();
            container.RegisterOverride(concreteType, instance, label);
            foreach (var iface in LayerInterfacesCache.GetLayerInterfaces(concreteType, typeof(TLayer)))
                container.RegisterOverride(iface, instance, label);
        }

        /// <summary>取消注册实例。仅当值匹配时才移除，避免误删同名类型的新注册。</summary>
        public static void UnregisterFor<TLayer>(this Container container, object instance) where TLayer : class
        {
            var concreteType = instance.GetType();
            container.RemoveOverride(concreteType, instance);
            foreach (var iface in LayerInterfacesCache.GetLayerInterfaces(concreteType, typeof(TLayer)))
                container.RemoveOverride(iface, instance);
        }

        /// <summary>
        /// 注册运行时覆盖项。同层同契约重复注册会抛异常；已销毁的 UnityEngine.Object 允许替换。
        /// </summary>
        public static void RegisterOverride(this Container container, Type contractType, object instance, string label)
        {
            if (container.TryGetOverride(contractType, out var existing))
            {
                if (existing is UnityEngine.Object uObj && uObj == null)
                {
                    container.ReplaceOverride(contractType, instance);
                    Log.Trace($"[Container] 替换 {contractType.Name}：{label}（旧实例已销毁）");
                    return;
                }

                throw new InvalidOperationException(
                    $"[Container] 契约 '{contractType.Name}' 重复注册：" +
                    $"'{label}' 与已注册的 '{existing.GetType().Name}' 冲突。");
            }

            container.ReplaceOverride(contractType, instance);
            // 容器每注册一次就走这里：插值处理器让 Verbose 关时连字符串都不拼（旧的 LogVerbose(string) 是先拼好再丢弃）。
            Log.Trace($"[Container] 注册 {contractType.Name}：{label}");
        }
    }
}
