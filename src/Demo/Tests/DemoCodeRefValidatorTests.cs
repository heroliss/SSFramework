using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Framework.Context;
using Game.Framework.Demo.Core;
using Game.Framework.Demo.Modules;
using Game.Framework.Demo.Modules.Services;
using Game.Framework.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Demo.Tests
{
    /// <summary>把原本只能手点菜单的 Demo 源码跳转防腐检查纳入 EditMode 门禁。</summary>
    public sealed class DemoCodeRefValidatorTests
    {
        [Test]
        public void EveryCodeRefPathAndAnchor_StillResolvesPrecisely()
        {
            LogAssert.Expect(LogType.Log, new Regex(@"\[CodeRef 校验\] 通过：\d+ 处跳转全部精准命中"));
            DemoCodeRefValidator.Validate();
        }

        [Test]
        public void ProjectReport_CoversEveryRealConstructionSite()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var report = DemoCodeRefValidator.ValidateProject(projectRoot);

            Assert.IsEmpty(report.Problems, string.Join("\n", report.Problems));
            Assert.AreEqual(315, report.Total,
                "数量锁既防止已有链接静默退出门禁，也提醒新增构造语法必须同步扩展扫描器。当前基线不含注释/文案示例。 ");
            Assert.AreEqual(315, report.Precise);
            Assert.AreEqual(0, report.FileTop, "教程链接应尽量指向可解释的具体代码，而不是只打开文件头。 ");
        }

        [Test]
        public void SourceScanner_IgnoresNonCodeAndResolvesConstantsConcatenationAndTargetTypedNew()
        {
            const string source =
                "private const string Root = \"Assets/Demo\";\n" +
                "private static readonly CodeRef First = new(Root + \"/A.cs\", \"class A\");\n" +
                "// CodeRef.Here(\"comment\")\n" +
                "var teachingText = \"new CodeRef(\\\"fake.cs\\\", \\\"fake\\\")\";\n" +
                "CodeRef.Here(\"void Build\");\n" +
                "new CodeRef(Root + \"/B.cs\", \"class B\");\n";

            var scan = DemoCodeRefSourceScanner.Scan(source);

            Assert.IsEmpty(scan.Issues);
            Assert.AreEqual(3, scan.SiteCount);
            CollectionAssert.AreEqual(
                new[] { "Assets/Demo/A.cs", null, "Assets/Demo/B.cs" },
                scan.Calls.Select(call => call.Path).ToArray());
            CollectionAssert.AreEqual(
                new[] { "class A", "void Build", "class B" },
                scan.Calls.Select(call => call.Anchor).ToArray());
        }

        [Test]
        public void SourceScanner_ReportsUnresolvableExpressionsInsteadOfSilentlySkippingThem()
        {
            var scan = DemoCodeRefSourceScanner.Scan(
                "new CodeRef(BuildPath(), \"class Demo\");");

            Assert.AreEqual(1, scan.SiteCount);
            Assert.IsEmpty(scan.Calls);
            Assert.AreEqual(1, scan.Issues.Count);
            StringAssert.Contains("path", scan.Issues[0].Message);
        }

        [Test]
        public void SourceScanner_CoversQualifiedAndCommonTargetTypedConstructionForms()
        {
            const string source =
                "CodeRef Ref => new(\"Assets/A.cs\", \"class A\");\n" +
                "CodeRef Choose(bool first) { return first ? new(\"Assets/B.cs\", \"class B\") : new(\"Assets/C.cs\", \"class C\"); }\n" +
                "CodeRef RefWithGetter { get { return new(\"Assets/D.cs\", \"class D\"); } }\n" +
                "var explicitRef = new global::Game.Framework.Demo.Core.CodeRef(\"Assets/E.cs\", \"class E\");\n";

            var scan = DemoCodeRefSourceScanner.Scan(source);

            Assert.IsEmpty(scan.Issues);
            Assert.AreEqual(5, scan.SiteCount);
            CollectionAssert.AreEqual(
                new[] { "Assets/A.cs", "Assets/B.cs", "Assets/C.cs", "Assets/D.cs", "Assets/E.cs" },
                scan.Calls.Select(call => call.Path).ToArray());
        }

        [Test]
        public void SourceScanner_DecodesCSharpUnicodeEscapesPrecisely()
        {
            var scan = DemoCodeRefSourceScanner.Scan(
                "new CodeRef(\"Assets/\\u0041/\\x42/\\U00000043.cs\", \"class Demo\");");

            Assert.IsEmpty(scan.Issues);
            Assert.AreEqual("Assets/A/B/C.cs", scan.Calls.Single().Path);
        }

        [Test]
        public void SourceScanner_DoesNotGuessWhenConstNamesAreAmbiguousAcrossScopes()
        {
            const string source =
                "class A { const string Path = \"Assets/A.cs\"; }\n" +
                "class B { const string Path = \"Assets/B.cs\"; CodeRef Ref => new(Path, \"class B\"); }\n";

            var scan = DemoCodeRefSourceScanner.Scan(source);

            Assert.AreEqual(1, scan.SiteCount);
            Assert.IsEmpty(scan.Calls);
            Assert.AreEqual(1, scan.Issues.Count, "同名常量无法在轻量扫描中可靠判定作用域时，必须显式失败而非选错路径。 ");
        }

        [Test]
        public void SourceScanner_TreatsVerbatimAndPlainIdentifiersAsTheSameConstName()
        {
            const string source =
                "class A { const string @Path = \"Assets/A.cs\"; CodeRef Ref => new(Path, \"class A\"); }\n" +
                "class B { const string Path = \"Assets/B.cs\"; }\n";

            var scan = DemoCodeRefSourceScanner.Scan(source);

            Assert.AreEqual(1, scan.SiteCount);
            Assert.IsEmpty(scan.Calls);
            Assert.AreEqual(1, scan.Issues.Count,
                "C# 中 @Path 与 Path 是同一标识符；轻量扫描器无法判定作用域时必须保守失败。 ");
        }

        [Test]
        public void ResolveAnchor_SkipsTeachingTextAndCommentsBeforeRealCode()
        {
            const string source =
                "var caption = \"await ctx.ExecuteCommandAsync 只是教程文案\";\n" +
                "/* await ctx.ExecuteCommandAsync 也是块注释 */\n" +
                "// await ctx.ExecuteCommandAsync 也是行注释\n" +
                "await ctx.ExecuteCommandAsync(command);\n";

            int line = CodeNavigator.ResolveAnchor(
                source,
                "await ctx.ExecuteCommandAsync",
                out var verdict);

            Assert.AreEqual(CodeNavigator.AnchorVerdict.Ok, verdict);
            Assert.AreEqual(4, line);
        }

        [TestCase("var caption = \"OnlyInText\";", (int)CodeNavigator.AnchorVerdict.OnlyLiteral)]
        [TestCase("/* OnlyInText */", (int)CodeNavigator.AnchorVerdict.CommentHit)]
        public void ResolveAnchor_WhenNoCodeMatch_ReportsTheNonCodeFailure(string source, int expected)
        {
            CodeNavigator.ResolveAnchor(source, "OnlyInText", out var verdict);
            Assert.AreEqual((CodeNavigator.AnchorVerdict)expected, verdict);
        }

        [Test]
        public void ResolveAnchor_WhenCodeMatchesMoreThanOnce_ReportsAmbiguous()
        {
            const string source = "RunStep();\nRunStep();\n";

            int line = CodeNavigator.ResolveAnchor(source, "RunStep()", out var verdict);

            Assert.AreEqual(CodeNavigator.AnchorVerdict.Ambiguous, verdict);
            Assert.AreEqual(1, line, "实际打开行为仍落到首个命中，但校验必须拒绝把它称为精准锚点。 ");
        }

        [TestCase("var text = $\"caption {ActualMethod()}\";", 1)]
        [TestCase("var text = $@\"caption {ActualMethod()}\";", 1)]
        [TestCase("var text = $\"\"\"caption {ActualMethod()}\"\"\";", 1)]
        [TestCase("var text = $\"caption {Format(\"ActualMethod()\")}\";\nActualMethod();", 2)]
        [TestCase("var text = $\"{42:ActualMethod()}\";\nActualMethod();", 2)]
        [TestCase("var text = $\"\"\"caption {Format(\"\"\"ActualMethod()\"\"\")}\"\"\";\nActualMethod();", 2)]
        public void ResolveAnchor_RecognizesCodeInsideInterpolatedStringsButNotNestedText(string source, int expectedLine)
        {
            int line = CodeNavigator.ResolveAnchor(source, "ActualMethod()", out var verdict);

            Assert.AreEqual(CodeNavigator.AnchorVerdict.Ok, verdict);
            Assert.AreEqual(expectedLine, line);
        }

        [Test]
        public void ModuleCatalog_MetadataSatisfiesRuntimeContract()
        {
            using var catalog = DemoModuleCatalog.Discover();
            Assert.AreEqual(32, catalog.Modules.Count, "章节增删时应同步检查学习路径、module map 与目录元数据。");
            Assert.AreEqual(2, catalog.Modules.Count(module => module.TeachingKind == DemoTeachingKind.Concept));
            Assert.AreEqual(6, catalog.Modules.Count(module => module.TeachingKind == DemoTeachingKind.Workflow));
            Assert.AreEqual(24, catalog.Modules.Count(module => module.TeachingKind == DemoTeachingKind.Capability));
        }

        [Test]
        public void ShopSystem_UsesNarrowInterfaceAndKeepsPurchaseInvariant()
        {
            var method = typeof(IShopSystem).GetMethod(nameof(IShopSystem.TryBuyPotion));
            Assert.IsNotNull(method);
            Assert.IsEmpty(method.GetParameters(), "System 接口不应泄漏 ICommandContext 等调用方编排细节。");

            var wallet = new WalletModel();
            using var builder = new ContainerBuilder();
            builder.RegisterValue(wallet, typeof(WalletModel));
            builder.RegisterValue(new ShopSystem(), typeof(IShopSystem));
            builder.RegisterValue(new CommandSystem(), typeof(ICommandSystem));
            using var context = new GameContext(builder.Build(), inheritFromGlobal: false);

            context.ExecuteCommand(new BuyPotionCommand());
            context.ExecuteCommand(new BuyPotionCommand());
            context.ExecuteCommand(new BuyPotionCommand());

            Assert.AreEqual(0, ReadReactiveInt(wallet, nameof(WalletModel.Gold)), "余额只够成功购买两次。");
            Assert.AreEqual(2, ReadReactiveInt(wallet, nameof(WalletModel.Potions)), "余额不足的第三次购买不得增加药水。");
        }

        // 测试程序集不直接引用 R3.Unity；通过反射读取示例 Model 的响应式字段，避免为两条断言扩大程序集依赖面。
        private static int ReadReactiveInt(WalletModel wallet, string fieldName)
        {
            object reactiveProperty = typeof(WalletModel).GetField(fieldName)?.GetValue(wallet);
            Assert.IsNotNull(reactiveProperty, $"找不到 WalletModel.{fieldName}");
            object value = reactiveProperty.GetType().GetProperty("Value")?.GetValue(reactiveProperty);
            Assert.IsNotNull(value, $"WalletModel.{fieldName} 不是可读响应式属性");
            return (int)value;
        }
    }
}
