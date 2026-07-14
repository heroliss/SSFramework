// C# 10 的「插值字符串处理器」（interpolated string handler）需要下面两个 attribute，
// 但 Unity 的 BCL（netstandard2.1 档）不提供它们——实测 DefaultInterpolatedStringHandler 也没有。
//
// 编译器只按**完全限定名**匹配这两个 attribute，不要求它们来自 BCL，因此各程序集自行声明一份即可。
// 声明为 internal 是业界惯例（实测 R3 / ObservableCollections / Microsoft.CodeAnalysis 全是 internal）：
// 内部类型不外泄，多个程序集各带一份也不会互相冲突；而「某个类型是处理器」这一事实是写进
// 该类型元数据的，调用方所在程序集**不需要**也声明这两个 attribute 就能识别（跨程序集调用照常工作）。
//
// 用途见 Logging/TraceInterpolatedStringHandler.cs：让 Log.Trace($"...") 在级别没开时
// 连插值表达式都不求值（真·零成本），而不是先拼好字符串再丢弃。

namespace System.Runtime.CompilerServices
{
    /// <summary>标记一个类型为插值字符串处理器（C# 10）。Unity BCL 缺失，此处自带。</summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }

    /// <summary>把调用方的其它实参转发给处理器构造函数（C# 10）。Unity BCL 缺失，此处自带。</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument) => Arguments = new[] { argument };

        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments) => Arguments = arguments;

        public string[] Arguments { get; }
    }
}
