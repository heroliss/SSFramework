using Game.Framework.Command;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Model;
using Game.Framework.UI.Toolkit;
using ObservableCollections;
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
            => builder.RegisterValue(new TodoBoardModel(), typeof(TodoBoardModel));

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("会增删的集合用 ObservableList + BindList 增量绑定");
            host.AddNote(
                "单个值用 `RP<T>` + `BindText`。可是「一串会增删的东西」（背包格子、聊天记录、在线列表）是**集合**——"
                + "若塞进 `RP<List>` 整包推送，View 每次都得清空重建整张列表，丢滚动/选中、还抖 GC。"
                + "改用 `ObservableList<T>` 持有集合，`Bag.BindList` 订阅它的增删移换、只增量维护对应子视图。",
                CodeRef.Here("Bag.BindList(listContainer", "BindList 调用点"));

            // ── 操作区：一个按钮 = 一次 ObservableList 操作（白盒，看清每步因果）──
            host.AddSectionTitle("操作源集合（每个按钮 = 一次 ObservableList 操作）");
            host.AddActionRow("尾部添加一项", () => this.ExecuteCommand(new AddTodoCommand()),
                CodeRef.Here("struct AddTodoCommand", "Add"));
            host.AddActionRow("插入到顶部", () => this.ExecuteCommand(new InsertTopTodoCommand()),
                CodeRef.Here("struct InsertTopTodoCommand", "Insert(0)"));
            host.AddActionRow("移除第一项", () => this.ExecuteCommand(new RemoveFirstTodoCommand()),
                CodeRef.Here("struct RemoveFirstTodoCommand", "RemoveAt(0)"));
            host.AddActionRow("首项移到末尾", () => this.ExecuteCommand(new MoveFirstToEndCommand()),
                CodeRef.Here("struct MoveFirstToEndCommand", "Move"));
            host.AddActionRow("清空", () => this.ExecuteCommand(new ClearTodoCommand()),
                CodeRef.Here("struct ClearTodoCommand", "Clear"));

            // ── 列表区：BindList 把源集合增量绑到一个容器 ──
            host.AddSectionTitle("绑定的列表视图（跟随源集合增量刷新）");
            var listContainer = new VisualElement();
            listContainer.AddToClassList("demo-list");
            host.Content.Add(listContainer);

            // View 只经只读查询 Command 拿到集合（读写分离照旧：写走上面的命令、读走这里）。
            var items = this.ExecuteCommand(new GetTodoItemsCommand());
            Bag.BindList(listContainer, items, BuildRow);

            host.AddNote(
                "上方任一操作后，只有**变化的那一行**被增删或移动，其余行原地不动——这正是 `ObservableList` 的增量通知，"
                + "而非「重算整表再 diff」。行数适中的 UI 列表（背包/设置项/聊天）用它正合适；上万项要虚拟化滚动复用则用 Toolkit 原生 `ListView`。");
            host.AddTip("每行末尾的 ✕ 用「行专属子 bag」接点击（BindList 第二参给的就是它）：这一行被移除时，行内订阅随子 bag 自动退订，不用手动清理。"
                + "同一套写法在 UGUI 侧是 Bag.BindList(Transform, ...)——只换容器类型，绑定心智一致。");
        }

        // 造一行子视图：文本 + 末尾 ✕。✕ 的点击订阅挂到本行专属子 bag（rowBag），行离开列表时自动退订。
        private VisualElement BuildRow(string text, DisposableBag rowBag)
        {
            var row = new VisualElement();
            row.AddToClassList("demo-list-row");

            var label = new Label(text);
            label.AddToClassList("demo-list-item");
            row.Add(label);

            var remove = new Button { text = "✕" };
            remove.AddToClassList("demo-list-remove");
            remove.tooltip = "移除该项";
            rowBag.SubscribeClick(remove, () => this.ExecuteCommand(new RemoveTodoCommand(text)));
            row.Add(remove);

            return row;
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
