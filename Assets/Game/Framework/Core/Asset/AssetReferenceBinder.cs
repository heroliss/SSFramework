using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Game.Framework.Logging;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.Framework.Internal
{
    /// <summary>
    /// 可被自动绑定到资源加载入口的字段标记。
    /// <see cref="AssetReference{T}"/> 和 <see cref="AssetReferenceList{T}"/> 共用这个入口。
    /// 实现 <see cref="IDisposable"/> 是为了让 bag 统一接管释放（bag.Dispose 自动调 ref.Dispose）。
    /// </summary>
    public interface IAssetReferenceBindable : IDisposable
    {
        /// <summary>
        /// 注入加载器和宿主销毁信号。utility 用来发起底层加载；hostToken 用来在宿主销毁时结束引用侧等待、
        /// 拒绝晚到结果，不承诺强停 provider 已经启动的共享物理操作。
        /// </summary>
        void Bind(IAssetUtility utility, CancellationToken hostToken);
    }

    /// <summary>
    /// AssetReference 字段扫描计划。每个类型只扫描一次，后续 BindAll 直接走缓存。
    /// 反射只看声明字段（DeclaredOnly），通过 while-loop 沿 BaseType 自己向上遍历，避免重复访问继承自父类的字段定义。
    /// </summary>
    internal sealed class AssetReferenceBindPlan
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, AssetReferenceBindPlan> _cache = new();
        private static readonly object _cacheLock = new();
        private static readonly FieldInfo[] _emptyFields = Array.Empty<FieldInfo>();

        private readonly FieldInfo[] _fields;

        public bool IsEmpty => _fields.Length == 0;

        private AssetReferenceBindPlan(FieldInfo[] fields) => _fields = fields;

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void ClearCacheOnDomainReload()
        {
            lock (_cacheLock) _cache.Clear();
        }
#endif

        public static AssetReferenceBindPlan For(Type type)
        {
            if (_cache.TryGetValue(type, out var plan)) return plan;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(type, out plan)) return plan;
                plan = Build(type);
                _cache[type] = plan;
                return plan;
            }
        }

        /// <summary>
        /// 把目标对象上所有 IAssetReferenceBindable 字段绑定到 utility 并登记到 bag。
        /// bag.Dispose 时会逐个调 ref.Dispose 释放 handle，因此 MonoBase 自身不再需要单独 DisposeAll。
        /// </summary>
        public void Bind(object target, IAssetUtility utility, CancellationToken hostToken, DisposableBag bag)
        {
            if (_fields.Length == 0) return;
            for (int i = 0; i < _fields.Length; i++)
            {
                if (_fields[i].GetValue(target) is not IAssetReferenceBindable bindable) continue;
                bindable.Bind(utility, hostToken);
                bag.Add(bindable);
            }
        }

        private static AssetReferenceBindPlan Build(Type type)
        {
            List<FieldInfo> fields = null;
            var t = type;
            while (t != null && t != typeof(object))
            {
                foreach (var field in t.GetFields(Flags))
                {
                    if (!typeof(IAssetReferenceBindable).IsAssignableFrom(field.FieldType)) continue;
                    // 只处理直接字段，不递归深入 ScriptableObject / 嵌套配置：
                    // 共享的 SO 资产不应被某个宿主的生命周期接管。
                    fields ??= new List<FieldInfo>(4);
                    fields.Add(field);
                }
                t = t.BaseType;
            }
            return new AssetReferenceBindPlan(fields?.ToArray() ?? _emptyFields);
        }
    }

    /// <summary>
    /// MonoBase Awake 时调用，把对象上的所有 AssetReference 字段绑定到加载器并登记到 bag。
    /// 不需要对应的 DisposeAll：bag.Dispose 已经会调每个 ref.Dispose。
    /// </summary>
    internal static class AssetReferenceBinder
    {
        public static void BindAll(object target, Func<IAssetUtility> utilityFactory, CancellationToken hostToken, Func<DisposableBag> bagFactory)
        {
            if (target == null || utilityFactory == null || bagFactory == null) return;
            var plan = AssetReferenceBindPlan.For(target.GetType());
            if (plan.IsEmpty) return;

            // 延迟解析 utility / bag：没有 AssetReference 字段的 MonoBase 完全不会触发资源系统初始化。
            var utility = utilityFactory();
            if (utility == null)
            {
                Log.Warning(
                    $"Skip '{target.GetType().Name}': IAssetUtility is null. " +
                    "Add AssetSystemConfigModel + AssetUtility + AssetInitSystem under the context, or call ref.Bind() manually.",
                    nameof(AssetReferenceBinder),
                    target as UnityEngine.Object);
                return;
            }
            var bag = bagFactory();
            plan.Bind(target, utility, hostToken, bag);
        }
    }
}
