using System;
using R3;

namespace R3
{
    /// <summary>
    /// <see cref="SerializableReactiveProperty{T}"/> 的泛型简写，适用于任意类型。
    /// Inspector 中直接显示值（不多套一层），行为与 <see cref="SerializableReactiveProperty{T}"/> 完全一致。
    /// 编辑器绘制由 <c>Game.Framework.Editor</c> 程序集的 <c>RPDrawer</c>（注册到 <c>RP&lt;&gt;</c>）提供。
    /// <code><![CDATA[
    /// [field: SerializeField] public RP<int>        HP    { get; private set; } = new(100);
    /// [field: SerializeField] public RP<Vector3>    Pos   { get; private set; } = new();
    /// [field: SerializeField] public RP<PlayerData> Stats { get; private set; } = new();
    /// ]]></code>
    /// </summary>
    /// <remarks>
    /// 之所以是真实子类而非类型别名：C# 不支持泛型 <c>using</c> 别名（<c>using RP&lt;T&gt; = ...</c> 非法），
    /// 要得到简短泛型名只能定义类型。本类位于 <c>Game.Framework</c> 程序集——业务无论在 Assembly-CSharp
    /// 还是将来独立 asmdef 引用框架后，都能直接使用；框架内部也可使用。
    /// </remarks>
    [Serializable]
    public class RP<T> : SerializableReactiveProperty<T>
    {
        /// <summary>创建使用 <typeparamref name="T"/> 默认值的响应式属性。</summary>
        public RP() { }

        /// <summary>创建指定初始值的响应式属性。</summary>
        /// <param name="value">初始值。</param>
        public RP(T value) : base(value) { }
    }
}
