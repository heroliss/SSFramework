using System;
using Game.Framework.UI.Toolkit;
using UnityEngine;

namespace Game.Framework.UI.Bridge
{
    /// <summary>刷新策略：每帧渲染，或仅在 <see cref="MonoUGuiEmbed.RequestRender"/> 时渲染一帧。</summary>
    public enum UGuiEmbedRefreshMode
    {
        /// <summary>相机每帧自动渲染——内容会动（动画 / 频繁变化）时用。</summary>
        EveryFrame,

        /// <summary>只在尺寸变化或显式 <see cref="MonoUGuiEmbed.RequestRender"/> 时渲染一帧——静态内容用，省电。</summary>
        OnDemand,
    }

    /// <summary>
    /// 一键把一段 UGUI 内容「真嵌入」进 UI Toolkit：给它一个 UGUI 面板 prefab，它自建一台隔离相机把该面板渲进
    /// <see cref="RenderTexture"/>，再交给 Toolkit 里的 <see cref="RenderTextureElement"/> 当内容显示（ADR-0033）。
    /// 与「浮层对齐」的伪嵌入不同，纹理是 Toolkit 的真内容——能被 ScrollView 裁剪 / 滚动、被后续元素遮挡。
    /// </summary>
    /// <remarks>
    /// v1 <b>只读显示</b>：事件不穿透 RenderTexture（输入转发是公认难点，留待后续）。<br/>
    /// 用法：把本组件挂在 Context 子树下的场景节点上，Inspector 配 <c>Content Prefab</c>（一段 RectTransform 面板，
    /// <b>自身不带 Canvas</b>，由本组件的托管 Canvas 承载）+ 隔离层名（该 layer 需在工程里预留，且主相机剔除它，
    /// 否则嵌入内容会同时出现在游戏画面里）；再在视图代码里 <see cref="Bind"/> 一个 <see cref="RenderTextureElement"/>。<br/>
    /// 相机拍什么由本组件装配的 <c>ScreenSpaceCamera</c> Canvas 决定；纹理尺寸随元素布局自动同步（元素上报所需设备像素）。
    /// </remarks>
    public sealed class MonoUGuiEmbed : MonoBehaviour
    {
        [Tooltip("要嵌入的 UGUI 面板 prefab：一段 RectTransform 内容（自身不带 Canvas），实例化到本组件托管的 ScreenSpaceCamera Canvas 下。")]
        [SerializeField] private GameObject _contentPrefab;

        [Tooltip("隔离剔除层名：托管 Canvas 与内容都置于此层，专用相机只拍此层。该 layer 需在工程 Tags & Layers 里预留，且主相机 cullingMask 排除它。")]
        [SerializeField] private string _isolationLayer = "UGuiEmbed";

        [Tooltip("刷新策略：内容会动用 EveryFrame；静态内容用 OnDemand（省电，靠 RequestRender 触发）。")]
        [SerializeField] private UGuiEmbedRefreshMode _refreshMode = UGuiEmbedRefreshMode.EveryFrame;

        [Tooltip("RenderTexture 单边像素上限，透传给 RenderTextureElement，避免高 DPI 大面板申请巨型显存。")]
        [SerializeField] private int _maxTextureSize = 2048;

        private GameObject _rig;              // 托管的相机 + Canvas 子树根
        private Camera _camera;
        private GameObject _content;          // 实例化出来的内容根
        private CameraTextureRenderer _renderer;
        private RenderTextureElement _element;

        /// <summary>实例化出来的内容根（供消费方驱动其动画 / 状态）；未初始化时为 <c>null</c>。</summary>
        public GameObject Content => _content;

        /// <summary>
        /// 把嵌入内容接到 Toolkit 里的显示元素：订阅其尺寸上报，据此建 / 重建等大 RenderTexture 并回填。
        /// 重复调用会先解绑上一个元素。首次调用时惰性装配相机 + Canvas + 内容实例。
        /// </summary>
        public void Bind(RenderTextureElement element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (_element == element) return;
            Unbind();
            EnsureRig();

            _element = element;
            _element.MaxTextureSize = _maxTextureSize;
            _element.DesiredPixelSizeChanged += OnDesiredSizeChanged;

            // 元素可能已完成布局（Bind 晚于首次 GeometryChanged），此时主动拉一次当前尺寸补渲。
            var size = _element.DesiredPixelSize;
            if (size.x > 0 && size.y > 0) OnDesiredSizeChanged(size.x, size.y);
        }

        /// <summary>解绑当前元素，清空它的纹理显示（相机 / 内容保留，可再次 <see cref="Bind"/>）。</summary>
        public void Unbind()
        {
            if (_element == null) return;
            _element.DesiredPixelSizeChanged -= OnDesiredSizeChanged;
            _element.SetTexture(null);
            _element = null;
        }

        /// <summary>OnDemand 模式下手动渲染一帧（内容变化后调）；EveryFrame 模式无需调用。</summary>
        public void RequestRender() => _renderer?.Render();

        private void OnDesiredSizeChanged(int width, int height)
        {
            if (_renderer == null) return;
            // 尺寸变化才重建纹理；重建后要把新纹理回填元素，并（OnDemand 下）补渲一帧让新纹理有内容。
            if (_renderer.Resize(width, height))
            {
                _element.SetTexture(_renderer.Texture);
                if (_refreshMode == UGuiEmbedRefreshMode.OnDemand) _renderer.Render();
            }
        }

        // 惰性装配：一台只拍隔离层的透明背景相机 + 一个 ScreenSpaceCamera Canvas + 实例化的内容，全部置于隔离层。
        private void EnsureRig()
        {
            if (_rig != null) return;

            int layer = ResolveLayer();

            _rig = new GameObject("[UGuiEmbed Rig]");
            _rig.transform.SetParent(transform, worldPositionStays: false);

            _camera = _rig.AddComponent<Camera>();
            _camera.orthographic = true;                       // 平面 UI，避免透视畸变
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // 透明背景，UI 空白处在纹理里透出、可与 Toolkit 合成
            _camera.cullingMask = 1 << layer;                   // 只拍隔离层，不碰场景其它内容
            _camera.enabled = _refreshMode == UGuiEmbedRefreshMode.EveryFrame; // OnDemand 靠 camera.Render() 手动触发

            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(_rig.transform, worldPositionStays: false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;   // 画布贴合相机视口 → 随 RenderTexture 尺寸自适应
            canvas.worldCamera = _camera;

            if (_contentPrefab != null)
            {
                _content = Instantiate(_contentPrefab, canvasGo.transform, worldPositionStays: false);
            }

            SetLayerRecursive(_rig, layer);
            _renderer = new CameraTextureRenderer(_camera);
        }

        // 隔离层解析：配了名就查，查不到（工程未预留该 layer）回退默认层并告警——嵌入内容会漏进主画面，属需修的配置错。
        private int ResolveLayer()
        {
            if (!string.IsNullOrEmpty(_isolationLayer))
            {
                int idx = LayerMask.NameToLayer(_isolationLayer);
                if (idx >= 0) return idx;
                Debug.LogWarning($"[MonoUGuiEmbed] 隔离层 '{_isolationLayer}' 在工程里不存在，回退默认层——嵌入内容可能漏进主画面。请在 Tags & Layers 预留该 layer 并让主相机剔除它。", this);
            }
            return gameObject.layer;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            root.layer = layer;
            var t = root.transform;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i).gameObject, layer);
        }

        private void OnDestroy()
        {
            Unbind();
            _renderer?.Dispose();   // 释放 RenderTexture（显存 + 对象）
            _renderer = null;
            if (_rig != null) Destroy(_rig);
        }
    }
}
