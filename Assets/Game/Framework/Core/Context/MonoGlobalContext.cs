using UnityEngine;

namespace Game.Framework.Context
{
    /// <summary>
    /// 全局上下文基类：项目级唯一根上下文的标准载体。
    ///
    /// 用途：
    /// - 注册跨场景共享的服务（Audio/Save/Network 等）
    /// - 子上下文（无论是否在同一场景）通过 _inheritFromGlobal = true（默认开启）回退到此
    ///
    /// 自动行为：
    /// - Awake 把自身 Context 写入 GameContext.Main
    /// - DontDestroyOnLoad 跨场景保留
    /// - 检测到第二个 MonoGlobalContext 实例时报错并销毁自身（先到胜出）
    /// - OnDestroy 清空 GameContext.Main（仅当仍指向自身）
    ///
    /// 执行顺序：DefaultExecutionOrder = -2000，确保在所有子级 MonoGameContextBase（-1000）之前 Awake，
    /// 使得子级在 Awake 时已能读到 GameContext.Main。
    ///
    /// 用法：子类重写 InstallBindings 注册全局服务即可，不再需要手工设置 GameContext.Main。
    /// </summary>
    [DefaultExecutionOrder(-2000)]
    public abstract class MonoGlobalContext : MonoGameContextBase
    {
        protected override void OnInitialized()
        {
            // 检测重复实例：在场景 A → B → A 来回切换时，旧 Main 可能仍存活
            if (GameContext.Main != null && GameContext.Main != RawContext)
            {
                Debug.LogError(
                    $"[MonoGlobalContext] Another global context is already active. " +
                    $"Only one MonoGlobalContext should exist per project. Destroying '{name}'.");
                Destroy(gameObject);
                return;
            }

            GameContext.Main = RawContext;
            DontDestroyOnLoad(gameObject);
        }

        protected override void OnDestroy()
        {
            // 仅当 Main 仍指向自己时才清空，避免被新实例覆盖后误清
            if (GameContext.Main == RawContext)
                GameContext.Main = null;
            base.OnDestroy();
        }
    }
}
