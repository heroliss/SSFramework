using System;
using System.IO;
using DemoCfg;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·配置表：讲 Luban 集成的「构建期生成 / 运行期只读字节」分界。表定义（XML）与数据（JSON / Excel）放 demo 自带的
    /// Configs~/（~ 后缀 Unity 不导入），工作台跑 CLI 生成 C# 类 + 二进制数据 + 表清单；运行期由一个自加载的配置 Utility
    /// 服务按清单预载、构造表根，各层（含 View）直读。数据文件走资源包通道（可热更）。查询按钮真实读 Play 中加载好的表。
    /// </summary>
    public sealed class ConfigTableModule : DemoModuleBase
    {
        private const string OverviewMenu = "SSFramework/代码生成/配置表 (Luban)";
        // demo 自带源目录（构建期输入，~ 后缀 Unity 不导入）——可直接打开；生成与配置管理仍集中在工作台。
        private const string ConfigSourceDir = "Assets/Game/Framework/Demo/Configs~";

        // View 侧的轮巡游标（纯展示状态，不属于任何 Model）。
        private int _itemCursor;
        private int _monsterCursor;

        public override string Id => "config-table";
        public override string Title => "配置表 · Luban";
        public override string Category => "进阶";
        public override int Order => 30;
        public override DemoTeachingKind TeachingKind => DemoTeachingKind.Workflow;
        public override string Summary =>
            "Luban 在构建期把 XML 定义与 JSON/Excel 数据生成强类型 C#、二进制数据和表清单。" +
            "运行时配置 Utility 按清单加载 Tables，数据可随资源包更新。";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("策划填的数值表，构建期编译成强类型只读数据");
            host.AddNote("「配置表」就是物品 / 怪物 / 全局参数这类策划填的数值表。本框架用 Luban：**构建期**把表编译成强类型 C# 类 + 二进制数据，" +
                         "**运行期**只读字节、做内存查询——Excel / JSON 解析、数据校验全发生在构建期，运行期对它们零感知。" +
                         "两种数据源都有活样例：`item.json`（文本，git diff 可读、AI 可维护）与 `monster.xlsx`（策划顺手），" +
                         "同一项目可混搭，表定义里 `input` 一个属性决定每张表从哪读。");
            host.AddTable(
                new[] { "构建期产物", "落点", "运行期谁消费" },
                new[] { "配置 C# 类（Tables / TbItem / Item…）", "`Demo/Config/Gen/`", "业务代码（强类型查表）" },
                new[] { "二进制数据（*.bytes）", "`Demo/Res/Configs/`（资源收集范围内）", "`Bag.LoadBytes` 直读字节" },
                new[] { "表清单（LubanTableManifest.g.cs）", "随生成代码", "配置服务据此并行预载" });
            host.AddSubNote("为什么要表清单：生成的 `Tables` 构造函数是**同步**逐表要字节，而框架资源加载是**异步**——先按清单把全部数据并行预载进内存，" +
                            "再同步构造。清单与代码 / 数据同一次生成（CLI 跑完扫数据目录补写），不存在手工维护漏表（机制同热更代码包的 manifest）。",
                new CodeRef("Assets/Game/Framework/Config/Editor/LubanGenerationTransaction.cs", "private static void WriteManifest(", "生成事务 · 校验暂存数据后写清单"));

            // ── 2. 先看结果：各层一行取到强类型表（最常用的一步，先给概念落地） ──
            host.AddSectionTitle("先看结果：各层一行取到强类型表");
            host.AddNote("表由场景里的配置服务在进游戏时加载好（下一节讲它怎么搭）。各层（含 View）一行 " +
                         "`this.GetConfig<Tables>()` 拿到当前 Context 已就绪的表根，查询就是纯内存读、不需要查询 Command——" +
                         "下面按钮真实读 Play 中已加载的表。它不会偷偷使用全局配置；子 Context 仍会解析自己的配置服务。",
                new CodeRef("Assets/Game/Framework/Config/ConfigAccessExtensions.cs", "public static TTables GetConfig", "Context 感知的短读取入口"));

            // 配置是基础设施服务：各层（含本 demo 模块、真实 View）直接 GetUtility 取，无需查询 Command 绕行。
            var config = this.GetUtility<IConfigUtility<Tables>>();

            var stateLabel = host.AddValueDisplay("", CodeRef.Here("Bag.Subscribe(config.State", "本处订阅 config.State（收到 Ready 再读表）"));
            Bag.Subscribe(config.State, s => stateLabel.text = $"加载状态：{s}");

            host.AddTable(
                new[] { "调用场景", "推荐入口", "为什么" },
                new[] { "已由上游保证 Ready 的零散读取", "`this.GetConfig<Tables>()`", "短、强类型、仍按当前 Context 解析；未就绪会给出明确错误" },
                new[] { "启动流程、进关卡前的硬门禁", "`await this.EnsureConfig<Tables>(token)`", "一次得到 Tables；失败保留原始异常，不必手写终态轮询" },
                new[] { "加载提示、按钮禁用态、失败提示", "获取 `IConfigUtility<Tables>` 后订阅 `State`", "持续观察状态变化，收到 Ready 时 Tables 已可用" });
            var readyLabel = host.AddValueDisplay("（点下方按钮观察命令式等待）");
            host.AddAsyncActionRow("等待配置就绪（启动流程推荐）", async ct =>
            {
                try
                {
                    var tables = await this.EnsureConfig<Tables>(ct);
                    readyLabel.text = $"就绪：拿到同一份 Tables，物品 {tables.TbItem.DataList.Count} 条、怪物 {tables.TbMonster.DataList.Count} 条";
                }
                catch (Exception e)
                {
                    readyLabel.text = $"配置失败：{e.Message}（原始异常也已进入统一日志）";
                }
            }, CodeRef.Here("await this.EnsureConfig<Tables>(ct)", "命令式门禁 · 短入口直接取得 Tables 或原始异常"));
            host.AddSubNote("`State` 与 `EnsureReady` 不是两套重复 API：前者是响应式观察，后者把「等待 Ready/Failed → 返回 Tables / 抛根因」收进 Interface。" +
                            "调用方 token 只让当前等待者离开，不会因为一个窗口关闭就中止其他系统共享的配置加载；组件或 Context 销毁才取消真正的 owner。",
                new CodeRef("Assets/Game/Framework/Config/IConfigUtility.cs", "UniTask<TTables> EnsureReady", "就绪契约 · 取消与失败语义"));

            var itemLabel = host.AddValueDisplay("（点下方按钮查表）");
            host.AddActionRow("查下一条物品（轮巡 TbItem.DataList）", () =>
            {
                if (config.State.CurrentValue != ConfigInitState.Ready) { itemLabel.text = "配置未就绪（看上方加载状态）"; return; }
                var t = this.GetConfig<Tables>();
                if (t.TbItem.DataList.Count == 0) { itemLabel.text = "TbItem 没有数据（Datas/item.json 是空的？）"; return; }
                int id = t.TbItem.DataList[_itemCursor++ % t.TbItem.DataList.Count].Id;
                var item = t.TbItem[id];
                itemLabel.text = $"[{item.Id}] {item.Name}（{item.Quality}）售价 {item.Price}，堆叠上限 {item.StackLimit} —— {item.Desc}";
            }, CodeRef.Here("var item = t.TbItem[id]", "本处查表语句 · 生成索引器按主键读取"));

            var monsterLabel = host.AddValueDisplay("（点下方按钮查表）");
            host.AddActionRow("查下一条怪物（轮巡 TbMonster，Excel 数据源）", () =>
            {
                if (config.State.CurrentValue != ConfigInitState.Ready) { monsterLabel.text = "配置未就绪（看上方加载状态）"; return; }
                var t = this.GetConfig<Tables>();
                if (t.TbMonster.DataList.Count == 0) { monsterLabel.text = "TbMonster 没有数据（Datas/monster.xlsx 是空的？）"; return; }
                var m = t.TbMonster.DataList[_monsterCursor++ % t.TbMonster.DataList.Count];
                var drop = t.TbItem.GetOrDefault(m.DropItemId);
                monsterLabel.text = $"[{m.Id}] {m.Name}：HP {m.Hp}，攻击 {m.Attack}，掉落 {(drop != null ? drop.Name : $"#{m.DropItemId}")}" +
                                    "（数据来自 monster.xlsx——运行期与 JSON 源无任何区别）";
            }, CodeRef.Here("t.TbMonster.DataList[_monsterCursor", "本处查表语句 · TbMonster + GetOrDefault 取掉落"));

            var globalLabel = host.AddValueDisplay("");
            host.AddActionRow("读全局配置（TbGlobalConfig，one 模式单例表）", () =>
            {
                if (config.State.CurrentValue != ConfigInitState.Ready) { globalLabel.text = "配置未就绪（看上方加载状态）"; return; }
                var t = this.GetConfig<Tables>();
                var g = t.TbGlobalConfig;
                globalLabel.text = $"背包容量 {g.BagCapacity}，初始金币 {g.InitialGold}，新手物品 [{string.Join(", ", g.NewbieItemIds)}]";
            }, CodeRef.Here("var g = t.TbGlobalConfig", "本处查表语句 · one 模式直接读字段"));

            host.AddSubNote("map 表按主键取用 `TbItem.Get(id)` / `TbItem[id]`（缺键抛异常）或 `GetOrDefault(id)`；全量遍历用 `DataList`；one 模式表（如 `TbGlobalConfig`）" +
                            "全表只有一条记录、直接读字段。这些访问器都是生成代码自带的——这个链接直接看生成出来的 `TbItem`（只在「想看生成代码长什么样」时点）。",
                new CodeRef("Assets/Game/Framework/Demo/Config/Gen/TbItem.cs", "public Item Get(int key)", "生成代码 · TbItem 的 Get / GetOrDefault / DataList"));
            host.AddSubNote("为什么没有做成静态 `TbItem[id]`：那必须隐藏一个“当前 Tables”，会丢掉父子 Context 覆盖、多配置集和测试隔离。" +
                            "框架只省掉没有信息量的解析样板，保留 `this.GetConfig<Tables>()` 这一小段有意义的作用域声明；高频调用把返回值缓存为字段后就是 `_tables.TbItem[id]`。",
                new CodeRef("Assets/Game/Framework/Config/ConfigAccessExtensions.cs", "private static TTables RequireReady", "短入口仍保留 Context 与 readiness 防线"));

            // ── 3. 运行期：自加载的配置服务（Utility，不是 System） ──
            host.AddSectionTitle("运行期：一个自加载的配置服务（是 Utility，不是 System）");
            host.AddNote("场景里 `ConfigService` 节点只挂一个组件 `DemoConfigUtility`——配置做成**自加载的 Utility 服务**：进游戏自己按清单预载数据、" +
                         "构造表根、对外只读暴露。**为什么是 Utility 而不是 Model / System**：配置是全层只读引用数据，而本框架 Model 把 View 挡在外面" +
                         "（View 没有 `GetModel`），做成 Utility 才让 View 也能直读（View 有 `ICanGetUtility`）；配置加载又没有资源系统那种多包 / CDN / 下载的" +
                         "复杂度，不必拆出 System——一个组件就够（资源系统才是「Model + System + Utility」三件套，因为它加载复杂、且持的是可变运行期配置）。",
                new CodeRef("Assets/Game/Framework/Demo/Config/DemoConfigUtility.cs", "class DemoConfigUtility", "Demo 接入 · 仅两个 override"));
#if UNITY_EDITOR
            host.AddActionRow("选中 ConfigService 节点（DemoConfigUtility · 自加载配置服务）",
                () => DemoEditorNav.PingSceneObject(GameObject.Find("ConfigService")));
#endif

            host.AddSubNote("接入就是一个一行子类闭合泛型 `class DemoConfigUtility : MonoConfigUtilityBase<Tables>`，只补两个 override——" +
                            "它们是框架（后端无关）与项目（Luban）之间仅有的接缝：");
            host.AddTable(
                new[] { "override", "回答的问题", "demo 实现" },
                new[] { "`TableFiles`", "预载哪些数据文件（数据清单）", "直接交还生成的 `LubanTableManifest.Files`" },
                new[] { "`CreateTables`", "字节怎么变表根（反序列化适配器）", "`new Tables(f => new ByteBuf(getBytes(f)))`——唯一碰后端类型的一行" });
            host.AddSubNote("其余通用编排（并行预载、异步→同步桥、加载状态机、按接口注册、生命周期）全在框架基类里。`TableFiles` 是**数据清单**、" +
                            "`CreateTables` 是**反序列化适配器**——换后端（JSON / 自定义格式）只改 `CreateTables` 一行，`TableFiles` 照旧；" +
                            "多套配置就是多个闭合不同 `Tables` 的子类，各有自己这两块。",
                new CodeRef("Assets/Game/Framework/Config/MonoConfigUtilityBase.cs", "protected abstract IReadOnlyList<string> TableFiles", "框架基类 · 两个 abstract 接缝"));
            host.AddSubNote("框架会在任何资源 I/O 前快照并校验清单：空项、重复项会直接失败，`CreateTables` 返回 null 也会被拒绝。这样生成管线或 Adapter 的错误在配置边界就暴露，" +
                            "不会加载到一半才留下难解释的部分副作用；失败的原始异常既写入 `Log`，也由 `EnsureReady` 交还给需要阻断流程的调用方。",
                new CodeRef("Assets/Game/Framework/Config/MonoConfigUtilityBase.cs", "private IReadOnlyList<string> SnapshotAndValidateTableFiles()", "清单防线 · I/O 前 fail-fast"));
            host.AddSubNote("组件上还有两个 Inspector 字段：`_packageName`（配置数据在哪个资源包，留空 = 默认包）与 `_initializePackageIfIdle`" +
                            "（该包没开「自动初始化」时，由配置服务在加载前先初始化它——合规启动 / DLC 懒加载等场景才需要；demo 这套勾上了）。",
                new CodeRef("Assets/Game/Framework/Config/MonoConfigUtilityBase.cs", "private string _packageName", "两个 Inspector 字段：包名 / 按需初始化"));
            host.AddSubNote("解耦边界：框架 `Game.Framework.Config` 模块不引用 Luban——它只做「清单 → 预载字节 → 调抽象工厂」的通用编排；" +
                            "整条链路里接触 Luban 类型（`ByteBuf`）的只有上面 `CreateTables` 那一行。换任何配置后端，框架模块原样可用。",
                new CodeRef("Assets/Game/Framework/Config/MonoConfigUtilityBase.cs", "class MonoConfigUtilityBase", "框架基类（后端无关，自加载）"));

            // ── 4. 改表工作流 ──
            host.AddSectionTitle("改一张表的完整工作流");
            host.AddStep("①", "改数据：`Demo/Configs~/Datas/item.json`（JSON，diff 可读）或 `monster.xlsx`（Excel/WPS 直接编辑）；改表结构 / 加表：`Demo/Configs~/Defines/demo.xml`。");
            host.AddStep("②", "打开「SSFramework/代码生成/配置表 (Luban)」工作台，确认输入与输出后点生成：CLI 先写暂存区，代码 / 数据 / 清单校验通过才一起差量发布（Play 中会被拒绝，先停）。");
            host.AddSubNote("生成失败不会拿半套新产物覆盖旧配置；发布中断会恢复代码与数据两棵目录树。内容完全未变时也不会重写文件或触发无谓编译。");
            host.AddStep("③", "重新 Play 查看——配置在启动时一次性加载（只读数据不做运行中增量更新）。");
            host.AddActionRow("打开表定义与数据目录（Demo/Configs~/）", () => OpenConfigSource());

            // ── 5. 进阶：多套配置并存 ──
            host.AddSectionTitle("进阶 · 多套配置并存：demo 是一套自洽样例");
            host.AddNote("这套 demo 配置自成一套——profile + 源（`Configs~/`）+ 生成代码（`Demo/Config/Gen/`）+ 数据（`Demo/Res/Configs/`）全在 `Demo/` 内，" +
                         "随 demo 程序集（带 `UNITY_EDITOR` 约束）与样例资源包一并被正式打包排除。正式游戏在自己目录里建**另一个** `LubanConfigProfile`" +
                         "（各自的 `luban.conf` 源 + 输出 + 命名空间）即可与 demo 并存；「生成全部」逐套生成，定位 / 打开目录 / 单独生成都在「配置总览」窗口。");
            host.AddActionRow("打开配置总览（定位 / 打开目录 / 单独生成）", () => RunMenu(OverviewMenu),
                new CodeRef("Assets/Game/Framework/Config/Editor/LubanConfigOverviewWindow.cs", "class LubanConfigOverviewWindow", "多套配置的集中视图"));
            host.AddSubNote("「多套配置」同时就是**懒加载的落点**——按需加载分两个粒度看：**单表**没有、也不建议（生成的 `Tables` 一次性构造全表、" +
                            "且跨表 `ResolveRef` 要全表在场，配置又是小体积只读数据，全量预载最省心）；真要「用到才加载」就**按配置集拆**：把 DLC / 活动 / " +
                            "巨表做成**另一套** `Tables` + 另一个配置服务，让它的组件晚点才实例化（进对应玩法时才挂上 / 放进按需创建的子 Context），那套就用到才加载——" +
                            "数据再放非自动初始化的包，还能顺带按需下载。下载（包级）+ 配置集拆分（set 级），都是组合现成原语，框架不另设单表 lazy API。");

            host.AddTip("深度阅读：docs/framework-guide.md §16（用法手册：接入步骤 / 换 Excel 数据源 / 命名空间坑）、docs/adr/0009（设计取舍）。");
        }

        private static void RunMenu(string path)
        {
            if (!UnityEditor.EditorApplication.ExecuteMenuItem(path))
                Debug.LogWarning($"[ConfigTableModule] 菜单执行失败：{path}（菜单路径变更？）");
        }

        // 在资源管理器里打开 demo 自带的表定义 / 数据源目录（~ 后缀 Unity 不导入，故直接走文件系统而非资产 ping）。
        private static void OpenConfigSource()
        {
            string full = Path.GetFullPath(ConfigSourceDir);
            if (Directory.Exists(full)) UnityEditor.EditorUtility.RevealInFinder(full);
            else Debug.LogWarning($"[ConfigTableModule] 源目录不存在：{full}");
        }
    }
}
