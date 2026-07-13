using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 被嵌进 UI Toolkit 的一块「活」UGUI 面板：代码搭建背景 + TMP 实时文本 + 旋转指针，每帧更新——
    /// 用来证明 RenderTexture 桥显示的是<b>实时</b> UGUI 渲染（含 TMP），不是静态快照。
    /// 放在被 <c>MonoUGuiEmbed</c> 实例化的 prefab 根上；本身<b>不含 Canvas</b>（由桥的托管 Canvas 承载）。
    /// </summary>
    public sealed class DemoUGuiEmbedPanel : MonoBehaviour
    {
        private TMP_Text _text;
        private RectTransform _spinner;

        private void Awake()
        {
            // 先加背景 Image：顺带确保根上有 RectTransform（Graphic 会自动补），再取来铺满父级。
            var bg = gameObject.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.13f, 0.20f, 0.92f);
            Stretch(GetComponent<RectTransform>());

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(transform, worldPositionStays: false);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            Stretch(_text.rectTransform);
            _text.alignment = TextAlignmentOptions.Center;
            _text.enableWordWrapping = true;
            _text.fontSize = 30;
            _text.color = Color.white;

            var spinGo = new GameObject("Spinner", typeof(RectTransform));
            spinGo.transform.SetParent(transform, worldPositionStays: false);
            _spinner = spinGo.GetComponent<RectTransform>();
            _spinner.anchorMin = _spinner.anchorMax = new Vector2(0.5f, 0.20f);
            _spinner.sizeDelta = new Vector2(46f, 46f);
            _spinner.anchoredPosition = Vector2.zero;
            spinGo.AddComponent<Image>().color = new Color(0.30f, 0.75f, 1f, 1f);
        }

        private void Update()
        {
            if (_text != null)
                _text.text = $"UGUI 实时渲染（含 TMP）\n帧 {Time.frameCount}\n{System.DateTime.Now:HH:mm:ss}";
            if (_spinner != null)
                _spinner.localRotation = Quaternion.Euler(0f, 0f, -Time.time * 90f); // 匀速旋转 = 肉眼可见的实时性
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
