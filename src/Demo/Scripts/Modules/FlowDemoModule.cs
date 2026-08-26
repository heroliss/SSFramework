using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Flow;
using R3;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·游戏流程：把「启动 → 登录 → 大厅 → 战斗」显式化为 <see cref="FlowState"/> 子类，
    /// 每个状态进入时获得子 Context、退出整棵撤——阶段私有服务 / 订阅 / 资源随阶段结束自动清理。
    /// 转换串行 + 最新意图胜（战斗加载中点「进登录」，登录赢）。ADR-0023。
    /// </summary>
    public sealed class FlowDemoModule : DemoModuleBase
    {
        public override string Id => "flow";
        public override string Title => "游戏流程 · 阶段状态机";
        public override string Category => "能力";
        public override int Order => 50;
        public override string Summary =>
            "显式 Flow：GoTo(new BattleState(levelId)) 切阶段（传参走构造），每状态一个子 Context、" +
            "退出整棵撤（切阶段漏清理被结构性消灭）；转换串行 + 最新意图胜。ADR-0023。";

        /// <summary>
        /// 流程状态机的标准注册路径：RegisterOwned = 注册即注入回填宿主 Context（ADR-0019），
        /// 宿主 Context Dispose 时 flow 连同当前状态子 Context 一并撤。
        /// 本阶段只声明注册关系；Build 需要的运行时对象仍从 Context 解析，让所有权与 View 权限保持清晰。
        /// </summary>
        public override void InstallBindings(ContainerBuilder builder)
        {
            builder.RegisterOwned(new GameFlow(), typeof(IGameFlow));
        }

        public override void Build(DemoModuleHost host)
        {
            var flow = this.GetUtility<IGameFlow>();

            // ── 定位 ──
            host.AddPositioning("游戏宏观阶段的显式结构 + 作用域整棵撤");
            host.AddNote("没有显式流程时，「现在游戏在哪个阶段、这个阶段占用的东西什么时候撤」散落在各场景脚本里——切阶段漏清理是最常见的泄漏来源。`IGameFlow` 把阶段显式化为 `FlowState` 子类：进入时框架为它构建**子 Context**（父级 = 宿主 Context，解析未命中自动回退父链），退出时**整棵 Dispose**——阶段私有服务（InstallBindings 注册）、订阅与资源（进状态 Bag）全部自动清理，不依赖自觉。",
                new CodeRef("Assets/Game/Framework/Core/Flow/IGameFlow.cs", "public interface IGameFlow", "流程入口契约"));
            host.AddSubNote("状态是**一次性实例**：`GoTo(new BattleDemoState(level, …))`——传参走构造函数，每次进入全新对象（无残留脏状态）；重进同类状态就 new 一个新实例，复用已消费的实例抛参数异常。");

            // ── 注册方式 ──
            host.AddSectionTitle("注册：RegisterOwned，随宿主 Context 存亡");
            host.AddNote("本章的 `IGameFlow` 在 `InstallBindings` 里 `RegisterOwned` 注册——「注册即注入」自动回填宿主 Context，宿主 Dispose 时 flow 连同当前状态子 Context 一并撤。流程比场景活得长，刻意没有 Mono 版。",
                CodeRef.Here("builder.RegisterOwned(new GameFlow()", "本章的注册代码"));

            // ── 状态面板 ──
            host.AddSectionTitle("当前阶段与流转日志");
            var stateLabel = host.AddValueDisplay();
            stateLabel.schedule.Execute(() =>
            {
                stateLabel.text = $"当前阶段：{flow.Current?.ToString() ?? "（未启动）"}" +
                                  (flow.IsTransitioning ? "　|　转换中…" : string.Empty);
            }).Every(100);

            var logLabel = host.AddValueDisplay("（流转日志将出现在这里）");
            logLabel.style.whiteSpace = WhiteSpace.Normal;

            // 本章 UI 的写入口：切走本章后（Bag 已 Dispose）静默丢弃——flow 注册在共享 demo Context 上，
            // 生命周期比章节长，迟到的状态回调不能再碰已拆除的面板。
            bool alive = true;
            Bag.Add(Disposable.Create(() => alive = false));
            var logLines = new List<string>();
            Action<string> report = msg =>
            {
                if (!alive) return;
                logLines.Add(msg);
                if (logLines.Count > 8) logLines.RemoveAt(0);
                logLabel.text = string.Join("\n", logLines);
            };

            // FlowChangedEvent：loading 界面 / 埋点只订这一个事件，不侵入每个状态。订阅进 Bag 随本章退订。
            Bag.Add(this.RegisterEvent<FlowChangedEvent>(e =>
                report($"[事件] {e.From?.ToString() ?? "（无）"} → {e.To}")));
            host.AddSubNote("事件只记录**完整进入成功**的阶段：若从大厅进入战斗的加载途中改点登录，最终只发布「大厅 → 登录」；从未成为 Current 的战斗不是历史节点，来源也不会误报成「无」。");

            // ── 转换按钮 ──
            host.AddSectionTitle("流转：GoTo 是唯一动词");
            host.AddAsyncActionRow("进「启动」（模拟初始化 1s）", ct => Go(flow, new BootDemoState(report), report, ct),
                CodeRef.Here("new BootDemoState(report)", "进入启动"));
            host.AddAsyncActionRow("进「登录」", ct => Go(flow, new LoginDemoState(report), report, ct),
                CodeRef.Here("new LoginDemoState(report)", "进入登录"));
            host.AddAsyncActionRow("登录成功 → 「大厅」（注册阶段私有服务）", ct => Go(flow, new LobbyDemoState(report), report, ct),
                CodeRef.Here("new LobbyDemoState(report)", "进入大厅"));

            var levelSlider = new SliderInt("关卡号（构造参数）", 1, 10) { value = 3, showInputField = true };
            levelSlider.AddToClassList("demo-slider");
            host.Content.Add(levelSlider);
            host.AddAsyncActionRow("进「战斗」（带关卡号，模拟加载 1.5s）",
                ct => Go(flow, new BattleDemoState(levelSlider.value, report), report, ct),
                CodeRef.Here("new BattleDemoState(levelSlider.value", "带参进入战斗"));

            host.AddNote("**观察整棵撤**：进大厅时日志出现「宝箱服务已注册」（`LobbyDemoState.InstallBindings` 里 RegisterOwned 的阶段私有服务）；切去任意别处时出现「宝箱服务已随子 Context 撤除」——没有任何手写清理代码，这就是「每状态一个子 Context」买到的东西。",
                CodeRef.Here("RegisterOwned(new LobbyChestService(", "阶段私有服务"));
            host.AddNote("**观察最新意图胜**：战斗加载的 1.5 秒内点「进登录」——战斗的在途进入被协作取消（日志出现「被顶替」，其半建的子 Context 整棵撤、不调 OnExit），最终落在登录。转换全程串行，业务不用自己处理竞态。");
            host.AddConcept("OnExit 是优雅告别，不是可靠清理", "它只在完整进入后的正常退出开始时调用，适合存档上报等尽力而为工作；可靠释放必须进状态 Bag 或子 Context 持有的服务。宿主若在 OnExit 期间销毁，GoTo 与子 Context 会立即收口，不等这个无 token 的物理任务；迟到代码不能再访问已撤的 Context / Bag，迟到异常仍会进入统一日志。");

            // ── 刻意不做 ──
            host.AddSectionTitle("刻意不做");
            host.AddConcept("不做转换表 / 守卫", "任意 GoTo 合法。「登录没完成不给进大厅」是业务 if 的事（按钮置灰 / Command 里查状态），框架不做规则引擎。");
            host.AddConcept("不做分层状态机（HSM）", "战斗内的子阶段机（准备 → 作战 → 结算）= 在 `BattleState.InstallBindings` 里再 RegisterOwned 一个 `GameFlow`——作用域树天然嵌套，外层状态退出时子 flow 连同其当前状态级联撤。");
            host.AddConcept("不做场景绑定", "状态 ≠ 场景（多状态共享一场景、一状态加载多场景都常见）。状态在 `OnEnter` 里自己 `Bag.LoadScene(...)`，退出随 Bag 卸载。");
            host.AddConcept("不做历史栈", "「返回上一状态」是业务记一个变量再 GoTo 的事；UI 返回栈已归 UI 框架管（「UI 框架」章），流程层再来一个栈会打架。");

            host.AddTip("速记：阶段 = FlowState 子类（一次性实例，传参走构造）；私有服务进 InstallBindings、订阅资源进 Bag，退出整棵撤；GoTo 串行 + 最新意图胜，await 它可拿到完成 / 被顶替 / 失败；OnExit 只做优雅告别。微观逻辑状态机（技能连招、AI）不要用它。深度见 framework-guide 流程章 / ADR-0023。");
        }

        // GoTo 的三种结局都写进日志：完成 / 被更新的 GoTo 顶替（取消）/ Enter 失败（异常冒出，调用方决定去处）。
        private static async UniTask Go(
            IGameFlow flow,
            FlowState next,
            Action<string> report,
            CancellationToken chapterCt)
        {
            try
            {
                chapterCt.ThrowIfCancellationRequested();
                await flow.GoTo(next);
                chapterCt.ThrowIfCancellationRequested();
                report($"GoTo {next} 完成 ✓");
            }
            catch (OperationCanceledException) when (!chapterCt.IsCancellationRequested)
            {
                report($"GoTo {next} 被顶替/取消（最新意图胜）");
            }
            catch (OperationCanceledException)
            {
                throw; // 切章取消交回 Host 静默收口，不能落进下面的通用失败提示。
            }
            catch (Exception e)
            {
                report($"GoTo {next} 失败：{e.Message}");
            }
        }

        // ── 演示用流程状态（真实项目里这些是业务顶层类型，一个文件一个阶段） ──

        private sealed class BootDemoState : FlowState
        {
            private readonly Action<string> _report;
            public BootDemoState(Action<string> report) => _report = report;
            public override string ToString() => "启动";

            protected override async UniTask OnEnter(CancellationToken ct)
            {
                _report("启动：初始化中…（1s）");
                await UniTask.Delay(TimeSpan.FromSeconds(1), cancellationToken: ct);
                _report("启动：初始化完成");
            }
        }

        private sealed class LoginDemoState : FlowState
        {
            private readonly Action<string> _report;
            public LoginDemoState(Action<string> report) => _report = report;
            public override string ToString() => "登录";

            protected override UniTask OnEnter(CancellationToken ct)
            {
                _report("登录：等待玩家输入（即时进入）");
                return UniTask.CompletedTask;
            }
        }

        private sealed class LobbyDemoState : FlowState
        {
            private readonly Action<string> _report;
            public LobbyDemoState(Action<string> report) => _report = report;
            public override string ToString() => "大厅";

            // 阶段私有服务：注册在本状态的子 Context，切走大厅时随整棵撤自动 Dispose——观察日志即可验证。
            protected override void InstallBindings(ContainerBuilder builder)
                => builder.RegisterOwned(new LobbyChestService(_report), typeof(LobbyChestService));

            protected override UniTask OnEnter(CancellationToken ct)
            {
                _report("大厅：就绪");
                return UniTask.CompletedTask;
            }
        }

        private sealed class BattleDemoState : FlowState
        {
            private readonly int _level;
            private readonly Action<string> _report;

            public BattleDemoState(int level, Action<string> report)
            {
                _level = level;
                _report = report;
            }

            public override string ToString() => $"战斗(第{_level}关)";

            protected override async UniTask OnEnter(CancellationToken ct)
            {
                _report($"战斗：加载第 {_level} 关…（1.5s，此间点「进登录」演示最新意图胜）");
                await UniTask.Delay(TimeSpan.FromSeconds(1.5), cancellationToken: ct);
                _report($"战斗：第 {_level} 关就绪");
            }

            protected override UniTask OnExit()
            {
                _report("战斗：结算完成（OnExit——仅正常转换时被调，可靠清理靠 Bag）");
                return UniTask.CompletedTask;
            }
        }

        /// <summary>大厅阶段私有服务样例：生命周期完全由状态子 Context 管，Dispose 时机可在日志观察。</summary>
        private sealed class LobbyChestService : IDisposable
        {
            private readonly Action<string> _report;

            public LobbyChestService(Action<string> report)
            {
                _report = report;
                _report("宝箱服务已注册（大厅阶段私有，RegisterOwned）");
            }

            public void Dispose() => _report("宝箱服务已随子 Context 撤除（整棵撤，无手写清理）");
        }
    }
}
