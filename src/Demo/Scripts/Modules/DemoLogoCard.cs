using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 资源加载章 section 2 的「屏幕空间 Logo 卡片」预制体脚本：一张可点击的 UGUI <see cref="Image"/>。
    /// 由资源系统 <c>Bag.Load&lt;GameObject&gt;</c> 从样例包加载、Instantiate 到居中的 overlay 容器；
    /// 点一下触发 <see cref="Clicked"/>，由模块销毁它——演示「资源系统不止能加载图片，也能加载整个 prefab」。
    /// 实例复用（对象池）是另一回事，见「对象池」章，这里刻意不掺进来、保持资源章聚焦。
    /// </summary>
    [RequireComponent(typeof(Image))]
    public sealed class DemoLogoCard : MonoBehaviour, IPointerClickHandler
    {
        /// <summary>被点击时触发；模块据此销毁这张卡片。</summary>
        public event Action Clicked;

        public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke();
    }
}
