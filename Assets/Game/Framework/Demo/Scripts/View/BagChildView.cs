using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 3 — Bag 演示用的子 View 模板。每个实例订阅 <see cref="Event.LogEvent"/>，
    /// 销毁时 Bag 自动反订阅——验证"无清理代码"。
    /// </summary>
    public sealed class BagChildView : MonoViewBase
    {
        [SerializeField] private TMP_Text _label;
        private int _received;

        protected override void Awake()
        {
            base.Awake();
            _label.text = "Subs: 0";

            Bag.Subscribe<Event.LogEvent>(e =>
            {
                _received++;
                _label.text = $"收到 ping: {_received}";
            });
        }
    }
}
