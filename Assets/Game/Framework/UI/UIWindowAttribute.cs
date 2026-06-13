using System;
using System.Collections.Generic;

namespace Game.Framework.UI
{
    /// <summary>
    /// 关闭窗口时的处理策略。
    /// </summary>
    public enum UICachePolicy
    {
        /// <summary>关闭即销毁——释放实例与其加载的资源句柄。下次打开重新加载、重建。默认。</summary>
        Destroy = 0,

        /// <summary>关闭只隐藏、保留实例与资源在内存里，下次打开秒显（适合高频开关、构建昂贵的窗口）。由 Context 销毁时统一清理。</summary>
        Cache = 1,
    }

    /// <summary>
    /// 标注一个窗口类的元数据：落在哪一层、从哪加载资源、关闭策略、是否模态。
    /// 类型驱动（贴框架"用类型代替字符串"理念）——<c>Open&lt;TWindow&gt;()</c> 时由 <see cref="UIWindowMeta.Of"/> 读取。
    /// </summary>
    /// <remarks>
    /// <see cref="Asset"/> 的语义由 backend 解释：UGUI backend 当作窗口 prefab 的资源 location；
    /// UI Toolkit backend 当作 UXML（<c>VisualTreeAsset</c>）的 location，留空则纯代码搭建（窗口自己在 OnCreate 里建可视树）。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class UIWindowAttribute : Attribute
    {
        /// <summary>所属层。默认 <see cref="UILayer.Window"/>。</summary>
        public UILayer Layer { get; set; } = UILayer.Window;

        /// <summary>窗口资源 location（UGUI=prefab / UIToolkit=UXML）。<b>留空 = 纯代码搭建</b>：两套 backend 都支持——空 GameObject/VisualElement 由窗口在 <c>OnCreated</c> 里代码搭。</summary>
        public string Asset { get; set; }

        /// <summary>关闭策略。默认 <see cref="UICachePolicy.Destroy"/>。</summary>
        public UICachePolicy Cache { get; set; } = UICachePolicy.Destroy;

        /// <summary>是否模态：打开时在本窗口之下铺一层遮罩，拦截更下层的输入。默认 false。</summary>
        public bool Modal { get; set; }
    }

    /// <summary>
    /// 从窗口类型解析出的窗口元数据（<see cref="UIWindowAttribute"/> 的运行时快照，按类型缓存）。
    /// 没标注特性的窗口类用全默认值（Window 层 / 无资源 / Destroy / 非模态）。
    /// </summary>
    public sealed class UIWindowMeta
    {
        private static readonly Dictionary<Type, UIWindowMeta> _cache = new();

        public Type WindowType { get; private set; }
        public UILayer Layer { get; private set; }
        public string Asset { get; private set; }
        public UICachePolicy Cache { get; private set; }
        public bool Modal { get; private set; }

        /// <summary>读取并缓存某窗口类型的元数据。</summary>
        public static UIWindowMeta Of(Type windowType)
        {
            if (_cache.TryGetValue(windowType, out var meta)) return meta;

            var attr = (UIWindowAttribute)Attribute.GetCustomAttribute(windowType, typeof(UIWindowAttribute));
            meta = new UIWindowMeta
            {
                WindowType = windowType,
                Layer = attr?.Layer ?? UILayer.Window,
                Asset = attr?.Asset,
                Cache = attr?.Cache ?? UICachePolicy.Destroy,
                Modal = attr?.Modal ?? false,
            };
            _cache[windowType] = meta;
            return meta;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ClearCacheOnReload() => _cache.Clear();
#endif
    }
}
