using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 核心·多 Context：Context 构成作用域树 + 覆盖/回退。不另造父节点——demo 根 Context 就是父级，
    /// 场景里只加一个子 Context 节点（<see cref="DemoSubContext"/>），其子树下挂第二个 <see cref="MonoScoreModel"/>：
    /// Hierarchy 树就是作用域树——同类型 Model 挂在哪个 Context 子树下就近注册进哪个作用域（覆盖父级同类型），
    /// 子级没有的类型沿作用域链回退父级；同一个 Command 在哪个 Context 上执行，就作用于哪个作用域的数据。
    /// </summary>
    public sealed class MultiContextDemoModule : DemoModuleBase
    {
        public override string Id => "multi-context";
        public override string Title => "多 Context · 作用域树";
        public override string Category => "核心";
        public override int Order => 45;
        public override string Summary =>
            "GameContext 是一棵作用域树（全局 / 场景 / 局部）。demo 根 Context 就是父级，场景里只加一个子 Context 节点；"
            + "其子树下另挂一个 MonoScoreModel——同类型就近注册形成覆盖，子级没有的类型回退父级。"
            + "同一个 Command 在哪个 Context 上执行，就作用于哪个作用域的数据。";

        public override void Build(DemoModuleHost host)
        {
            var root = Object.FindFirstObjectByType<MonoDemoContext>();
            var sub = Object.FindFirstObjectByType<DemoSubContext>();
            if (root == null || sub == null)
            {
                host.AddNote("没找到 Context 节点——请确认场景里有 MonoDemoContext（Main Context），ChapterAssets 下有挂 DemoSubContext 的 SubContext 节点（含 MonoScoreModel 子节点）。");
                return;
            }

            // ── 覆盖：同类型 Model，两个作用域各自解析到自己的实例 ──
            host.AddSectionTitle("覆盖：同类型 Model，子 Context 用自己的");
            // 白盒说明：本章演示容器解析本身，直接在两个 Context 上解析 / 执行；业务代码仍按层权限走（View 只 ExecuteCommand）。
            var rootScore = root.GetModel<MonoScoreModel>();
            var subScore = sub.GetModel<MonoScoreModel>();

            var rootLabel = host.AddValueDisplay();
            var subLabel = host.AddValueDisplay();
            Bag.Subscribe(rootScore.Score, v => rootLabel.text = $"根 Context 解析 MonoScoreModel → 分数：{v}");
            Bag.Subscribe(subScore.Score, v => subLabel.text = $"子 Context 解析 MonoScoreModel → 分数：{v}");

            // 同一个 Command（「Model」章的 RaiseMonoScoreCommand）零改动：在哪个 Context 上执行，
            // 命令里的 ctx.GetModel 就解析到哪个作用域的 MonoScoreModel——作用域树给业务的直接便利。
            host.AddActionRow("在【子】Context 执行同一个 +1 命令", () => sub.ExecuteCommand(new RaiseMonoScoreCommand()),
                CodeRef.Here("sub.ExecuteCommand(new RaiseMonoScoreCommand())", "子 Context 上执行"));
            host.AddActionRow("在【根】Context 执行同一个 +1 命令", () => root.ExecuteCommand(new RaiseMonoScoreCommand()),
                CodeRef.Here("root.ExecuteCommand(new RaiseMonoScoreCommand())", "根 Context 上执行"));
            host.AddNote("两个 `MonoScoreModel` 都没写一行注册代码：挂在哪个 Context 的子树下，`Awake` 就近注册进哪个作用域——"
                + "**Hierarchy 树就是作用域树**。同一个 `RaiseMonoScoreCommand` 在子 Context 上执行只动子级分数、在根上执行只动根级分数。",
                new CodeRef("Assets/Game/Framework/Demo/Scripts/Modules/MonoScoreModel.cs", "class MonoScoreModel", "MonoScoreModel（零注册代码）"));

            // ── 回退：子级没注册的类型，沿作用域链回退父级 ──
            host.AddSectionTitle("回退：子 Context 没有的类型，回退父级");
            bool same = ReferenceEquals(sub.GetModel<CodeScoreModel>(), root.GetModel<CodeScoreModel>());
            host.AddValueDisplay(same
                ? "子 Context 解析 CodeScoreModel → 与根 Context 同一实例 ✓（子级没注册，回退命中父级）"
                : "子 Context 解析 CodeScoreModel → 意外：不同实例 ✗");
            host.AddNote("`CodeScoreModel`（「Model」章注册在根）子 Context 没注册 → 沿作用域链逐级回退，命中父级同一实例。"
                + "执行命令所需的 `ICommandSystem` 同理——子 Context 没注册它，上面两个按钮能跑就是回退在生效。"
                + "回退是单向的（子 → 父）：父 Context 看不到子 Context 注册的东西。");

#if UNITY_EDITOR
            host.AddActionRow("选中 子 Context 节点", () => SelectInInspector(sub.gameObject));
            host.AddActionRow("选中 根 Context 的 MonoScoreModel", () => SelectInInspector(rootScore.gameObject));
            host.AddActionRow("选中 子 Context 的 MonoScoreModel", () => SelectInInspector(subScore.gameObject));
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
            host.AddConcept("prefab 即插即用", "内含 `MonoXxxBase` 的 prefab 实例化到哪个子树就接入哪个作用域——换挂载位置 = 换依赖来源，拖一下节点完成。");
            host.AddNote("「树状思维」贯穿框架：Context 作用域树（解析回退）、Hierarchy 就近注册（本章演示的）、`Bag` 子作用域级联释放（「生命周期」章）——"
                + "把节点放进哪个子树，就一次说清「依赖从哪来、注册到哪去、何时被清理」。深入见框架手册 §1「树状思维」。");
        }

#if UNITY_EDITOR
        // 编辑器便利：选中并高亮场景节点，方便去 Hierarchy / Inspector 看结构与 RP 实时值。非框架用法，纯 demo 导航。
        private static void SelectInInspector(GameObject go)
        {
            UnityEditor.Selection.activeObject = go;
            UnityEditor.EditorGUIUtility.PingObject(go);
        }
#endif
    }
}
