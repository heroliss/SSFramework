using System;
using System.Collections.Generic;
using Game.Framework.Logging;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 外壳。挂在带 <see cref="UIDocument"/> 的节点上；共享 Context 由<b>父节点</b>的 <see cref="MonoDemoContext"/> 承载：
    /// 1) 用 <c>GetComponentInParent</c> 取父节点上已建好的共享 Context；
    /// 2) 取得根 Context 已完成 Install / Initialize 的唯一 <see cref="DemoModuleCatalog"/>；
    /// 3) 构建左侧导航 + 右侧内容区，处理选择与挂载。
    /// </summary>
    /// <remarks>
    /// UIDocument 有个固有行为：在编辑器里它会重建整棵可视树——在 Hierarchy 选中 UIDocument 所在节点、域重载等都会触发。
    /// 本 demo 的 UI 是命令式搭出来的（不来自 source VisualTreeAsset），一旦重建就被整棵冲掉。这无法关闭，
    /// 所以本类在 <c>Update</c> 里检测“根容器脱离面板”后调 <c>RebuildUI</c> 重新搭回去——这是命令式 UIDocument 内容的标准做法，不是临时补丁。<br/>
    /// 共享 Context 由<b>父节点</b>的 <see cref="MonoDemoContext"/>（一个真正的 <c>MonoGameContextBase</c>）承载、与本 UIDocument 节点分开：
    /// 让 Context 生命周期独立于视图，也避免把框架生命周期宿主与 UIDocument 堆在同一节点。
    /// 挂在 Context 节点下的 Mono 层（如 <c>MonoModelBase</c>）沿 Transform 父链自动注册进同一容器，供“纯 C# vs Mono”对比演示。<br/>
    /// 整体风格集中在 <c>DemoTheme.uss</c>（Inspector 指定到 <see cref="_theme"/>），与各模块内容解耦。
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DemoShellController : MonoBehaviour
    {
        // 小于这个宽度时，固定左栏会吞掉大部分正文空间；切成“上导航、下正文”比继续压缩两列更可读。
        private const float NarrowLayoutWidth = 780f;

        [SerializeField] private UIDocument _document;
        [Tooltip("整体样式表 DemoTheme.uss。")]
        [SerializeField] private StyleSheet _theme;

        private DemoModuleCatalog _moduleCatalog;
        private IReadOnlyList<IDemoModule> _modules = Array.Empty<IDemoModule>();
        private readonly Dictionary<string, Button> _navButtons = new();
        private IDemoModule _current;

        private VisualElement _navList;
        private ScrollView _navScroll;
        private ScrollView _contentScroll;
        private VisualElement _contentArea;
        private Label _headerProgress;
        private Label _headerTitle;
        private Label _headerSummary;
        private Button _previousChapterButton;
        private Button _nextChapterButton;
        private Label _chapterNavigationHint;

        /// <summary>真实场景冒烟测试使用的稳定章节 Interface；不暴露可变集合。</summary>
        internal IReadOnlyList<IDemoModule> Modules => _modules;

        /// <summary>当前已成功构建的章节；教学契约或 Build 失败时会回到 null。</summary>
        internal IDemoModule CurrentModule => _current;

        private void Awake()
        {
            if (_document == null) _document = GetComponent<UIDocument>();
            if (!BuildContext())
                enabled = false;
        }

        private void Start()
        {
            // rootVisualElement 在 UIDocument 启用后才可用；Start 早于首帧、晚于所有 OnEnable，时机正好。
            BuildUI();
            if (_modules.Count > 0) SelectChapter(_modules[0]);
        }

        private void OnDestroy()
        {
            var root = _document != null ? _document.rootVisualElement : null;
            root?.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            ReleaseCurrentModule();
            // 不 Dispose _context：它由 MonoDemoContext（MonoGameContextBase）的生命周期负责释放。
        }

        // 检测 UIDocument 重建（见类 remarks）：根导航脱离面板即说明可视树被重建过，把命令式 UI 重新搭回去。
        // 代价是每帧一次引用判空，可忽略；这是命令式 UIDocument 内容跨重建存活的标准做法。
        private void Update()
        {
            if (_navList != null && _navList.panel == null)
                RebuildUI();
        }

        // 重建整棵 UI 并恢复当前选中的模块。先 Teardown 旧模块，释放它挂在已失效 UI 上的订阅/异步，避免重复订阅。
        private void RebuildUI()
        {
            var current = _current;
            ReleaseCurrentModule(); // 同时置空，避免 Select 因“同一模块”短路而不重建内容
            BuildUI();
            if (current != null) SelectChapter(current);
            else if (_modules.Count > 0) SelectChapter(_modules[0]);
        }

        // 从父节点的 MonoDemoContext 取已建好的共享 Context 与唯一目录。章节 Adapter 已由 Context 在更早的
        // ExecutionOrder 中完成 Install + Initialize；Shell 不再反射构造第二批实例。
        private bool BuildContext()
        {
            var contextHost = GetComponentInParent<MonoDemoContext>();
            if (contextHost == null)
                throw new InvalidOperationException(
                    "[DemoShellController] 父链上缺少 MonoDemoContext——它承载共享 Context，应挂在本节点的父节点（DemoApp）上。");
            if (contextHost.IsDisposed)
            {
                // 根 Context 已经输出带 inner exception 的唯一根因；外壳不再继续构建章节并制造 Resolve/NRE 洪泛。
                Log.Warning(
                    "Demo root Context is unavailable, so the UI shell has been disabled. Fix the earlier Context initialization exception and enter Play again.",
                    "DemoShell",
                    this);
                return false;
            }
            _moduleCatalog = contextHost.ModuleCatalog;
            _modules = _moduleCatalog.Modules;
            return true;
        }

        private void BuildUI()
        {
            var root = _document.rootVisualElement;
            if (root == null) return; // UIDocument 重建过程中可能瞬时为 null，下一帧 Update 会再触发
            root.Clear();
            if (_theme != null) root.styleSheets.Add(_theme);
            root.AddToClassList("demo-root");
            root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            ApplyResponsiveLayout(root.resolvedStyle.width);

            var titleBar = new Label("SSFramework · 框架功能演示");
            titleBar.AddToClassList("demo-app-title");
            root.Add(titleBar);

            var body = new VisualElement();
            body.AddToClassList("demo-body");
            root.Add(body);

            var nav = new ScrollView();
            nav.AddToClassList("demo-nav");
            nav.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(nav);
            _navScroll = nav;
            _navList = nav.contentContainer;

            _contentScroll = new ScrollView();
            _contentScroll.AddToClassList("demo-content");
            _contentScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(_contentScroll);
            var contentRoot = _contentScroll.contentContainer;

            _headerProgress = new Label();
            _headerProgress.AddToClassList("demo-content-progress");
            _headerProgress.enableRichText = false;
            contentRoot.Add(_headerProgress);

            _headerTitle = new Label();
            _headerTitle.AddToClassList("demo-content-title");
            _headerTitle.enableRichText = false;
            contentRoot.Add(_headerTitle);

            _headerSummary = new Label();
            _headerSummary.AddToClassList("demo-content-summary");
            _headerSummary.style.whiteSpace = WhiteSpace.Normal;
            _headerSummary.enableRichText = false; // 简介里可能出现 RP<T> 等泛型，关掉富文本免得 <T> 被当标签吞掉
            contentRoot.Add(_headerSummary);

            _contentArea = new VisualElement();
            _contentArea.AddToClassList("demo-content-body");
            contentRoot.Add(_contentArea);

            var chapterNavigation = new VisualElement();
            chapterNavigation.AddToClassList("demo-chapter-navigation");
            contentRoot.Add(chapterNavigation);

            _previousChapterButton = new Button(() => SelectRelativeChapter(-1));
            _previousChapterButton.AddToClassList("demo-chapter-navigation-button");
            chapterNavigation.Add(_previousChapterButton);

            _chapterNavigationHint = new Label();
            _chapterNavigationHint.AddToClassList("demo-chapter-navigation-hint");
            chapterNavigation.Add(_chapterNavigationHint);

            _nextChapterButton = new Button(() => SelectRelativeChapter(1));
            _nextChapterButton.AddToClassList("demo-chapter-navigation-button");
            chapterNavigation.Add(_nextChapterButton);

            BuildNav();
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => ApplyResponsiveLayout(evt.newRect.width);

        private void ApplyResponsiveLayout(float width)
        {
            var root = _document != null ? _document.rootVisualElement : null;
            if (root == null || float.IsNaN(width) || width <= 0f) return;
            root.EnableInClassList("demo-root--narrow", width < NarrowLayoutWidth);
        }

        private void BuildNav()
        {
            _navButtons.Clear();
            string lastCategory = null;
            foreach (var m in _modules)
            {
                if (m.Category != lastCategory)
                {
                    lastCategory = m.Category;
                    var cat = new Label(lastCategory);
                    cat.AddToClassList("demo-nav-category");
                    _navList.Add(cat);
                }

                var module = m; // 闭包捕获
                var btn = new Button(() => SelectChapter(module)) { text = m.Title };
                btn.AddToClassList("demo-nav-item");
                btn.tooltip = BuildChapterTooltip(m);
                if (m.IsComingSoon) btn.AddToClassList("demo-nav-item--soon");
                _navList.Add(btn);
                _navButtons[m.Id] = btn;
            }
        }

        /// <summary>
        /// 通过外壳切换章节的唯一入口：同步标题、导航、内容与目录生命周期。
        /// 真实场景测试调用它，避免绕过 Shell 直接操作目录后留下状态分叉。
        /// </summary>
        internal void SelectChapter(IDemoModule module)
        {
            if (_current == module) return;
            ReleaseCurrentModule();
            _current = module;

            foreach (var kv in _navButtons)
                kv.Value.EnableInClassList("demo-nav-item--active", kv.Key == module.Id);

            _headerTitle.text = module.Title;
            _headerSummary.text = module.Summary;
            UpdateChapterNavigation(module);
            _contentArea.Clear();
            try
            {
                _moduleCatalog.Activate(module, _contentArea);
            }
            catch
            {
                // Build 可能已注册订阅或启动异步按钮；失败也必须走与切章相同的所有权出口，不能留下半章资源。
                ReleaseCurrentModule();
                throw;
            }

            // 每章独立的滚动位置：换章回到顶部，不继承上一章滚到的位置（否则新章一进来就停在半中间）。
            // 直接置 0 立即生效；再调度一帧兜底——内容布局在本帧末才算完，个别情况即时设的偏移会被布局后的钳制覆盖。
            _contentScroll.scrollOffset = Vector2.zero;
            _contentScroll.schedule.Execute(() => _contentScroll.scrollOffset = Vector2.zero);

            // 导航滚到选中项（延一帧等布局）：用户点击时按钮本就可见，这是给「UI 重建后恢复选中」兜底——
            // 重建让导航回到顶部，恢复的选中章可能在可视区外。ScrollTo 只滚最小距离，可见时是空操作。
            if (_navButtons.TryGetValue(module.Id, out var navBtn))
                _navScroll.schedule.Execute(() => _navScroll.ScrollTo(navBtn));
        }

        // 所有离开当前章节的路径都走同一出口；目录集中保证先取消 Host 异步按钮，再释放模块 Bag。
        private void ReleaseCurrentModule()
        {
            try
            {
                _moduleCatalog?.Deactivate();
            }
            finally
            {
                _current = null;
            }
        }

        private void SelectRelativeChapter(int offset)
        {
            int currentIndex = IndexOfModule(_current);
            int targetIndex = currentIndex + offset;
            if (currentIndex >= 0 && targetIndex >= 0 && targetIndex < _modules.Count)
                SelectChapter(_modules[targetIndex]);
        }

        private void UpdateChapterNavigation(IDemoModule module)
        {
            int absoluteIndex = IndexOfModule(module);
            int groupCount = 0;
            int groupIndex = -1;
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i].Category != module.Category) continue;
                if (ReferenceEquals(_modules[i], module)) groupIndex = groupCount;
                groupCount++;
            }

            _headerProgress.text = $"{module.Category} · 本组 {groupIndex + 1}/{groupCount} · 全部 {absoluteIndex + 1}/{_modules.Count}";
            _chapterNavigationHint.text = module.Category is "入门" or "核心"
                ? "推荐按目录顺序学习"
                : "本组章节相互独立，可按需跳转";

            bool hasPrevious = absoluteIndex > 0;
            _previousChapterButton.SetEnabled(hasPrevious);
            _previousChapterButton.text = hasPrevious ? $"← {_modules[absoluteIndex - 1].Title}" : "← 已到起点";
            _previousChapterButton.tooltip = hasPrevious
                ? BuildChapterTooltip(_modules[absoluteIndex - 1])
                : "已经是全部教程的第一章";

            bool hasNext = absoluteIndex >= 0 && absoluteIndex < _modules.Count - 1;
            _nextChapterButton.SetEnabled(hasNext);
            _nextChapterButton.text = hasNext ? $"{_modules[absoluteIndex + 1].Title} →" : "已到终点 →";
            _nextChapterButton.tooltip = hasNext
                ? BuildChapterTooltip(_modules[absoluteIndex + 1])
                : "已经是全部教程的最后一章";
        }

        private static string BuildChapterTooltip(IDemoModule module)
            => string.IsNullOrWhiteSpace(module.Summary)
                ? module.Title
                : module.Title + "\n\n" + module.Summary;

        private int IndexOfModule(IDemoModule module)
        {
            for (int i = 0; i < _modules.Count; i++)
                if (ReferenceEquals(_modules[i], module)) return i;
            return -1;
        }
    }
}
