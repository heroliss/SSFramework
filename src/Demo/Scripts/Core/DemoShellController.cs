using System;
using System.Collections.Generic;
using System.Linq;
using Game.Framework.Context;
using Game.Framework.Pool;
using Game.Framework.System;
using Game.Framework.Utility;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Core
{
    /// <summary>
    /// Demo 外壳。挂在带 <see cref="UIDocument"/> 的场景节点上：
    /// 1) 反射收集本程序集所有 <see cref="IDemoModule"/>；
    /// 2) 建一个纯 C# demo <see cref="GameContext"/>（注册 <c>CommandSystem</c> / <c>PoolUtility</c> + 各模块绑定）；
    /// 3) 构建左侧导航 + 右侧内容区，处理选择与挂载。
    /// </summary>
    /// <remarks>
    /// 用纯 C# Context（不挂 MonoModelBase 等组件）：demo 由 UI Toolkit 驱动，框架层注册为普通 C# 值即可，
    /// 模块因此完全自包含（UI + 自己的层绑定 + 命令都在一个模块类里），外壳对具体主题零知识——加模块即出现。<br/>
    /// 整体风格集中在 <c>DemoTheme.uss</c>（Inspector 指定到 <see cref="_theme"/>），与各模块内容解耦。
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DemoShellController : MonoBehaviour
    {
        [SerializeField] private UIDocument _document;
        [Tooltip("整体样式表 DemoTheme.uss。")]
        [SerializeField] private StyleSheet _theme;

        private GameContext _context;
        private readonly List<IDemoModule> _modules = new();
        private readonly Dictionary<string, Button> _navButtons = new();
        private IDemoModule _current;

        private VisualElement _navList;
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
            _context?.Dispose();
        }

        // 收集模块 → 建 Context（先注册公共服务，再让各模块贡献绑定）→ 注入各模块。
        private void BuildContext()
        {
            var modules = DiscoverModules();

            var builder = new ContainerBuilder();
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            builder.RegisterValue(new PoolUtility(), new[] { typeof(IPoolUtility), typeof(IUtility) });
            foreach (var m in modules) m.InstallBindings(builder);

            _context = new GameContext(builder.Build());

            foreach (var m in modules)
            {
                m.Initialize(_context);
                _modules.Add(m);
            }
        }

        // 分类显示顺序（未列出的排到最后）。
        private static readonly string[] CategoryOrder = { "入门", "核心", "能力", "视图", "规划中" };

        private static int CategoryIndex(string category)
        {
            int i = Array.IndexOf(CategoryOrder, category);
            return i < 0 ? int.MaxValue : i;
        }

        // 反射收集本程序集中所有非抽象、带无参构造的 IDemoModule，按 分类→Order→标题 排序（由简入深）。
        private static List<IDemoModule> DiscoverModules()
        {
            var contract = typeof(IDemoModule);
            return typeof(DemoShellController).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && contract.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (IDemoModule)Activator.CreateInstance(t))
                .OrderBy(m => CategoryIndex(m.Category)).ThenBy(m => m.Order).ThenBy(m => m.Title)
                .ToList();
        }

        private void BuildUI()
        {
            var root = _document.rootVisualElement;
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
            _navList = nav.contentContainer;

            var contentScroll = new ScrollView();
            contentScroll.AddToClassList("demo-content");
            contentScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(contentScroll);
            var contentRoot = contentScroll.contentContainer;

            _headerTitle = new Label();
            _headerTitle.AddToClassList("demo-content-title");
            contentRoot.Add(_headerTitle);

            _headerSummary = new Label();
            _headerSummary.AddToClassList("demo-content-summary");
            _headerSummary.style.whiteSpace = WhiteSpace.Normal;
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
        }
    }
}
