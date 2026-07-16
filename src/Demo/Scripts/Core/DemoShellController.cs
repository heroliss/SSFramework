using System;
using System.Collections.Generic;
using System.Linq;
using Game.Framework;
using Game.Framework.Context;
using Game.Framework.Internal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 外壳。挂在带 <see cref="UIDocument"/> 的节点上；共享 Context 由<b>父节点</b>的 <see cref="MonoDemoContext"/> 承载：
    /// 1) 用 <c>GetComponentInParent</c> 取父节点上已建好的共享 Context；
    /// 2) 反射收集本程序集所有 <see cref="IDemoModule"/> 并注入该 Context；
    /// 3) 构建左侧导航 + 右侧内容区，处理选择与挂载。
    /// </summary>
    /// <remarks>
    /// UIDocument 有个固有行为：在编辑器里它会重建整棵可视树——在 Hierarchy 选中 UIDocument 所在节点、域重载等都会触发。
    /// 本 demo 的 UI 是命令式搭出来的（不来自 source VisualTreeAsset），一旦重建就被整棵冲掉。这无法关闭，
    /// 所以本类在 <c>Update</c> 里检测“根容器脱离面板”后调 <c>RebuildUI</c> 重新搭回去——这是命令式 UIDocument 内容的标准做法，不是临时补丁。<br/>
    /// 共享 Context 由<b>父节点</b>的 <see cref="MonoDemoContext"/>（一个真正的 <c>MonoGameContextBase</c>）承载、与本 UIDocument 节点分开：
    /// 让 Context 生命周期独立于视图，也避免把 Odin <c>SerializedMonoBehaviour</c> 与 UIDocument 堆在同一节点。
    /// 挂在 Context 节点下的 Mono 层（如 <c>MonoModelBase</c>）沿 Transform 父链自动注册进同一容器，供“纯 C# vs Mono”对比演示。<br/>
    /// 整体风格集中在 <c>DemoTheme.uss</c>（Inspector 指定到 <see cref="_theme"/>），与各模块内容解耦。
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DemoShellController : MonoBehaviour
    {
        [SerializeField] private UIDocument _document;
        [Tooltip("整体样式表 DemoTheme.uss。")]
        [SerializeField] private StyleSheet _theme;

        private IGameContext _context;
        private readonly List<IDemoModule> _modules = new();
        private readonly Dictionary<string, Button> _navButtons = new();
        private IDemoModule _current;

        private VisualElement _navList;
        private ScrollView _navScroll;
        private ScrollView _contentScroll;
        private VisualElement _contentArea;
        private Label _headerTitle;
        private Label _headerSummary;

        private void Awake()
        {
            if (_document == null) _document = GetComponent<UIDocument>();
            BuildContext();
        }

        private void Start()
        {
            // rootVisualElement 在 UIDocument 启用后才可用；Start 早于首帧、晚于所有 OnEnable，时机正好。
            BuildUI();
            if (_modules.Count > 0) Select(_modules[0]);
        }

        private void OnDestroy()
        {
            _current?.Teardown();
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
            _current?.Teardown();
            _current = null; // 置空，避免 Select 因“同一模块”短路而不重建内容
            BuildUI();
            if (current != null) Select(current);
            else if (_modules.Count > 0) Select(_modules[0]);
        }

        // 从父节点的 MonoDemoContext 取已建好的共享 Context（它在更早的 ExecutionOrder 里注册了公共服务 + 各模块绑定，
        // 并自动收纳挂在它下面的 Mono 层），再把各模块注入这个 Context。
        private void BuildContext()
        {
            var contextHost = GetComponentInParent<MonoDemoContext>();
            if (contextHost == null)
                throw new InvalidOperationException(
                    "[DemoShellController] 父链上缺少 MonoDemoContext——它承载共享 Context，应挂在本节点的父节点（DemoApp）上。");
            _context = contextHost;

            foreach (var m in DiscoverModules())
            {
                m.Initialize(_context);
                _modules.Add(m);
            }
        }

        // 分类显示顺序（未列出的排到最后）。
        private static readonly string[] CategoryOrder = { "入门", "核心", "能力", "进阶", "规划中" };

        private static int CategoryIndex(string category)
        {
            int i = Array.IndexOf(CategoryOrder, category);
            return i < 0 ? int.MaxValue : i;
        }

        // 反射收集本程序集中所有非抽象、带无参构造的 IDemoModule，按 分类→Order→标题 排序（由简入深）。
        internal static List<IDemoModule> DiscoverModules()
        {
            var contract = typeof(IDemoModule);
            var modules = typeof(DemoShellController).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && contract.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (IDemoModule)Activator.CreateInstance(t))
                .OrderBy(m => CategoryIndex(m.Category)).ThenBy(m => m.Order).ThenBy(m => m.Title)
                .ToList();

            // 同组 Order 撞号时顺序退化为标题字符串比较（文化相关、跨机器不稳定），章节编排意图会被打乱。
            for (int i = 1; i < modules.Count; i++)
                if (modules[i].Category == modules[i - 1].Category && modules[i].Order == modules[i - 1].Order)
                    Debug.LogWarning($"[DemoShell] 「{modules[i].Category}」组内 Order 撞号（{modules[i].Order}）：" +
                                     $"「{modules[i - 1].Title}」与「{modules[i].Title}」——请错开 Order 以固定导航顺序。");

            return modules;
        }

        private void BuildUI()
        {
            var root = _document.rootVisualElement;
            if (root == null) return; // UIDocument 重建过程中可能瞬时为 null，下一帧 Update 会再触发
            root.Clear();
            if (_theme != null) root.styleSheets.Add(_theme);
            root.AddToClassList("demo-root");

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

            BuildNav();
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
                var btn = new Button(() => Select(module)) { text = m.Title };
                btn.AddToClassList("demo-nav-item");
                if (m.IsComingSoon) btn.AddToClassList("demo-nav-item--soon");
                _navList.Add(btn);
                _navButtons[m.Id] = btn;
            }
        }

        private void Select(IDemoModule module)
        {
            if (_current == module) return;
            _current?.Teardown();
            _current = module;

            foreach (var kv in _navButtons)
                kv.Value.EnableInClassList("demo-nav-item--active", kv.Key == module.Id);

            _headerTitle.text = module.Title;
            _headerSummary.text = module.Summary;
            _contentArea.Clear();
            module.Build(new DemoModuleHost(_contentArea));

            // 每章独立的滚动位置：换章回到顶部，不继承上一章滚到的位置（否则新章一进来就停在半中间）。
            // 直接置 0 立即生效；再调度一帧兜底——内容布局在本帧末才算完，个别情况即时设的偏移会被布局后的钳制覆盖。
            _contentScroll.scrollOffset = Vector2.zero;
            _contentScroll.schedule.Execute(() => _contentScroll.scrollOffset = Vector2.zero);

            // 导航滚到选中项（延一帧等布局）：用户点击时按钮本就可见，这是给「UI 重建后恢复选中」兜底——
            // 重建让导航回到顶部，恢复的选中章可能在可视区外。ScrollTo 只滚最小距离，可见时是空操作。
            if (_navButtons.TryGetValue(module.Id, out var navBtn))
                _navScroll.schedule.Execute(() => _navScroll.ScrollTo(navBtn));
        }
    }
}
