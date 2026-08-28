using NUnit.Framework;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定所有代码生成器共用的 C# 命名空间词法边界。</summary>
    public sealed class FrameworkCSharpSyntaxTests
    {
        [TestCase("Game")]
        [TestCase("Game.Framework.Generated")]
        [TestCase("游戏.配置")]
        [TestCase("_Internal.Module2")]
        [TestCase("@class.Valid")]
        [TestCase("var.ContextualKeyword")]
        public void TryValidateNamespace_AcceptsValidIdentifiers(string value)
        {
            Assert.That(
                FrameworkCSharpSyntax.TryValidateNamespace(value, out string error),
                Is.True,
                error);
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase(".Game")]
        [TestCase("Game.")]
        [TestCase("Game..Generated")]
        [TestCase("Bad Namespace")]
        [TestCase("1Game.Generated")]
        [TestCase("class.Generated")]
        [TestCase("Game.namespace")]
        [TestCase("Game-Generated")]
        [TestCase("global::Game")]
        [TestCase("@.Game")]
        public void TryValidateNamespace_RejectsInvalidOrReservedSegments(string value)
        {
            Assert.That(
                FrameworkCSharpSyntax.TryValidateNamespace(value, out string error),
                Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase("Window", "Window")]
        [TestCase("游戏2", "游戏2")]
        [TestCase("1-window", "_1_window")]
        [TestCase("class", "_class")]
        [TestCase("@event", "_event")]
        [TestCase("", "_")]
        public void SanitizeIdentifier_ProducesStableLegalSourceName(
            string value,
            string expected)
        {
            string identifier = FrameworkCSharpSyntax.SanitizeIdentifier(value);

            Assert.That(identifier, Is.EqualTo(expected));
            Assert.That(
                FrameworkCSharpSyntax.TryValidateNamespace(identifier, out string error),
                Is.True,
                error);
        }
    }
}
