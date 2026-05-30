using Cysharp.Threading.Tasks;
using Game.Framework.Common;
using Game.Framework.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.View
{
    /// <summary>
    /// 章节 6 — 资源系统演示。
    /// </summary>
    /// <remarks>
    /// 演示两条加载路径：<br/>
    /// 1. <see cref="AssetReference{T}"/> 拖拽——字段在 Awake 自动绑定 Bag，OnDestroy 自动释放。<br/>
    /// 2. <see cref="DisposableBag.Load{T}"/> 动态路径——handle 自动入 Bag。<br/>
    /// 注意：未配 AssetUtility / YooAsset 包时按钮按下会 Log 错误，但本 Page 不会崩——
    /// 异步异常被 UniTask 转 Logged Exception。
    /// </remarks>
    public sealed class Page_AssetSystemView : MonoViewBase
    {
        [Header("Inspector 拖拽引用")]
        [Tooltip("把任意 Sprite 资产拖入；点 \"Load Static\" 后显示在 _image 上。")]
        [SerializeField] private AssetReference<Sprite> _staticIconRef;

        [Header("动态路径")]
        [Tooltip("Bag.Load<Sprite>(location) 的 location；运行时按下 \"Load Dynamic\" 后加载。")]
        [SerializeField] private string _dynamicLocation = "ui/icon";

        [Header("UI")]
        [SerializeField] private Image _image;
        [SerializeField] private Button _loadStaticBtn;
        [SerializeField] private Button _loadDynamicBtn;
        [SerializeField] private Button _clearBtn;
        [SerializeField] private TMP_Text _statusLabel;

        protected override void Awake()
        {
            base.Awake();

            Bag.Subscribe(_loadStaticBtn.onClick,  () => LoadStatic().Forget());
            Bag.Subscribe(_loadDynamicBtn.onClick, () => LoadDynamic().Forget());
            Bag.Subscribe(_clearBtn.onClick,       Clear);

            SetStatus("待加载");
        }

        private async UniTaskVoid LoadStatic()
        {
            if (_staticIconRef == null || !_staticIconRef.HasGuid)
            {
                SetStatus("AssetReference 未配置");
                return;
            }
            SetStatus("加载中… (AssetReference)");
            try
            {
                var sp = await _staticIconRef.Get();
                _image.sprite = sp;
                SetStatus("[OK] AssetReference 加载完成");
            }
            catch (global::System.Exception e)
            {
                SetStatus($"[FAIL] {e.Message}");
            }
        }

        private async UniTaskVoid LoadDynamic()
        {
            if (string.IsNullOrEmpty(_dynamicLocation))
            {
                SetStatus("dynamic location 为空");
                return;
            }
            SetStatus($"加载中… ({_dynamicLocation})");
            try
            {
                var sp = await Bag.Load<Sprite>(_dynamicLocation);
                _image.sprite = sp;
                SetStatus($"[OK] Bag.Load 完成：{_dynamicLocation}");
            }
            catch (global::System.Exception e)
            {
                SetStatus($"[FAIL] {e.Message}");
            }
        }

        private void Clear()
        {
            _image.sprite = null;
            SetStatus("已清空（Bag 内 handle 仍持有，OnDestroy 时统一释放）");
        }

        private void SetStatus(string s)
        {
            _statusLabel.text = s;
        }
    }
}
