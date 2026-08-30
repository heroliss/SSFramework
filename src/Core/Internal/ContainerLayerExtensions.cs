using System;
using Game.Framework.Logging;
using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 一次运行时分层注册的不可变准备结果。准备阶段只计算契约并预检，不公开实例；用户初始化完成后再
    /// <see cref="ContainerLayerExtensions.CommitRegistration"/>，让 Mono 层不会在 [Inject] 回调期间被解析到。
    /// </summary>
    internal readonly struct LayerRegistrationPlan
    {
        internal Container Container { get; }
        internal object Instance { get; }
        internal Type ConcreteType { get; }
        internal Type[] Interfaces { get; }
        internal string Label { get; }

        internal LayerRegistrationPlan(
            Container container,
            object instance,
            Type concreteType,
            Type[] interfaces,
            string label)
        {
            Container = container;
            Instance = instance;
            ConcreteType = concreteType;
            Interfaces = interfaces;
            Label = label;
        }
    }

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
            var plan = container.PrepareRegistrationFor<TLayer>(instance, label);
            CommitRegistration(plan);
            TraceRegistration(plan);
        }

        /// <summary>
        /// 计算“具体类型 + 层 Interface”并预检完整集合；不写 Container。Mono 初始化在任何用户回调前调用，
        /// 回调结束后仍须经 <see cref="CommitRegistration"/> 再次预检，防止重入注册造成竞态式覆盖。
        /// </summary>
        internal static LayerRegistrationPlan PrepareRegistrationFor<TLayer>(
            this Container container,
            object instance,
            string label) where TLayer : class
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            EnsureUnityObjectAlive(instance, label);

            var concreteType = instance.GetType();
            LayerInterfacesCache.ValidateSingleLayer(
                concreteType,
                typeof(TLayer),
                nameof(Container),
                nameof(instance));
            var plan = new LayerRegistrationPlan(
                container,
                instance,
                concreteType,
                LayerInterfacesCache.GetLayerInterfaces(concreteType, typeof(TLayer)),
                label);
            EnsureRegistrationCanCommit(plan);
            return plan;
        }

        /// <summary>
        /// 在无用户回调的短临界段内复检并一次写入全部契约。日志刻意不在这里触发，调用方可先记录
        /// 自己已经提交，再转发到可替换 Log sink，避免同步重入销毁时误判注册状态。
        /// </summary>
        internal static void CommitRegistration(LayerRegistrationPlan plan)
        {
            EnsureUnityObjectAlive(plan.Instance, plan.Label);
            EnsureRegistrationCanCommit(plan);

            plan.Container.ReplaceOverride(plan.ConcreteType, plan.Instance);
            for (int i = 0; i < plan.Interfaces.Length; i++)
                plan.Container.ReplaceOverride(plan.Interfaces[i], plan.Instance);
        }

        /// <summary>提交完成后的可观察 Trace；不参与注册原子性。</summary>
        internal static void TraceRegistration(LayerRegistrationPlan plan)
        {
            Log.Trace($"[Container] 注册 {plan.ConcreteType.Name}：{plan.Label}");
            for (int i = 0; i < plan.Interfaces.Length; i++)
                Log.Trace($"[Container] 注册 {plan.Interfaces[i].Name}：{plan.Label}");
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

        private static void EnsureRegistrationCanCommit(LayerRegistrationPlan plan)
        {
            EnsureOverrideCanCommit(plan.Container, plan.ConcreteType, plan.Label);
            for (int i = 0; i < plan.Interfaces.Length; i++)
                EnsureOverrideCanCommit(plan.Container, plan.Interfaces[i], plan.Label);
        }

        private static void EnsureUnityObjectAlive(object instance, string label)
        {
            if (instance is UnityEngine.Object unityObject && unityObject == null)
                throw new MissingReferenceException(
                    $"[Container] 无法注册已销毁的 Unity 对象：{label ?? instance.GetType().Name}。");
        }
    }
}
