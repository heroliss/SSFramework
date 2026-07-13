using Game.Framework.Demo.Core;
using Game.Framework.UI.Bridge;
using Game.Framework.UI.Toolkit;
using R3;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 能力·UI 融合：把一段活的 UGUI（含 TMP）以 RenderTexture 桥「真嵌入」进 UI Toolkit 内容流——
    /// 能被 ScrollView 裁剪 / 滚动，区别于「浮层对齐」把 Canvas 盖在面板上的伪嵌入。演示 <c>MonoUGuiEmbed</c> 一键接法。
    /// </summary>
    public sealed class UIEmbedModule : DemoModuleBase
    {
        public override string Id => "ui-embed";
        public override string Title => "UI 融合 · UGUI 嵌进 Toolkit";
        public override string Category => "能力";
        public override int Order => 75;
        public override string Summary =>
            "把活的 UGUI/TMP 以 RenderTexture 桥嵌进 UI Toolkit 内容流：隔离相机把 UGUI 渲进纹理、当 Toolkit 元素显示，能被 ScrollView 裁剪 / 滚动（伪嵌入做不到）。一键组件 MonoUGuiEmbed，v1 只读显示。";

        private const string PanelFile = "Assets/Game/Framework/Demo/Scripts/Modules/Support/DemoUGuiEmbedPanel.cs";
        private const string BridgeFile = "Assets/Game/Framework/UI.Bridge/MonoUGuiEmbed.cs";

        public override void Build(DemoModuleHost host)
        {
            var embed = Object.FindFirstObjectByType<MonoUGuiEmbed>();

            // ── 定位 ──
            host.AddSectionTitle("定位：UGUI / 相机内容当「真内容」嵌进 Toolkit");
            host.AddNote("UGUI 与 UI Toolkit 是两套渲染系统，谁也不能当对方的子节点。要把 UGUI/TMP（或 3D 预览、小地图）放进 Toolkit 布局，正法是 `RenderTexture` 桥：隔离相机把 UGUI 渲进纹理、纹理当 Toolkit 元素显示——于是它是 Toolkit 的**真内容**，能被 ScrollView 裁剪 / 滚动、被后续元素遮挡。");
            host.AddNote("对比：对象池右栏、字体章浮层用的是「浮层对齐」——把 UGUI Canvas 盖在面板之上、每帧对齐占位框。那套简单但**浮在最上层**：不能被裁剪 / 滚动，还要防 `worldBound` 退化成 NaN。两套各有用武之地，按「要不要被 Toolkit 裁剪」选。");

            host.AddSectionTitle("三个零件");
            host.AddConcept("RenderTextureElement", "Toolkit 显示元素：显示一张 RenderTexture，按布局尺寸 × DPI 上报所需像素，不拥有纹理。");
            host.AddConcept("CameraTextureRenderer", "相机→RenderTexture 生命周期：按需重建等大纹理、接相机。后端无关（也能拍 3D 道具预览）。");
            host.AddConcept("MonoUGuiEmbed", "一键组件：给个 UGUI 面板 prefab，自动装配隔离相机 + Canvas + RT，`Bind` 到显示元素即可。");

            if (embed == null)
            {
                host.AddSectionTitle("场景未挂 UI 嵌入宿主");
                host.AddNote("本章需要 demo 场景里挂一个配好 `MonoUGuiEmbed`（指定被嵌 UGUI 面板 prefab + 隔离层）的节点。当前找不到——挂好后回到本章即可看到嵌入的活面板。");
                return;
            }

            // ── 真嵌入展示 ──
            host.AddSectionTitle("看它嵌在这里（往下滚，它会被裁剪 / 滚动）");
            var view = new RenderTextureElement();
            view.style.height = 200;
            view.style.marginTop = 6;
            view.style.marginBottom = 6;
            host.Content.Add(view);

            embed.Bind(view);
            Bag.Add(Disposable.Create(embed.Unbind)); // 切走本章时解绑，清掉纹理显示、断开尺寸订阅

            host.AddNote("上面那块就是**活的 UGUI 面板**（背景 + TMP 实时文本 + 旋转指针）渲进 RenderTexture、当 Toolkit 元素显示。帧号 / 时间每帧在跳、指针在转——证明是实时渲染不是快照。往下滚动本章，它像普通 Toolkit 内容一样被滚动容器裁剪。",
                new CodeRef(PanelFile, "class DemoUGuiEmbedPanel", "被嵌的活 UGUI 面板"));
            host.AddNote("接法就三步：新建 `RenderTextureElement` 放进 Toolkit 内容 → 拿到场景里的 `MonoUGuiEmbed` → `embed.Bind(view)`。纹理尺寸随元素布局自动同步，业务不碰相机 / RT。",
                CodeRef.Here("embed.Bind(view)", "Bind 用法"));
            host.AddSubNote("一键组件与其相机 / RT 装配的完整契约看框架实现（隔离层、刷新模式 EveryFrame / OnDemand、透明背景合成都在这）。",
                new CodeRef(BridgeFile, "class MonoUGuiEmbed", "MonoUGuiEmbed · 框架组件"));

            // ── 边界 ──
            host.AddSectionTitle("刻意不做（v1）");
            host.AddNote("**输入不穿透**：事件不会从 Toolkit 传进 RenderTexture 里的 UGUI（公认难点，需坐标翻译 + 假 raycaster）。v1 定位「只读显示」——覆盖 TMP 富文本、3D 道具预览、小地图等绝大多数场景；要交互的 UI 仍直接用 Toolkit / UGUI 各自原生事件。");
        }
    }
}
