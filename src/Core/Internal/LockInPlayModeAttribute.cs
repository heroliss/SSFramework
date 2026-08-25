using UnityEngine;

namespace Game.Framework.Internal
{
    /// <summary>
    /// 标记只在宿主初始化前读取的场景接线字段。Editor Drawer 会在 PlayMode 禁止修改，避免 Inspector
    /// 看似接受了新值、运行时却仍使用 Awake 快照；它不参与运行时逻辑，也不依赖具体 Inspector 实现。
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    internal sealed class LockInPlayModeAttribute : PropertyAttribute { }
}
