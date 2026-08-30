using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using Game.Framework.UI.Toolkit;
using ObservableCollections;
using R3;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·响应式列表：集合状态用 <c>ObservableList&lt;T&gt;</c> 持有，UI 用 <c>Bag.BindList</c> 增量绑定——
    /// 增删移换只动变化的那一项，不整表重建。补 <c>RP&lt;T&gt;</c> 单值订阅覆盖不到的列表空缺。
    /// </summary>
    /// <remarks>
    /// 焦点是「一个源集合 → 一个跟随的列表视图」这条数据流：每个按钮做一次原子集合操作，
    /// 下方列表即时反映；每行自带一个 ✕（用行专属子 bag 接点击，行被移除时订阅自动退订）。
    /// </remarks>
    public sealed class ReactiveListModule : DemoModuleBase
    {
        public override string Id => "reactive-list";
        public override string Title => "响应式列表 · 集合绑定";
        public override string Category => "能力";
        public override int Order => 71; // 紧跟「UI 框架 · 窗口/层级」(70)：先有窗口，再看怎么把活集合绑进去

        public override string Summary =>
            "集合状态（背包 / 聊天 / 排行榜）用 ObservableList<T> 持有，UI 用 Bag.BindList 增量绑定：只动变化那一项、不整表重建。" +
            "每行独享子 bag，行内订阅随行移除自动退订。";

        public override void InstallBindings(ContainerBuilder builder)
            => builder.RegisterModel(new TodoBoardModel());

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("会增删的集合用 ObservableList + BindList 增量绑定");
            host.AddNote(
                "单个值用 `RP<T>` + `BindText`。可是「一串会增删的东西」（背包格子、聊天记录、在线列表）是**集合**——"
                + "若塞进 `RP<List>` 整包推送，View 每次都得清空重建整张列表，丢滚动/选中、还抖 GC。"
                + "改用 `ObservableList<T>` 持有集合，`Bag.BindList` 订阅它的增删移换、只增量维护对应子视图。",
                CodeRef.Here("Bag.BindList(listContainer", "BindList 调用点"));

            // View 只经只读查询 Command 拿到集合（读写分离照旧：写走下面的命令、读走这里）。
            var items = this.ExecuteCommand(new GetTodoItemsCommand());

            // ── 操作区：一个按钮 = 一次 ObservableList 操作（白盒，看清每步因果）──
            host.AddSectionTitle("操作源集合（每个按钮 = 一次 ObservableList 操作）");
            var evidenceLabel = host.AddValueDisplay();
            evidenceLabel.name = "reactive-list-evidence";
            var evidence = new ReactiveListEvidence(evidenceLabel);
            host.AddActionRow("尾部添加一项", () => this.ExecuteCommand(new AddTodoCommand()),
                CodeRef.Here("struct AddTodoCommand", "Add"));
            host.AddActionRow("插入到顶部", () => this.ExecuteCommand(new InsertTopTodoCommand()),
                CodeRef.Here("struct InsertTopTodoCommand", "Insert(0)"));
            host.AddActionRow("移除第一项", () => this.ExecuteCommand(new RemoveFirstTodoCommand()),
                CodeRef.Here("struct RemoveFirstTodoCommand", "RemoveAt(0)"));
            host.AddActionRow("替换第一项（应换新实例）", () => this.ExecuteCommand(new ReplaceFirstTodoCommand()),
                CodeRef.Here("struct ReplaceFirstTodoCommand", "Replace（索引器赋值）"));
            host.AddActionRow("首项移到末尾（实例号应保持）", () =>
            {
                int createdBefore = evidence.Created;
                int disposedBefore = evidence.Disposed;
                bool canMove = items.Count > 1;
                this.ExecuteCommand(new MoveFirstToEndCommand());
                evidence.ReportMove(canMove, createdBefore, disposedBefore);
            },
                CodeRef.Here("struct MoveFirstToEndCommand", "Move"));
            host.AddActionRow("清空", () => this.ExecuteCommand(new ClearTodoCommand()),
                CodeRef.Here("struct ClearTodoCommand", "Clear"));

            // ── 列表区：BindList 把源集合增量绑到一个容器 ──
            host.AddSectionTitle("绑定的列表视图（跟随源集合增量刷新）");
            var listContainer = new VisualElement();
            listContainer.name = "reactive-list-container";
            listContainer.AddToClassList("demo-list");
            host.Content.Add(listContainer);

            Bag.BindList(listContainer, items, (text, rowBag) => BuildRow(text, rowBag, evidence));

            host.AddNote(
                "每行左侧的 `实例 #N` 是这一个子 View 的稳定身份：点 Move 时它只换位置，创建/释放计数都不变；点 Replace 时该槽旧实例释放、一个新实例接替。"
                + "这让“增量”不再只是看起来顺序正确，而能直接排除整表重建。行数适中的 UI 列表（背包/设置项/聊天）用它正合适；上万项要虚拟化滚动复用则用 Toolkit 原生 `ListView`。");
            host.AddTip("顶部证据条里的「释放」由每行专属子 bag 的真实 Dispose 回调累计，不是按钮手算。每行末尾的 ✕ 也挂这个 rowBag：行被移除时，行内订阅随之自动退订。"
                + "同一套写法在 UGUI 侧是 Bag.BindList(Transform, ...)——只换容器类型，绑定心智一致。");

            // ── 失败边界：不把 Adapter 缺陷伪装成仍可继续的列表 ──
            host.AddSectionTitle("失败时为何停止绑定，而不是自动重试");
            host.AddConcept("初始化失败",
                "若某一行的 factory / 挂载失败，`BindList` 会摘除此前已建行并释放所有 rowBag，随后直接抛出根因；调用方拿不到一个半初始化句柄。若 factory 在返回前还创建了其它外部对象，那部分仍由 factory 自己清理。");
            host.AddConcept("增量失败",
                "集合的 Add / Move 已经提交，容器回调又可能只执行了一半；框架无法安全反改 Model 或猜测 UI 当前层级。因此它会停止本次订阅、尽力释放所有行，并用一条框架 Error 保留终止根因；清理本身若也失败，会有补充 Error，但不会盖掉根因。修复 factory / Adapter 后重进页面或重新绑定。");
            host.AddTip("`itemFactory`、挂载、摘除、移动和 rowBag 的 Dispose 回调只负责当前行，不是集合写入钩子：不要在其中同步修改正在绑定的同一个 `ObservableList`，否则会产生嵌套索引事件并被明确拒绝。只有 rowBag 清理当前行时释放宿主 Bag 属于正常结束；框架会等当前事件返回后清理余行，也不会再启动 Replace / Reset 后半段的新 factory。");
        }

        // 造一行子视图：稳定实例号 + 文本 + 末尾 ✕。实例证据和点击订阅都挂本行专属 rowBag，
        // 行离开列表时由绑定引擎真实 Dispose；Move 只换兄弟位，不会触发此释放回调。
        private VisualElement BuildRow(string text, DisposableBag rowBag, ReactiveListEvidence evidence)
        {
            var rowEvidence = evidence.CreateRow();
            var row = new VisualElement();
            row.name = $"reactive-list-row-{rowEvidence.InstanceId}";
            row.userData = rowEvidence;
            row.AddToClassList("demo-list-row");

            var identity = new Label($"实例 #{rowEvidence.InstanceId}");
            identity.AddToClassList("demo-badge");
            identity.AddToClassList("demo-badge--yes");
            row.Add(identity);

            var label = new Label(text);
            label.AddToClassList("demo-list-item");
            row.Add(label);

            var remove = new Button { text = "✕" };
            remove.AddToClassList("demo-list-remove");
            remove.tooltip = "移除该项";
            rowBag.SubscribeClick(remove, () => this.ExecuteCommand(new RemoveTodoCommand(text)));
            row.Add(remove);
            rowBag.Add(Disposable.Create(() => evidence.DisposeRow(rowEvidence)));

            return row;
        }
    }

    /// <summary>
    /// Demo 的列表增量可视证据：同时给画面和测试提供行实例身份、创建/释放/存活计数。
    /// 它不参与绑定正确性，只观察 itemFactory 与 rowBag Dispose 这两个真实 Seam。
    /// </summary>
    internal sealed class ReactiveListEvidence
    {
        private readonly Label _display;
        private int _nextInstanceId;
        private string _lastEvent = "等待集合操作";

        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public int Active => Created - Disposed;

        public ReactiveListEvidence(Label display)
        {
            _display = display;
            Refresh();
        }

        public ReactiveListRowEvidence CreateRow()
        {
            var row = new ReactiveListRowEvidence(++_nextInstanceId);
            Created++;
            _lastEvent = $"创建实例 #{row.InstanceId}";
            Refresh();
            return row;
        }

        public void DisposeRow(ReactiveListRowEvidence row)
        {
            if (row == null || row.IsDisposed) return;
            row.IsDisposed = true;
            Disposed++;
            _lastEvent = $"释放实例 #{row.InstanceId}（rowBag 已 Dispose）";
            Refresh();
        }

        public void ReportMove(bool moved, int createdBefore, int disposedBefore)
        {
            _lastEvent = moved
                ? $"Move 完成：创建 +{Created - createdBefore} / 释放 +{Disposed - disposedBefore}，原实例只换位置"
                : "Move 未执行：至少需要两行";
            Refresh();
        }

        private void Refresh()
        {
            if (_display == null) return;
            _display.text = $"行实例　创建 {Created}　释放 {Disposed}　存活 {Active}　｜　{_lastEvent}";
        }
    }

    /// <summary>附着在真实行 VisualElement 的可观察身份；rowBag 释放时 <see cref="IsDisposed"/> 变为 true。</summary>
    internal sealed class ReactiveListRowEvidence
    {
        public int InstanceId { get; }
        public bool IsDisposed { get; internal set; }

        public ReactiveListRowEvidence(int instanceId)
        {
            InstanceId = instanceId;
        }
    }

    /// <summary>纯 C# Model：一串待办项 + 用于生成唯一文案的自增序号。集合用 <see cref="ObservableList{T}"/> 持有（增删移换会推送增量通知）。</summary>
    public sealed class TodoBoardModel : IModel
    {
        public readonly ObservableList<string> Items = new();
        public int Seq;
    }

    /// <summary>只读查询：待办集合流，供 View 增量绑定。返回 <see cref="IReadOnlyObservableList{T}"/>（只读、仍可观察，等价于单值的 <c>ReadOnlyReactiveProperty</c>）。</summary>
    public readonly struct GetTodoItemsCommand : ICommand<IReadOnlyObservableList<string>>
    {
        public IReadOnlyObservableList<string> Execute(ICommandContext ctx) => ctx.GetModel<TodoBoardModel>().Items;
    }

    /// <summary>尾部添加一项（唯一文案）。</summary>
    public readonly struct AddTodoCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
        {
            var m = ctx.GetModel<TodoBoardModel>();
            m.Items.Add($"任务 #{++m.Seq}");
        }
    }

    /// <summary>插入到顶部。</summary>
    public readonly struct InsertTopTodoCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
        {
            var m = ctx.GetModel<TodoBoardModel>();
            m.Items.Insert(0, $"任务 #{++m.Seq}");
        }
    }

    /// <summary>移除第一项（空集合无操作）。</summary>
    public readonly struct RemoveFirstTodoCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
        {
            var items = ctx.GetModel<TodoBoardModel>().Items;
            if (items.Count > 0) items.RemoveAt(0);
        }
    }

    /// <summary>用索引器替换首项（空集合无操作）：绑定收到 Replace 后释放旧行 Bag，并在同槽创建新行。</summary>
    public readonly struct ReplaceFirstTodoCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
        {
            var m = ctx.GetModel<TodoBoardModel>();
            if (m.Items.Count > 0)
                m.Items[0] = $"任务 #{++m.Seq}（替换）";
        }
    }

    /// <summary>移除指定文案的项（每行 ✕ 用；文案唯一，Remove 命中该行）。</summary>
    public readonly struct RemoveTodoCommand : ICommand
    {
        private readonly string _value;
        public RemoveTodoCommand(string value) => _value = value;
        public void Execute(ICommandContext ctx) => ctx.GetModel<TodoBoardModel>().Items.Remove(_value);
    }

    /// <summary>首项移到末尾（少于两项无意义）。演示 Move：视图复用同一行实例、只换位置。</summary>
    public readonly struct MoveFirstToEndCommand : ICommand
    {
        public void Execute(ICommandContext ctx)
        {
            var items = ctx.GetModel<TodoBoardModel>().Items;
            if (items.Count > 1) items.Move(0, items.Count - 1);
        }
    }

    /// <summary>清空整个集合（发一次 Reset）。</summary>
    public readonly struct ClearTodoCommand : ICommand
    {
        public void Execute(ICommandContext ctx) => ctx.GetModel<TodoBoardModel>().Items.Clear();
    }
}
