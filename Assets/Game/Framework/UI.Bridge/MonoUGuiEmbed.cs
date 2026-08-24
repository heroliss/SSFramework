using System;
using Game.Framework.UI.Toolkit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;

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
    /// 用法：把本组件挂在 Context 子树下的场景节点上，Inspector 配 <c>Content Prefab</c>（一段 RectTransform 面板，
    /// <b>自身不带 Canvas</b>，由本组件的托管 Canvas 承载）+ 隔离层名（该 layer 需在工程里预留，且主相机剔除它，
    /// 否则嵌入内容会同时出现在游戏画面里）；再在视图代码里 <see cref="Bind"/> 一个 <see cref="RenderTextureElement"/>。
    /// prefab 之外也可经 <see cref="EnsureContentRoot"/> 往托管 Canvas 挂 code-built / 动态 UGUI 内容。<br/>
    /// 相机拍什么由本组件装配的 <c>ScreenSpaceCamera</c> Canvas 决定；纹理尺寸随元素布局自动同步（元素上报所需设备像素）。
    /// 托管 <see cref="CanvasScaler"/> 把元素内容框作为稳定的逻辑分辨率，RenderTexture 只决定采样密度：降低纹理预算时
    /// 内容会变糊，但字体、控件和构图不会跟着按低像素重新排版。<br/>
    /// <b>输入</b>：默认只读显示；开 <c>Interactive</c> 后经 <see cref="UGuiEmbedInputForwarder"/> 把 Toolkit 指针事件
    /// 转发进嵌入 UGUI（点击 / 悬停 / 拖拽 / 滚轮，需场景有 EventSystem；文本输入 / IME、多点触控不做，ADR-0033 §v2）。
    /// </remarks>
    public sealed class MonoUGuiEmbed : MonoBehaviour
    {
        [Tooltip("要嵌入的 UGUI 面板 prefab：一段 RectTransform 内容（自身不带 Canvas），实例化到本组件托管的 ScreenSpaceCamera Canvas 下。")]
        [SerializeField] private GameObject _contentPrefab;

        [Tooltip("隔离剔除层名：托管 Canvas 与内容都置于此层，专用相机只拍此层。该 layer 需在工程 Tags & Layers 里预留，且主相机 cullingMask 排除它。")]
        [SerializeField] private string _isolationLayer = "UGuiEmbed";

        [Tooltip("刷新策略：内容会动用 EveryFrame；静态内容用 OnDemand（省电，靠 RequestRender 触发）。")]
        [SerializeField] private UGuiEmbedRefreshMode _refreshMode = UGuiEmbedRefreshMode.EveryFrame;

        [Tooltip("RenderTexture 最长边像素预算，透传给 RenderTextureElement。超出时整张等比降采样：调低只降低清晰度，不改变宽高比。")]
        [SerializeField] private int _maxTextureSize = 2048;

        [Tooltip("开启输入穿透：托管 Canvas 加 GraphicRaycaster（禁用自动注册），把 Toolkit 指针事件转发进嵌入 UGUI（点击/悬停/拖拽/滚轮）。需场景有 EventSystem。纯显示留关。")]
        [SerializeField] private bool _interactive;

        private GameObject _rig;              // 托管的相机 + Canvas 子树根
        private Camera _camera;
        private Canvas _canvas;               // 托管的 ScreenSpaceCamera Canvas（内容都挂它下）
        private CanvasScaler _canvasScaler;   // 把 Toolkit 内容框固定为 UGUI 逻辑分辨率，RT 尺寸只影响采样清晰度
        private GraphicRaycaster _raycaster;  // 仅交互模式：禁用自动注册、手动 Raycast
        private GameObject _content;          // 实例化出来的内容根（prefab 路径）
        private int _layer;                   // 解析出的隔离层
        private CameraTextureRenderer _renderer;
        private UGuiEmbedInputForwarder _input;
        private RenderTextureElement _element;
        private bool _renderAfterCanvasLayout; // OnDemand 尺寸变化延到 LateUpdate，等 CanvasScaler 先应用新参考分辨率

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
            SetLayerRecursive(_rig, _layer); // code-built 内容可能在 EnsureRig 后才挂进来，Bind 时统一补隔离层

            _element = element;
            _element.MaxTextureSize = _maxTextureSize;
            _element.DesiredPixelSizeChanged += OnDesiredSizeChanged;
            _element.RegisterCallback<GeometryChangedEvent>(OnElementGeometryChanged);

            if (_interactive) SetupInput();

            // 元素可能已完成布局（Bind 晚于首次 GeometryChanged），此时主动拉一次当前尺寸补渲。
            var size = _element.DesiredPixelSize;
            if (size.x > 0 && size.y > 0) OnDesiredSizeChanged(size.x, size.y);
        }

        /// <summary>
        /// 确保 rig 建好并返回托管 Canvas 的 <see cref="RectTransform"/>，供 code-built / 动态 UGUI 内容挂入
        /// （prefab 之外的第二条路，如运行时搭的 TMP 样本）。挂完内容后 <see cref="Bind"/> 会统一补隔离层。
        /// </summary>
        public RectTransform EnsureContentRoot()
        {
            EnsureRig();
            return _canvas.GetComponent<RectTransform>();
        }

        /// <summary>解绑当前元素，清空它的纹理显示、拆掉输入转发（相机 / 内容保留，可再次 <see cref="Bind"/>）。</summary>
        public void Unbind()
        {
            if (_element == null) return;
            _renderAfterCanvasLayout = false;
            _input?.Dispose();
            _input = null;
            _element.DesiredPixelSizeChanged -= OnDesiredSizeChanged;
            _element.UnregisterCallback<GeometryChangedEvent>(OnElementGeometryChanged);
            _element.SetTexture(null);
            _element = null;
        }

        /// <summary>OnDemand 模式下手动渲染一帧（内容变化后调）；EveryFrame 模式无需调用。</summary>
        public void RequestRender() => _renderer?.Render();

        private void LateUpdate()
        {
            if (!_renderAfterCanvasLayout) return;
            _renderAfterCanvasLayout = false;
            // CanvasScaler 在 Update 根据 targetTexture 与 referenceResolution 更新 scaleFactor；LateUpdate 再强制布局并手动渲染，
            // 避免 OnDemand 在尺寸回调当帧拍到旧逻辑分辨率的一帧错误构图。EveryFrame 相机本来就在 LateUpdate 后渲染。
            Canvas.ForceUpdateCanvases();
            _renderer?.Render();
        }

        private void OnDesiredSizeChanged(int width, int height)
        {
            if (_renderer == null) return;
            bool logicalSizeChanged = SyncCanvasReferenceResolution();
            // 尺寸变化才重建纹理；重建后要把新纹理回填元素，并（OnDemand 下）补渲一帧让新纹理有内容。
            bool textureChanged = _renderer.Resize(width, height);
            if (textureChanged)
            {
                _element.SetTexture(_renderer.Texture);
            }
            if (_refreshMode == UGuiEmbedRefreshMode.OnDemand && (logicalSizeChanged || textureChanged))
                _renderAfterCanvasLayout = true;
        }

        // 最长边预算可能让不同逻辑尺寸映射到同一个低清 RT 尺寸；因此不能只监听 DesiredPixelSizeChanged。
        // 每次几何变化都单独同步 Canvas 参考分辨率，保证 OnDemand 下“纹理尺寸没变、布局尺寸变了”也会重排并补渲。
        private void OnElementGeometryChanged(GeometryChangedEvent _)
        {
            if (!SyncCanvasReferenceResolution()) return;
            if (_refreshMode == UGuiEmbedRefreshMode.OnDemand) _renderAfterCanvasLayout = true;
        }

        // UGUI 布局使用 Toolkit 的逻辑内容框（面板点），而不是被画质预算压低后的 RT 像素数。
        // ScaleWithScreenSize 再把这份稳定布局整体缩放进目标纹理；宽高取 0.5 折中可吸收整数像素取整造成的微小比例误差。
        private bool SyncCanvasReferenceResolution()
        {
            if (_canvasScaler == null || _element == null) return false;
            var logicalSize = _element.contentRect.size;
            if (logicalSize.x <= 0f || logicalSize.y <= 0f) return false;
            if ((_canvasScaler.referenceResolution - logicalSize).sqrMagnitude < 0.0001f) return false;
            _canvasScaler.referenceResolution = logicalSize;
            return true;
        }

        // 惰性装配：一台只拍隔离层的透明背景相机 + 一个 ScreenSpaceCamera Canvas + 实例化的内容，全部置于隔离层。
        private void EnsureRig()
        {
            if (_rig != null) return;

            _layer = ResolveLayer();

            _rig = new GameObject("[UGuiEmbed Rig]");
            _rig.transform.SetParent(transform, worldPositionStays: false);

            _camera = _rig.AddComponent<Camera>();
            _camera.orthographic = true;                       // 平面 UI，避免透视畸变
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // 透明背景，UI 空白处在纹理里透出、可与 Toolkit 合成
            _camera.cullingMask = 1 << _layer;                  // 只拍隔离层，不碰场景其它内容
            _camera.enabled = _refreshMode == UGuiEmbedRefreshMode.EveryFrame; // OnDemand 靠 camera.Render() 手动触发

            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(_rig.transform, worldPositionStays: false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;  // 画布贴合相机视口 → 随 RenderTexture 尺寸自适应
            _canvas.worldCamera = _camera;
            _canvasScaler = canvasGo.AddComponent<CanvasScaler>();
            _canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            _canvasScaler.matchWidthOrHeight = 0.5f;

            if (_interactive)
            {
                // GraphicRaycaster 供转发器手动 Raycast；enabled=false 让全局 InputSystemUIInputModule 不发现它、
                // 不会拿真实鼠标坐标误射这块离屏画布（禁用只停自动注册，Raycast() 仍可手动调）。
                _raycaster = canvasGo.AddComponent<GraphicRaycaster>();
                _raycaster.enabled = false;
            }

            if (_contentPrefab != null)
            {
                _content = Instantiate(_contentPrefab, canvasGo.transform, worldPositionStays: false);
            }

            SetLayerRecursive(_rig, _layer);
            _renderer = new CameraTextureRenderer(_camera);
        }

        // 交互模式：用托管 Canvas 的 GraphicRaycaster + 场景 EventSystem 驱动指针转发器。
        private void SetupInput()
        {
            if (_raycaster == null) return;
            var es = EventSystem.current != null ? EventSystem.current : FindFirstObjectByType<EventSystem>();
            if (es == null)
            {
                Debug.LogWarning("[MonoUGuiEmbed] 开了输入穿透但场景没有 EventSystem——嵌入 UGUI 不会响应输入。", this);
                return;
            }
            _input = new UGuiEmbedInputForwarder(_element, _raycaster, es,
                () => _renderer.Texture != null ? new Vector2Int(_renderer.Texture.width, _renderer.Texture.height) : Vector2Int.zero);
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
