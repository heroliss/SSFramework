using System.IO;
using DemoCfg;
using Game.Framework.Common;
using Game.Framework.Demo.Core;
using R3;
using UnityEngine;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 进阶·配置表：讲 Luban 集成的构建期 / 运行期分界——表定义（XML）与数据（JSON / Excel）放 demo 自带的 Configs~/（~ 后缀 Unity 不导入），
    /// 菜单跑 CLI 生成 C# 类 + 二进制数据；运行期由一个自加载的配置 Utility 服务持表（各层直读），
    /// 数据文件走资源包通道（可热更）。查询按钮真实读 Play 中加载好的表。
    /// </summary>
    public sealed class ConfigTableModule : DemoModuleBase
    {
        private const string GenerateMenu = "SSFramework/配置表构建/生成全部 (代码 + 数据)";
        private const string OverviewMenu = "SSFramework/配置表构建/配置总览 (定位 · 打开目录 · 生成)";
        // demo 自带源目录（构建期输入，~ 后缀 Unity 不导入）——直接打开，不再走「单套」菜单。
        private const string ConfigSourceDir = "Assets/Game/Framework/Demo/Configs~";

        // View 侧的轮巡游标（纯展示状态，不属于任何 Model）。
        private int _itemCursor;
        private int _monsterCursor;

        public override string Id => "config-table";
        public override string Title => "配置表 · Luban";
        public override string Category => "进阶";
        public override int Order => 30;
        public override string Summary =>
            "构建期菜单跑 Luban CLI：表定义（XML）+ 数据（JSON / Excel）→ 生成 C# 类 + 二进制数据 + 表清单；运行期按清单预载、构造 Tables、" +
            "Model 持有只读暴露——镜像资源系统三段式，数据随资源包热更。查询按钮真实读表。深度见 framework-guide §16 / ADR-0009。";

        public override void Build(DemoModuleHost host)
        {
            // ── 心智模型 ──
            host.AddSectionTitle("心智模型：构建期生成，运行期只是读字节");
            host.AddNote("表定义（XML）与数据（JSON / Excel）放在 demo 目录下的 `Configs~/`（`~` 后缀让 Unity 不导入，纯构建期输入）；构建期菜单跑 Luban CLI，" +
                         "产出三样东西后运行期对「Excel/JSON/校验」零感知——加载就是读字节、构造一次 `Tables`、之后纯内存查询。" +
                         "两种数据源各有活样例：`item.json`（文本，git diff 可读）与 `monster.xlsx`（策划顺手），表定义里 `input` 一个属性决定从哪读，可同表混搭。");
            host.AddTable(
                new[] { "产物", "落点", "谁消费" },
                new[] { "配置 C# 类（Tables / TbItem / Item…）", "`Demo/Config/Gen/`", "业务代码（强类型查表）" },
                new[] { "二进制数据（*.bytes）", "`Demo/Res/Configs/`（资源收集范围内）", "运行期 `Bag.LoadBytes` 直读字节" },
                new[] { "表清单（LubanTableManifest.g.cs）", "随生成代码", "配置服务据此并行预载" });
            host.AddSubNote("为什么要表清单：生成的 `Tables` 构造函数是同步逐表要字节，而框架资源加载是异步——先按清单把全部数据并行预载进内存，" +
                            "再同步构造。清单与代码/数据同一次生成，不存在手工维护漏表（机制同热更代码包的 manifest）。",
                new CodeRef("Assets/Game/Framework/Config/Editor/LubanCodeGenerator.cs", "class LubanCodeGenerator", "生成管线（CLI 调用 + 清单）"));

            // ── 运行期：自加载 Utility ──
            host.AddSectionTitle("运行期：自加载的配置服务（Utility）");
            host.AddNote("场景里 `ConfigSystem` 节点只挂一个组件 `DemoConfigUtility`——配置做成**自加载的 Utility 服务**：" +
                         "进游戏自己按清单预载数据、构造表根、对外只读暴露。各层（含 View）经 `GetUtility<IConfigUtility<Tables>>()` 直读，无需查询 Command。" +
                         "配置是静态只读引用数据（生成的 `Tables` 本就是数据模型），故不占 Model 层、也不像资源系统那样拆三件套（配置加载没有多包 / CDN / 下载的复杂度，一个组件够了）。",
                new CodeRef("Assets/Game/Framework/Demo/Config/DemoConfigUtility.cs", "class DemoConfigUtility", "Demo 接入（仅两个 override）"));
            host.AddSubNote("解耦边界：框架 `Game.Framework.Config` 模块不引用 Luban——它只做「清单 → 预载字节 → 调抽象工厂」的通用编排；" +
                            "整条链路里接触 Luban 类型（ByteBuf）的只有 Demo 子类工厂里那一行。换任何配置后端（JSON / 自定义格式），框架模块原样可用。",
                new CodeRef("Assets/Game/Framework/Config/MonoConfigUtilityBase.cs", "class MonoConfigUtilityBase", "框架基类（后端无关，自加载）"));

            // ── 现场演示 ──
            host.AddSectionTitle("演示：真实读 Play 中加载好的表");

            // 配置是基础设施服务：各层（含本 demo 模块、真实 View）直接 GetUtility 取，无需查询 Command 绕行。
            var config = this.GetUtility<IConfigUtility<Tables>>();

            var stateLabel = host.AddValueDisplay("", new CodeRef("Assets/Game/Framework/Config/IConfigUtility.cs", "interface IConfigUtility", "IConfigUtility · 全层可取的配置服务"));
            Bag.Subscribe(config.State, s => stateLabel.text = $"加载状态：{s}");

            var itemLabel = host.AddValueDisplay("（点下方按钮查表）");
            host.AddActionRow("查下一条物品（轮巡 TbItem.DataList）", () =>
            {
                var t = config.Tables; // 普通取值（配置只读、加载后不变），无需 .CurrentValue
                if (t == null) { itemLabel.text = "配置未就绪（看上方加载状态）"; return; }
                if (t.TbItem.DataList.Count == 0) { itemLabel.text = "TbItem 没有数据（Datas/item.json 是空的？）"; return; }
                var item = t.TbItem.DataList[_itemCursor++ % t.TbItem.DataList.Count];
                itemLabel.text = $"[{item.Id}] {item.Name}（{item.Quality}）售价 {item.Price}，堆叠上限 {item.StackLimit} —— {item.Desc}";
            }, new CodeRef("Assets/Game/Framework/Config/IConfigUtility.cs", "TTables Tables", "config.Tables 直读（普通取值）"));

            var monsterLabel = host.AddValueDisplay("（点下方按钮查表）");
            host.AddActionRow("查下一条怪物（轮巡 TbMonster，Excel 数据源）", () =>
            {
                var t = config.Tables; // 普通取值（配置只读、加载后不变），无需 .CurrentValue
                if (t == null) { monsterLabel.text = "配置未就绪（看上方加载状态）"; return; }
                if (t.TbMonster.DataList.Count == 0) { monsterLabel.text = "TbMonster 没有数据（Datas/monster.xlsx 是空的？）"; return; }
                var m = t.TbMonster.DataList[_monsterCursor++ % t.TbMonster.DataList.Count];
                var drop = t.TbItem.GetOrDefault(m.DropItemId);
                monsterLabel.text = $"[{m.Id}] {m.Name}：HP {m.Hp}，攻击 {m.Attack}，掉落 {(drop != null ? drop.Name : $"#{m.DropItemId}")}" +
                                    "（数据来自 monster.xlsx——运行期与 JSON 源无任何区别）";
            }, new CodeRef("Assets/Game/Framework/Demo/Config/Gen/TbMonster.cs", "class TbMonster", "Excel 生成的表类"));

            var globalLabel = host.AddValueDisplay("");
            host.AddActionRow("读全局配置（TbGlobalConfig，one 模式单例表）", () =>
            {
                var t = config.Tables; // 普通取值（配置只读、加载后不变），无需 .CurrentValue
                if (t == null) { globalLabel.text = "配置未就绪（看上方加载状态）"; return; }
                var g = t.TbGlobalConfig;
                globalLabel.text = $"背包容量 {g.BagCapacity}，初始金币 {g.InitialGold}，新手物品 [{string.Join(", ", g.NewbieItemIds)}]";
            }, new CodeRef("Assets/Game/Framework/Demo/Config/Gen/Tables.cs", "class Tables", "生成的表根（Tables）"));

            host.AddSubNote("map 表按主键取用 `TbItem.Get(id)` / `TbItem[id]`（缺键抛异常）或 `GetOrDefault(id)`；全量遍历用 `DataList`。这些访问器都是生成代码自带的。");

            // ── 改表工作流 ──
            host.AddSectionTitle("改一张表的完整工作流");
            host.AddStep("①", "改数据：`Demo/Configs~/Datas/item.json`（JSON，diff 可读）或 `monster.xlsx`（Excel/WPS 直接编辑）；改表结构/加表：`Demo/Configs~/Defines/demo.xml`。");
            host.AddStep("②", "菜单「SSFramework/配置表构建/生成全部」：代码 / 数据 / 清单一次刷新（Play 中会被拒绝，先停）。");
            host.AddStep("③", "重新 Play 查看——配置在启动时一次性加载（只读数据不做运行中增量更新）。");
            host.AddActionRow("打开表定义与数据目录（Demo/Configs~/）", () => OpenConfigSource());
            host.AddActionRow("生成配置代码 + 数据（真实跑 Luban CLI）", () => RunMenu(GenerateMenu),
                new CodeRef("Assets/Game/Framework/Config/Editor/LubanCodeGenerator.cs", "class LubanCodeGenerator", "生成管线（跑 Luban CLI + 写清单）"));

            // ── demo 与正式游戏并存 ──
            host.AddSectionTitle("多套配置并存：demo 是一套自洽样例");
            host.AddNote("这套 demo 配置自成一套——profile + 源（`Configs~/`）+ 生成代码（`Demo/Config/Gen/`）+ 数据（`Demo/Res/Configs/`）全在 `Demo/` 内，" +
                         "随 demo 程序集（带 `UNITY_EDITOR` 约束）与样例资源包一并被正式打包排除。正式游戏在自己目录里建**另一个** `LubanConfigProfile`" +
                         "（各自的 `luban.conf` 源 + 输出 + 命名空间）即可与 demo 并存；「生成全部」逐套生成，定位 / 打开目录 / 单独生成都在「配置总览」窗口。");
            host.AddActionRow("打开配置总览（定位 / 打开目录 / 单独生成）", () => RunMenu(OverviewMenu),
                new CodeRef("Assets/Game/Framework/Config/Editor/LubanConfigOverviewWindow.cs", "class LubanConfigOverviewWindow", "多套配置的集中视图"));

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
