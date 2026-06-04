using Game.Framework.Pool;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Demo.Modules
{
    /// <summary>
    /// 对象池章 GameObject 池演示用的小方块脚本，挂在 PooledChip prefab 上、实现 <see cref="IPoolable"/>。
    /// 把"复用是真的、OnRent/OnReturn 在 GameObject 上确实会跑"做成肉眼可见的证据，
    /// 与 C# 池段的 <see cref="PooledBox"/>（ConstructCount / Stamp 清零）一一对应：
    /// <list type="bullet">
    /// <item><see cref="InstantiateCount"/> 只在 <see cref="Awake"/> 自增——Awake 仅在真正 Instantiate 时跑一次、
    /// 复用旧实例不会再跑，所以它就是"真正实例化次数"；预热后涨、之后反复 Spawn 不再涨，即证明在复用。</item>
    /// <item><see cref="OnRent"/> 每次 Spawn（含复用旧实例）按"格子位置"在色环上渐变着色——证明取出时 OnRent 一定触发，可在此做激活初始化。</item>
    /// <item><see cref="OnReturn"/> 复位回基色——状态清理放归还侧的示范（对应 PooledBox.OnReturn 把 Stamp 清零）。</item>
    /// </list>
    /// </summary>
    public sealed class PooledChip : MonoBehaviour, IPoolable
    {
        /// <summary>
        /// 真正被 Instantiate 出来的实例数（仅 <see cref="Awake"/> 自增）。复用不触发 Awake，故可作"复用省实例化"的铁证。
        /// 用 static 而非 Model：自增点必须在 <see cref="Awake"/>（只有真正实例化才触发），而此刻实例刚被池 Instantiate、
        /// 尚未 SetParent，够不到任何 Context（池化对象刻意不绑 Context）——它本就是 demo 的进程级诊断量、不是领域状态。
        /// </summary>
        public static int InstantiateCount;

        private Image _image;
        private Color _baseColor;

        private void Awake()
        {
            InstantiateCount++;
            _image = GetComponent<Image>();
            _baseColor = _image.color; // prefab 默认色，OnReturn 复位回它
        }

        // 取出时初始化：颜色按"在容器里的格子位置"在色环上渐变（每格 +0.05 色相，约 20 格绕一圈；色相 1.0≡0.0 都落在红色，首尾无缝）。
        // Spawn 时池已先 SetParent 再触发 OnRent，故这里拿得到 sibling index——颜色是格子位置的纯函数，连续方块呈平滑彩虹、一眼看出规律，
        // 也省掉了任何全局计数器。复用的旧实例进到新格子也会重新着色——OnRent 每次 Spawn 都触发的可见证据。
        public void OnRent() => _image.color = Color.HSVToRGB((transform.GetSiblingIndex() * 0.05f) % 1f, 0.55f, 1f);

        // 归还时清状态：复位基色。真实项目里这里退订事件 / 清引用 / 停协程，避免脏状态被下一个取用者带走。
        public void OnReturn() => _image.color = _baseColor;
    }
}
