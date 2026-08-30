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
            Type[] interfaces = LayerInterfacesCache.GetLayerInterfaces(concreteType, typeof(TLayer));

            // 一个层对象会同时占用“具体类型 + 多个层 Interface”。必须先检查完整 contract 集，
            // 否则后面的共享 Interface 冲突时，前面已经写入的具体类型会成为一次失败注册的幽灵残留。
            EnsureOverrideCanCommit(container, concreteType, label);
            for (int i = 0; i < interfaces.Length; i++)
                EnsureOverrideCanCommit(container, interfaces[i], label);

            // Container 主线程独占；预检与下面提交之间没有并发写入，也没有用户回调。
            container.ReplaceOverride(concreteType, instance);
            for (int i = 0; i < interfaces.Length; i++)
                container.ReplaceOverride(interfaces[i], instance);

            Log.Trace($"[Container] 注册 {concreteType.Name}：{label}");
            for (int i = 0; i < interfaces.Length; i++)
                Log.Trace($"[Container] 注册 {interfaces[i].Name}：{label}");
        }

        /// <summary>取消注册实例。仅当值匹配时才移除，避免误删同名类型的新注册。</summary>
        public static void UnregisterFor<TLayer>(this Container container, object instance) where TLayer : class
        {
            var concreteType = instance.GetType();
            container.RemoveOverride(concreteType, instance);
            foreach (var iface in LayerInterfacesCache.GetLayerInterfaces(concreteType, typeof(TLayer)))
                container.RemoveOverride(iface, instance);
        }

        /// <summary>预检单个运行时覆盖键；完整 contract 集全部通过后才由调用方统一提交。</summary>
        private static void EnsureOverrideCanCommit(
            Container container,
            Type contractType,
            string label)
        {
            if (container.TryGetOverride(contractType, out var existing))
            {
                if (existing is UnityEngine.Object uObj && uObj == null)
                    return;

                throw new InvalidOperationException(
                    $"[Container] 契约 '{contractType.Name}' 重复注册：" +
                    $"'{label}' 与已注册的 '{existing.GetType().Name}' 冲突。");
            }
        }
    }
}
