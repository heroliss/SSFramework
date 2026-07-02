using System;
using System.Collections.Generic;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 缓存 (concreteType, layerMarkerType) → 派生自 layerMarker 的接口数组。
    /// 注册 Model/System/Utility 时需要同时把"具体类型 + 所有层接口"注册到容器，
    /// 此缓存避免每次注册都调用 Type.GetInterfaces() 并过滤。
    /// </summary>
    /// <remarks>
    /// 两层缓存：
    /// <list type="number">
    ///   <item><see cref="_interfacesCache"/>：<c>concrete → Type[]</c> 缓存 <c>GetInterfaces()</c> 全量结果。</item>
    ///   <item><see cref="_cache"/>：<c>(concrete, layer) → Type[]</c> 缓存按 layer 过滤后的结果。</item>
    /// </list>
    /// 同一 concrete 在多个 layer 下查询时，反射扫描只发生一次（第一层命中后直接走过滤）。
    /// </remarks>
    internal static class LayerInterfacesCache
    {
        // 静态缓存不加锁：与 Container 同一「主线程独占」契约（层注册只发生在 Awake / RegisterXxx 等主线程路径），
        // Editor / Development Build 下由 MainThreadGuard 兜底检测跨线程误用。
        private static readonly Dictionary<(Type concrete, Type layer), Type[]> _cache = new();
        private static readonly Dictionary<Type, Type[]> _interfacesCache = new();
        private static readonly Type[] _empty = Array.Empty<Type>();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ClearCacheOnDomainReload()
        {
            _cache.Clear();
            _interfacesCache.Clear();
        }
#endif

        /// <summary>
        /// 获取 concrete 实现的、派生自 layer 但不等于 layer 自身的所有接口。
        /// 例如 concrete=MyScoreSystem、layer=ISystem 时，返回 [IScoreSystem]（不含 ISystem 自身）。
        /// </summary>
        public static Type[] GetLayerInterfaces(Type concrete, Type layer)
        {
            var key = (concrete, layer);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            MainThreadGuard.AssertMainThread(nameof(LayerInterfacesCache));

            // 第一层缓存：concrete 的所有接口（最贵的反射只发生一次/type）
            if (!_interfacesCache.TryGetValue(concrete, out var ifaces))
            {
                ifaces = concrete.GetInterfaces();
                _interfacesCache[concrete] = ifaces;
            }

            // 第二层：按 layer 过滤
            List<Type> matched = null;
            for (int i = 0; i < ifaces.Length; i++)
            {
                var iface = ifaces[i];
                if (layer.IsAssignableFrom(iface) && iface != layer)
                {
                    matched ??= new List<Type>(4);
                    matched.Add(iface);
                }
            }

            cached = matched?.ToArray() ?? _empty;
            _cache[key] = cached;
            return cached;
        }
    }
}
