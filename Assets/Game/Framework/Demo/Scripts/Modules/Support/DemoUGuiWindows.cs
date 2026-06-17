using Game.Framework.Common;
using Game.Framework.UI;
using Game.Framework.UI.UGui;
using Game.Framework.Utility;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 「UI 框架」章的 UGUI 后端演示窗口：代码搭建（无 prefab，走 <c>UGuiBackend</c> 的 Asset 留空路径）。
    /// 与 Toolkit 的 <see cref="DemoCounterWindow"/> 一一对应、复用同一份 <c>MonoScoreModel</c>——
    /// 证明同一套开窗 API 在 UGUI / UI Toolkit 两套渲染后端行为一致。
    /// </summary>
    [UIWindow(Layer = UILayer.Window)]
    public sealed class UGuiCounterWindow : UGuiWindowBase
    {
        // UGUI 窗口接线放 OnCreated（不覆写 Awake，注入由 MonoViewBase 负责）。这里用代码搭 UGUI 控件。
        protected override void OnCreated()
        {
            var card = UGuiKit.Card((RectTransform)transform, 340, 210);
            // 从正中错开到偏右下：让它与居中的 Toolkit 窗口同屏并存、不互相遮挡（演示两套 UI 同屏共存）。
            card.anchoredPosition = new Vector2(260, -150);
            UGuiKit.Badge(card, "UGUI", 86);                       // 绿色后端标识，贴顶（与 Toolkit 窗口的蓝标对照）
            UGuiKit.Label(card, "计数窗口（代码搭建）", 56, 15, FontStyle.Bold);
            var score = UGuiKit.Label(card, "", 26);
            var add = UGuiKit.Btn(card, "+1（ExecuteCommand 写）", -16);
            var close = UGuiKit.Btn(card, "关闭", -60);

            // 只读订阅查询 Command（与 Toolkit 窗口、UGUI/UIToolkit View 章共用同一份 MonoScoreModel）。
            Bag.Subscribe(this.ExecuteCommand(new GetMonoScoreCommand()), v => score.text = $"Score: {v}");
            // 只写经 ExecuteCommand。
            Bag.Subscribe(add.onClick, () => this.ExecuteCommand(new RaiseMonoScoreCommand()));
            // 关闭：经本窗口所属 Context 的 IUIUtility 关掉自己。
            Bag.Subscribe(close.onClick, () => this.GetUtility<IUIUtility>().Close(this));
        }
    }

    /// <summary>极简 UGUI 代码搭建工具——给 demo 用的居中卡片 / 文本 / 按钮，不追求复用，够本章演示即可。</summary>
    internal static class UGuiKit
    {
        private static Font _font;
        private static Font Font => _font != null ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        public static RectTransform Card(RectTransform parent, float w, float h)
        {
            var rt = NewRect("Card", parent, new Vector2(w, h), Vector2.zero);
            rt.gameObject.AddComponent<Image>().color = new Color(0.16f, 0.18f, 0.22f, 0.98f);
            return rt;
        }

        // 绿色后端标识药丸（与 Toolkit 章 DemoWindowKit.Badge 的蓝色成对）：一眼区分这是 UGUI 搭的窗口。
        public static void Badge(RectTransform parent, string text, float y)
        {
            var rt = NewRect("Badge", parent, new Vector2(84, 20), new Vector2(0, y));
            rt.gameObject.AddComponent<Image>().color = new Color(0.22f, 0.55f, 0.30f);
            var trt = NewRect("Text", rt, Vector2.zero, Vector2.zero);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var t = trt.gameObject.AddComponent<Text>();
            t.text = text; t.font = Font; t.fontSize = 12; t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
        }

        public static Text Label(RectTransform parent, string text, float y, int fontSize = 18, FontStyle style = FontStyle.Normal)
        {
            var rt = NewRect("Label", parent, new Vector2(300, 30), new Vector2(0, y));
            var t = rt.gameObject.AddComponent<Text>();
            t.text = text; t.font = Font; t.fontSize = fontSize; t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter; t.color = new Color(0.85f, 0.88f, 0.95f);
            return t;
        }

        public static Button Btn(RectTransform parent, string text, float y)
        {
            var rt = NewRect("Button", parent, new Vector2(260, 34), new Vector2(0, y));
            rt.gameObject.AddComponent<Image>().color = new Color(0.25f, 0.35f, 0.5f);
            var btn = rt.gameObject.AddComponent<Button>();

            var trt = NewRect("Text", rt, Vector2.zero, Vector2.zero);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var t = trt.gameObject.AddComponent<Text>();
            t.text = text; t.font = Font; t.fontSize = 15;
            t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
            return btn;
        }

        private static RectTransform NewRect(string name, RectTransform parent, Vector2 size, Vector2 anchoredPos)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;
            return rt;
        }
    }
}
