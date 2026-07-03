using System;

namespace Game.Framework.Context
{
    /// <summary>
    /// 标记「此服务类不进生成的安装器」：服务安装器代码生成器扫描目录时跳过带此特性的类型，
    /// 注册交还业务手写——适用于需要 <c>RegisterFactory</c> 懒构造、带参构造、或注册契约需特殊裁剪的服务。
    /// 仅编辑期生成器读取，运行时无任何行为（ADR-0019）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ExcludeFromInstallerAttribute : Attribute
    {
    }
}
