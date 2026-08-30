using Game.Framework.Common;
using Game.Framework.Demo.Core;
using R3;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·多 Context：Context 构成作用域树 + 覆盖/回退。不另造父节点——demo 根 Context 就是父级，
    /// 场景里只有一个子 Context 节点（<see cref="DemoSubContext"/>），其子树下挂第二个 <see cref="MonoScoreModel"/>。
    /// 演示走业务的日常形态：把「View」章的同一个弹窗 prefab 分别挂到根 / 子作用域子树——同一份代码、同一个 Command，
    /// 挂哪儿就读写哪个作用域的数据；业务不手动抓 Context，靠挂载位置说话。
    /// </summary>
    public sealed class MultiContextDemoModule : DemoModuleBase
    {
        public override string Id => "multi-context";
        public override string Title => "多上下文（Context）· 作用域树";
        public override string Category => "核心";
        public override int Order => 40;   // 紧跟「View」章：复用它的弹窗 prefab 演示「挂哪儿就用哪个作用域」

        public override string Summary =>
            "GameContext 组成作用域树：同类型依赖就近覆盖，缺失时回退父级。" +
            "把同一个 View 挂到不同 Context 子树即可读写不同状态，无需改 View 代码。";

        public override void Build(DemoModuleHost host)
        {
            var assets = Object.FindFirstObjectByType<DemoUGuiAssets>();
            var subCtxNode = Object.FindFirstObjectByType<DemoSubContext>();
            if (assets == null || assets.ViewPrefab == null || subCtxNode == null)
            {
                host.AddUnavailable(
                    "当前场景缺少 UGUI View prefab 或 `DemoSubContext` 子树，无法并排演示父子作用域解析。",
                    "确认 ChapterAssets 下有 `DemoUGuiAssets`（已指定 ViewPrefab），并保留挂 `DemoSubContext` 的 SubContext 节点。",
                    "接线恢复后重新进入本章即可对比两份分数；恢复前先读“容器”章理解注册与解析，再回来看作用域覆盖。",
                    new CodeRef(
                        "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoSubContext.cs",
                        "class DemoSubContext",
                        "子 Context 接线"));
                return;
            }
            var subScore = subCtxNode.GetComponentInChildren<MonoScoreModel>(true);
            if (subScore == null)
            {
                host.AddUnavailable(
                    "`DemoSubContext` 子树中没有 `MonoScoreModel`，因此子作用域没有可覆盖父级的同类型状态。",
                    "把一个 `MonoScoreModel` 节点放到 SubContext 子树下，让它在 Awake 时就近注册进子 Context。",
                    "恢复后重新进入本章即可观察根/子两份分数；恢复前可先看“数据模型（Model）· 状态与 Inspector”了解 Mono 自动注册。",
                    new CodeRef(
                        "Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs",
                        "class MonoScoreModel",
                        "子树状态组件"));
                return;
            }

            // ── 定位 ──
            host.AddPositioning("Context 是作用域树，挂载位置决定用哪份数据");
            host.AddNote("`GameContext` 嵌套成作用域树（全局 / 场景 / 局部）：同类型就近注册形成**覆盖**，子级没有的类型**回退**父级。下面用「View」章的同一个弹窗演示——挂到哪个子树，就读写哪个作用域的数据，零代码切换。");

            // ── 覆盖：同一个 View prefab，挂到哪个子树就用哪个作用域 ──
            host.AddSectionTitle("覆盖：同一个 View，挂哪儿就用哪个作用域的数据");
            var rootLabel = host.AddValueDisplay();
            var subLabel = host.AddValueDisplay();
            // 根作用域分数：本模块自己就是根作用域的 view 角色，走正规读法（查询 Command）。
            Bag.Subscribe(this.ExecuteCommand(new GetMonoScoreCommand()), v => rootLabel.text = $"根作用域 ScoreModel → 分数：{v}");
            // 子作用域分数：本模块绑在根作用域，查询命令解析不到子级——直读场景组件做对照显示（仅 demo 导览用；
            // 业务里要读子作用域状态的 View，应像下面的弹窗一样挂进子作用域，用同样的查询命令读）。
            Bag.Subscribe(subScore.Score, v => subLabel.text = $"子作用域 ScoreModel → 分数：{v}");

            // 弹窗同一时刻只开一个：换挂载点时先关旧的，分数对照看上面两行常驻标签；切走本章随 Bag 销毁。
            GameObject popup = null;
            Bag.Add(Disposable.Create(() => { if (popup != null) Object.Destroy(popup); }));
            void Popup(Transform parent)
            {
                // 先关掉场上任何已开的同款弹窗（含「View」章遗留未关的）：同屏只留一个，分数对照看上面的常驻标签。
                var existing = Object.FindFirstObjectByType<UGuiDemoView>();
                if (existing != null) Object.Destroy(existing.gameObject);
                popup = Object.Instantiate(assets.ViewPrefab, parent);
            }

            host.AddActionRow("弹出 View 到【根】作用域（挂 UGuiAssets 下）", () => Popup(assets.transform),
                CodeRef.Here("Popup(assets.transform)", "挂根作用域子树"));
            host.AddActionRow("弹出同一个 View 到【子】作用域（挂 SubContext 下）", () => Popup(subCtxNode.transform),
                CodeRef.Here("Popup(subCtxNode.transform)", "挂子作用域子树"));
            host.AddNote("两个按钮弹的是**同一个 prefab、同一份代码、同一个 +1 Command**（就是「View」章那个弹窗），唯一区别是挂载位置："
                + "`Awake` 沿父链找最近 Context——挂 `UGuiAssets` 下读写根作用域的 `MonoScoreModel`，挂 `SubContext` 下读写子作用域那份。"
                + "点弹窗里的 +1，看上面两行分数各自跳动——**挂哪儿就用哪个作用域，零代码切换**。"
                + "这就是多 Context 的日常用法：业务不手动抓 Context，靠挂载位置说话。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/UGuiDemoView.cs", "class UGuiDemoView", "弹窗 View（与「View」章同一个）"));
            host.AddNote("两个 `MonoScoreModel` 也都没写一行注册代码：挂在哪个 Context 的子树下，`Awake` 就近注册进哪个作用域——"
                + "**Hierarchy 树就是作用域树**。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "MonoScoreModel（零注册代码）"));

            // ── 回退：子级没注册的类型，沿作用域链回退父级 ──
            host.AddSectionTitle("回退：子 Context 没有的类型，回退父级");
            host.AddNote("子 Context 里只注册了它自己的 `MonoScoreModel`——弹到子作用域的 View 能正常执行命令，"
                + "是因为命令所需的 `ICommandSystem` 在子级解析不到、自动沿作用域链回退到父级（根）解析：**回退对业务完全透明**。"
                + "回退是单向的（子 → 父）：父 Context 看不到子 Context 注册的东西。");

            // ── 运行时增删的边界 ──
            host.AddSectionTitle("运行时增删的边界");
            host.AddConcept("添加 ✅", "随时 Instantiate 带 `MonoXxxBase` 的 prefab 进某个 Context 子树，`Awake` 就近自动注册——上面的弹窗就是动态添加。"
                + "同一 Context 重复注册同类型会抛异常，这正是在帮你挡「替换」；要同类型另一份实例，开子 Context 覆盖（本章演示的）。");
            host.AddConcept("移除 ⚠️", "Destroy 会干净反注册，但 `[Inject]` 快照和已建立的订阅**不会被重定向**——还有消费者引用它时移除＝制造孤儿。"
                + "正确姿势：把「层 + 它的消费者」放同一棵子树，撤的时候整棵子树连根撤（如关闭弹窗、销毁整个子 Context），天然无孤儿。");
            host.AddConcept("替换 ❌", "「移除再添加、期望既有引用指向新实例」不支持（刻意设计）：快照 / 订阅指旧实例、实时解析指新实例，读写分裂成难查的 bug。"
                + "换数据 → 重置 Model 内部状态；换实例 → 子 Context 覆盖；换整层 → Context 整体 Dispose 重建。");
            host.AddNote("一句记法：**增量随便加，换血不允许，撤就整棵撤**。详见框架手册 §11「运行时增删层的边界」。");

#if UNITY_EDITOR
            host.AddActionRow("选中 SubContext 节点", () => DemoEditorNav.PingSceneObject(subCtxNode.gameObject),
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoSubContext.cs", "class DemoSubContext", "子 Context 定义"));
            host.AddActionRow("选中 子作用域的 ScoreModel", () => DemoEditorNav.PingSceneObject(subScore.gameObject),
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "Mono Model 定义"));
            host.AddActionRow("选中 根作用域的 ScoreModel", () =>
            {
                var rootContext = Object.FindFirstObjectByType<MonoDemoContext>();
                var rootScore = DemoEditorNav.FindComponentOwnedBy<MonoScoreModel>(rootContext);
                if (rootScore != null) DemoEditorNav.PingSceneObject(rootScore.gameObject);
            }, new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/Support/MonoScoreModel.cs", "class MonoScoreModel", "Mono Model 定义"));
            host.AddTip("点上面按钮去 Hierarchy 看结构：Main Context（根 Context）→ ChapterAssets/SubContext（DemoSubContext）→ 它的 ScoreModel (Sub)。"
                + "运行时在 Inspector 里直接改任一实例的 Score，上方对应标签实时刷新——哪个作用域的数据一目了然。");
#endif

            host.AddSectionTitle("作用域树");
            host.AddConcept("分层", "全局（跨场景：配置 / 音频）→ 场景（本场景）→ 局部（一个面板 / 关卡）各成一层。");
            host.AddConcept("解析顺序", "本层运行时覆盖 → 本层 `InstallBindings` → 父级递归 → 全局 `Main`（`inheritFromGlobal` 时）。");
            host.AddConcept("覆盖 vs 回退", "子层注册同类型 → 用子层（覆盖）；子层没有 → 逐级回退父层。");
            host.AddTip("好处：切场景 / 关卡时整层 Context 一并 Dispose，临时注册随之清掉、不污染全局——这也是不在运行时单独热替换某个层的原因。");

            host.AddSectionTitle("这棵树给你什么");
            host.AddConcept("测试沙盒", "拖一个子 Context、把被测 Model / System 挂进它的子树——缺的依赖回退父级、要替换的注册 Mock 覆盖；不必启动整个游戏即可联调，测完删掉整棵子树即净，主场景零污染。");
            host.AddConcept("局部世界", "关卡 / 副本 / 面板的状态注册在局部 Context，结束时整层 Dispose，临时注册不泄漏全局。");
            host.AddConcept("prefab 即插即用", "内含 `MonoXxxBase` 的 prefab 实例化到哪个子树就接入哪个作用域——换挂载位置 = 换依赖来源，上面的弹窗演示的就是这件事。");
            host.AddNote("「树状思维」贯穿框架：Context 作用域树（解析回退）、Hierarchy 就近注册（本章演示的）、`Bag` 子作用域级联释放（「生命周期」章）——"
                + "把节点放进哪个子树，就一次说清「依赖从哪来、注册到哪去、何时被清理」。深入见框架手册 §1「树状思维」。");
        }

    }
}
