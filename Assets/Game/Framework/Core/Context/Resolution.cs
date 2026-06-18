namespace Game.Framework.Context
{
    /// <summary>
    /// 控制工厂绑定的构造时机。
    /// - Lazy：首次 Resolve 时调用工厂并缓存结果（默认）
    /// - Eager：Build() 完成时立即调用工厂，启动期就暴露配置错误
    /// </summary>
    public enum Resolution
    {
        Lazy,
        Eager
    }
}
