using Game.Framework.Demo.Core;
using Game.Framework.UI.Bridge;
using Game.Framework.UI.Toolkit;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·UI 融合：把活的 UGUI（含 TMP）以 RenderTexture 桥「真嵌入」进 UI Toolkit 内容流——能被 ScrollView
    /// 裁剪 / 滚动（区别于「浮层对齐」伪嵌入）；开 <c>Interactive</c> 后指针事件穿透 RT，嵌入的按钮 / Slider 可点可拖。
    /// 演示 <c>MonoUGuiEmbed</c> 一键接法。
    /// </summary>
    public sealed class UIEmbedModule : DemoModuleBase
    {
        public override string Id => "ui-embed";
        public override string Title => "UI 融合 · UGUI 嵌进 Toolkit";
        public override string Category => "能力";
        public override int Order => 75;
        public override string Summary =>
            "RenderTexture Bridge 把活的 UGUI/TMP 嵌进 UI Toolkit 内容流，因此能随 ScrollView 裁剪和滚动。" +
            "可选输入转发支持点击、悬停、拖拽和滚轮。";

        private const string PanelFile = "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoUGuiEmbedPanel.cs";
        private const string InteractivePanelFile = "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoUGuiInteractivePanel.cs";
        private const string BridgeFile = "Assets/Game/Framework/UI.Bridge/MonoUGuiEmbed.cs";
        private const string ForwarderFile = "Assets/Game/Framework/UI.Bridge/UGuiEmbedInputForwarder.cs";

        public override void Build(DemoModuleHost host)
        {
            // ── 定位 ──
            host.AddPositioning("UGUI / 相机内容当「真内容」嵌进 Toolkit");
            host.AddNote("UGUI 与 UI Toolkit 是两套渲染系统，谁也不能当对方的子节点。要把 UGUI/TMP（或 3D 预览、小地图）放进 Toolkit 布局，正法是 `RenderTexture` 桥：隔离相机把 UGUI 渲进纹理、纹理当 Toolkit 元素显示——于是它是 Toolkit 的**真内容**，能被 ScrollView 裁剪 / 滚动、被后续元素遮挡。");
            host.AddNote("对比：对象池右栏用的是「浮层对齐」——把 UGUI Canvas 盖在面板之上、每帧对齐占位框。那套简单但**浮在最上层**：不能被裁剪 / 滚动，还要防 `worldBound` 退化成 NaN。两套各有用武之地，按「要不要被 Toolkit 裁剪」选。");

            host.AddSectionTitle("三个零件");
            host.AddConcept("RenderTextureElement", "Toolkit 显示元素：显示一张 RenderTexture，按布局尺寸 × DPI 上报所需像素；超预算时等比降采样，不拥有纹理。");
            host.AddConcept("CameraTextureRenderer", "相机→RenderTexture 生命周期：按需重建等大纹理、接相机。后端无关（也能拍 3D 道具预览）。");
            host.AddConcept("MonoUGuiEmbed", "一键组件：自动装配隔离相机 + CanvasScaler + Canvas + RT；逻辑布局与采样分辨率分离，开 `Interactive` 可转发指针。");

            // ── 只读显示 ──
            var display = FindEmbed("UGuiEmbedHost");
            host.AddSectionTitle("只读显示（往下滚，它会被裁剪 / 滚动）");
            if (display == null)
            {
                host.AddNote("场景没找到只读嵌入宿主 `UGuiEmbedHost`（挂 `MonoUGuiEmbed` + 指定被嵌面板 prefab + 隔离层）。挂好后回本章即可看到。");
            }
            else
            {
                var dview = new RenderTextureElement();
                dview.style.height = 190;
                dview.style.marginTop = 6;
                dview.style.marginBottom = 6;
                host.Content.Add(dview);
                display.Bind(dview);
                Bag.Add(Disposable.Create(display.Unbind)); // 切走本章解绑，清纹理显示、断尺寸订阅

                host.AddNote("上面是**活的 UGUI 面板**（背景 + TMP 实时文本 + 旋转指针）渲进 RenderTexture、当 Toolkit 元素显示。帧号 / 时间每帧在跳——实时渲染不是快照。往下滚动本章，它像普通 Toolkit 内容一样被滚动容器裁剪。",
                    new CodeRef(PanelFile, "class DemoUGuiEmbedPanel", "被嵌的活 UGUI 面板"));
                host.AddNote("接法三步：新建 `RenderTextureElement` 放进 Toolkit 内容 → 拿场景里的 `MonoUGuiEmbed` → `display.Bind(dview)`。纹理尺寸随元素布局自动同步，业务不碰相机 / RT。",
                    CodeRef.Here("display.Bind(dview)", "Bind 用法"));
                host.AddSubNote("一键组件与其相机 / RT 装配的完整契约看框架实现（隔离层、刷新模式、透明背景合成都在这）。",
                    new CodeRef(BridgeFile, "class MonoUGuiEmbed", "MonoUGuiEmbed · 框架组件"));
                int normalTextureBudget = dview.MaxTextureSize;
                host.AddSubNote("`MaxTextureSize` 是**最长边画质预算**，不是 UGUI 的逻辑布局尺寸：纹理宽高统一降采样，托管 `CanvasScaler` 仍以 Toolkit 内容框排版。调低后只会变糊，宽高比、字体和控件构图都应保持。可用下面两个按钮现场对比。",
                    new CodeRef("Assets/Game/Framework/UI.Toolkit/RenderTextureElement.cs", "public int MaxTextureSize", "低清等比降采样实现"));
                host.AddActionRow("切到 128px 低清（应变糊但不变形）", () => dview.MaxTextureSize = 128);
                host.AddActionRow("恢复正常纹理预算", () => dview.MaxTextureSize = normalTextureBudget);
#if UNITY_EDITOR
                host.AddActionRow("选中只读嵌入宿主 UGuiEmbedHost（看 MonoUGuiEmbed 配置）",
                    () => DemoEditorNav.PingSceneObject(display.gameObject));
#endif
            }

            // ── 交互（输入穿透 v2）──
            var interactive = FindEmbed("UGuiEmbedInteractiveHost");
            host.AddSectionTitle("可交互：输入穿透 RT（点按钮 / 拖 Slider）");
            if (interactive == null)
            {
                host.AddNote("场景没找到交互嵌入宿主 `UGuiEmbedInteractiveHost`（`MonoUGuiEmbed` 的 `Interactive` 勾上）。挂好后回本章即可点 / 拖。");
            }
            else
            {
                var iview = new RenderTextureElement();
                iview.style.height = 220;
                iview.style.marginTop = 6;
                iview.style.marginBottom = 6;
                host.Content.Add(iview);
                interactive.Bind(iview);
                Bag.Add(Disposable.Create(interactive.Unbind));

                host.AddNote("上面这块是**可交互**嵌入：**点 +1 / 重置会改变计数，拖动 Slider 会改变数值**——指针事件经桥从 Toolkit 转发进里面的 UGUI。开关就是 `MonoUGuiEmbed` 的 `Interactive`。",
                    new CodeRef(InteractivePanelFile, "class DemoUGuiInteractivePanel", "被嵌的可交互 UGUI 面板"));
                host.AddNote("接法与只读一模一样——只多勾一个 `Interactive`：`interactive.Bind(iview)` 之后按钮 / Slider 就活了，其余零改动。",
                    CodeRef.Here("interactive.Bind(iview)", "交互 Bind"));
                host.AddSubNote("穿透怎么做的看框架转发器：元素内指针坐标翻成 RT 空间屏幕点 → 禁用注册的 GraphicRaycaster 手动命中 → ExecuteEvents 分发（点击 / 悬停 / 拖拽 / 滚轮）。",
                    new CodeRef(ForwarderFile, "class UGuiEmbedInputForwarder", "UGuiEmbedInputForwarder · 输入转发（看框架实现）"));
#if UNITY_EDITOR
                host.AddActionRow("选中交互嵌入宿主 UGuiEmbedInteractiveHost（看 Interactive 勾选）",
                    () => DemoEditorNav.PingSceneObject(interactive.gameObject));
#endif
            }

            // ── 场景怎么搭（给节点跳转，省得自己翻树）──
            host.AddSectionTitle("场景怎么搭（两步，点按钮直接跳到节点）");
            host.AddNote("① 工程 Tags & Layers 留一个专用隔离层（本 demo 是 `UGuiEmbed`），填进 `MonoUGuiEmbed`；② 主相机 `cullingMask` **剔除**该层——否则嵌入的 UGUI 会同时漏进游戏画面。被嵌内容 / 托管相机是运行时建的，进 Play 后可在 Hierarchy 里看 `[UGuiEmbed Rig]`。");
#if UNITY_EDITOR
            host.AddActionRow("选中主相机（看它的 Culling Mask 剔除了 UGuiEmbed 层）",
                () => { if (Camera.main != null) DemoEditorNav.PingSceneObject(Camera.main.gameObject); });
#endif

            // ── 边界 ──
            host.AddSectionTitle("刻意不做");
            host.AddNote("输入穿透覆盖**指针**（点击 / 悬停 / 拖拽 / 滚轮）——够按钮 / 开关 / Slider / ScrollRect 用。**文本输入 / IME、多点触控不做**：成本陡增、场景罕见，要在嵌入 UGUI 里打字就直接用原生 UGUI 层，别走桥。");
        }

        // 按宿主 GameObject 名找对应的 MonoUGuiEmbed（本章有只读 / 交互两个嵌入宿主，按名区分）。
        private static MonoUGuiEmbed FindEmbed(string hostName)
        {
            var go = GameObject.Find(hostName);
            return go != null ? go.GetComponent<MonoUGuiEmbed>() : null;
        }
    }
}
