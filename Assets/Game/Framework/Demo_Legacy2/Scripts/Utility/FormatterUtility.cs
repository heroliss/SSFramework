using Game.Framework.Utility;

namespace Game.Framework.Demo.Utility
{
    /// <summary>数字格式化工具接口（Utility 层：无状态纯工具）。</summary>
    public interface IFormatterUtility : IUtility
    {
        string FormatNumber(int value);
        string FormatCurrency(int value);
    }

    /// <summary>
    /// 数字格式化工具。Utility 不访问 Model/System，不产生副作用——可被任意层调用。
    /// </summary>
    public sealed class FormatterUtility : MonoUtilityBase, IFormatterUtility
    {
        public string FormatNumber(int value) => value.ToString("N0");
        public string FormatCurrency(int value) => $"¥{value:N0}";
    }
}
