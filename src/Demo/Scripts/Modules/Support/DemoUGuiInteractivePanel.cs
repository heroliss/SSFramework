using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 被嵌进 UI Toolkit 的一块<b>可交互</b> UGUI 面板：代码搭建计数文本、<c>+1</c>/<c>重置</c> 按钮和一个 Slider——
    /// 用来验证 RenderTexture 桥的**输入穿透**（点击按钮、拖 Slider 都经桥转发进这里，ADR-0033 §v2 demo）。
    /// 放在被 <c>MonoUGuiEmbed</c>（<c>Interactive=true</c>）实例化的 prefab 根上；本身不含 Canvas。
    /// </summary>
    public sealed class DemoUGuiInteractivePanel : MonoBehaviour
    {
        private TMP_Text _countText;
        private TMP_Text _sliderText;
        private int _count;

        private void Awake()
        {
            var bg = gameObject.AddComponent<Image>();
            bg.color = new Color(0.12f, 0.14f, 0.20f, 0.95f);
            Stretch(GetComponent<RectTransform>());

            _countText = MakeText("Count", new Vector2(0.5f, 0.86f), 28f, "点 +1 / 重置：计数 0");
            MakeButton("+1", new Vector2(0.30f, 0.62f), () => { _count++; RefreshCount(); });
            MakeButton("重置", new Vector2(0.70f, 0.62f), () => { _count = 0; RefreshCount(); });

            _sliderText = MakeText("SliderVal", new Vector2(0.5f, 0.34f), 20f, "拖滑块：0.00");
            var sliderGo = DefaultControls.CreateSlider(default); // 标准控件，handle 拖拽已内部接好
            sliderGo.transform.SetParent(transform, worldPositionStays: false);
            var slider = sliderGo.GetComponent<Slider>();
            var srt = sliderGo.GetComponent<RectTransform>();
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.16f);
            srt.sizeDelta = new Vector2(280f, 20f);
            srt.anchoredPosition = Vector2.zero;
            slider.onValueChanged.AddListener(v => _sliderText.text = $"拖滑块：{v:0.00}");
        }

        private void RefreshCount() => _countText.text = $"点 +1 / 重置：计数 {_count}";

        private TMP_Text MakeText(string name, Vector2 anchor, float size, string text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(transform, worldPositionStays: false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.alignment = TextAlignmentOptions.Center;
            t.fontSize = size;
            t.color = Color.white;
            t.text = text;
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(380f, 44f);
            rt.anchoredPosition = Vector2.zero;
            return t;
        }

        private void MakeButton(string label, Vector2 anchor, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform));
            go.transform.SetParent(transform, worldPositionStays: false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.24f, 0.52f, 0.92f, 1f);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;                 // 按下 / 悬停有色调反馈（顺带证明 hover 也穿透）
            btn.onClick.AddListener(onClick);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(120f, 46f);
            rt.anchoredPosition = Vector2.zero;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, worldPositionStays: false);
            var t = labelGo.AddComponent<TextMeshProUGUI>();
            t.alignment = TextAlignmentOptions.Center;
            t.fontSize = 22f;
            t.color = Color.white;
            t.text = label;
            Stretch(t.rectTransform);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
