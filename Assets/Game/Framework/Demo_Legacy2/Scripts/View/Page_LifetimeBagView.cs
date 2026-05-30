using System.Collections.Generic;
using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 3 — 生命周期 Bag 演示。
    /// </summary>
    /// <remarks>
    /// 动态 Instantiate <see cref="BagChildView"/> 到 <c>_childContainer</c>——每个子 View 在 Awake 时
    /// 订阅 N 个事件到自身 Bag。"Send Ping" 按钮发事件，所有活着的子 View 计数自增；"Destroy 一个"
    /// 则销毁最后一个子 View，其 Bag 自动 Dispose，再发事件就只剩存活的子 View 响应。
    /// </remarks>
    public sealed class Page_LifetimeBagView : MonoViewBase
    {
        [Header("控制按钮")]
        [SerializeField] private Button _spawnBtn;
        [SerializeField] private Button _destroyOneBtn;
        [SerializeField] private Button _pingBtn;

        [Header("子 View")]
        [SerializeField] private Transform _childContainer;
        [SerializeField] private BagChildView _childPrefab;

        [Header("状态文本")]
        [SerializeField] private TMP_Text _aliveCountLabel;

        private readonly List<BagChildView> _children = new();

        protected override void Awake()
        {
            base.Awake();

            Bag.Subscribe(_spawnBtn.onClick,      Spawn);
            Bag.Subscribe(_destroyOneBtn.onClick, DestroyOne);
            Bag.Subscribe(_pingBtn.onClick,       () => this.ExecuteCommand(new Command.SendLogCommand("ping")));

            RefreshAliveLabel();
        }

        private void Spawn()
        {
            var child = Instantiate(_childPrefab, _childContainer);
            _children.Add(child);
            RefreshAliveLabel();
        }

        private void DestroyOne()
        {
            if (_children.Count == 0) return;
            int last = _children.Count - 1;
            var c = _children[last];
            _children.RemoveAt(last);
            if (c != null) Destroy(c.gameObject);
            RefreshAliveLabel();
        }

        private void RefreshAliveLabel()
        {
            _aliveCountLabel.text = $"活着的子 View: {_children.Count}";
        }
    }

}
