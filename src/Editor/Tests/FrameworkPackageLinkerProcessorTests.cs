using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;

namespace Game.Framework.Editor.Tests
{
    /// <summary>Package 裁剪规则接入、模块删除和失败时不发布半份规则的契约。</summary>
    public sealed class FrameworkPackageLinkerProcessorTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.GetFullPath(Path.Combine("Library", "SSFramework", "Tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void Selection_OnlyIncludesAdjacentRulesForSuppliedPackageModules()
        {
            string[] selected = FrameworkPackageLinkerProcessor.SelectLinkXmlAssetPaths(new[]
            {
                "Packages/custom-layout/Adapters/A/Game.Framework.A.asmdef",
                "Assets/Framework/B/Game.Framework.B.asmdef",
            }, new[]
            {
                "Packages/custom-layout/Adapters/A/link.xml",
                "Packages/custom-layout/Adapters/A/Editor/link.xml",
                "Packages/custom-layout/Adapters/Removed/link.xml",
                "Packages/unrelated/link.xml",
                "Assets/Framework/B/link.xml",
            });

            Assert.That(selected, Is.EqualTo(new[] { "Packages/custom-layout/Adapters/A/link.xml" }));
        }

        [Test]
        public void Selection_IsSortedAndDoesNotDuplicateModuleRules()
        {
            string[] selected = FrameworkPackageLinkerProcessor.SelectLinkXmlAssetPaths(new[]
            {
                "Packages/p/Z/Z.asmdef", "Packages/p/A/A.asmdef", "Packages/p/Z/Z.asmdef",
            }, new[] { "Packages/p/Z/link.xml", "Packages/p/A/link.xml" });

            Assert.That(selected, Is.EqualTo(new[] { "Packages/p/A/link.xml", "Packages/p/Z/link.xml" }));
        }

        [Test]
        public void InstalledPackageRules_ResolveThroughCatalogAndReachGeneratedDescriptor()
        {
            string[] modules = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Where(assembly => assembly.name.StartsWith("Game.Framework.", StringComparison.Ordinal))
                .Select(assembly => CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name))
                .ToArray();
            string[] paths = FrameworkPackageLinkerProcessor.SelectLinkXmlAssetPaths(
                modules, AssetDatabase.GetAllAssetPaths());
            if (paths.Length == 0) Assert.Ignore("当前工程没有带 Package link.xml 的 Runtime Module。");

            var sources = FrameworkModuleSourceCatalog.ResolveKnownAssetPaths(paths);
            string output = Path.Combine(_directory, "resolved.xml");
            FrameworkPackageLinkerProcessor.WriteLinkXml(sources, output);
            XElement[] actual = XDocument.Load(output).Root.Elements("assembly").ToArray();
            XElement[] expected = sources.SelectMany(source =>
                XDocument.Load(source.PhysicalPath).Root.Elements("assembly")).ToArray();
            Assert.That(actual.Select(element => element.ToString()),
                Is.EqualTo(expected.Select(element => element.ToString())));
            Assert.That(sources.All(source => source.IsPackage && File.Exists(source.PhysicalPath)), Is.True);
        }

        [Test]
        public void Merge_PreservesMemberRulesConditionsAndEscaping()
        {
            var source = Source("a.xml", "<linker><assembly fullname=\"Example\" ignoreIfMissing=\"1\" " +
                "ignoreIfUnreferenced=\"1\"><type fullname=\"Example.Generic`1\" preserve=\"nothing\">" +
                "<method signature=\"System.Void Use(System.Collections.Generic.List&lt;System.Int32&gt;)\"/>" +
                "</type></assembly></linker>");
            string output = Path.Combine(_directory, "merged.xml");
            FrameworkPackageLinkerProcessor.WriteLinkXml(new[] { source }, output);

            Assert.That(XNode.DeepEquals(XDocument.Load(source.PhysicalPath).Root,
                XDocument.Load(output).Root), Is.True);
        }

        [Test]
        public void RemovedModules_ClearPreviouslyGeneratedRoots()
        {
            string output = Path.Combine(_directory, "merged.xml");
            FrameworkPackageLinkerProcessor.WriteLinkXml(new[]
            {
                Source("a.xml", "<linker><assembly fullname=\"Removed\" preserve=\"all\"/></linker>"),
            }, output);
            FrameworkPackageLinkerProcessor.WriteLinkXml(
                Array.Empty<FrameworkModuleSourceCatalog.SourceLocation>(), output);

            Assert.That(XDocument.Load(output).Root.Elements(), Is.Empty);
        }

        [TestCase("<linker>")]
        [TestCase("<wrong/>")]
        [TestCase("<linker xmlns=\"unexpected\"/>")]
        [TestCase("<!DOCTYPE linker [<!ENTITY item 'external'>]><linker>&item;</linker>")]
        public void InvalidRule_FailsWithSourceAndDoesNotOverwriteLastOutput(string xml)
        {
            string output = Path.Combine(_directory, "merged.xml");
            File.WriteAllText(output, "previous complete output");
            var source = Source("broken.xml", xml);
            var exception = Assert.Throws<BuildFailedException>(() =>
                FrameworkPackageLinkerProcessor.WriteLinkXml(new[] { source }, output));

            Assert.That(exception.Message, Does.Contain(source.AssetPath));
            Assert.That(File.ReadAllText(output), Is.EqualTo("previous complete output"));
        }

        [Test]
        public void MissingSource_FailsBeforeCreatingOutputDirectory()
        {
            var source = Source("missing.xml", "<linker/>");
            File.Delete(source.PhysicalPath);
            string output = Path.Combine(_directory, "not-created", "link.xml");
            Assert.Throws<BuildFailedException>(() =>
                FrameworkPackageLinkerProcessor.WriteLinkXml(new[] { source }, output));
            Assert.That(Directory.Exists(Path.GetDirectoryName(output)), Is.False);
        }

        private FrameworkModuleSourceCatalog.SourceLocation Source(string name, string xml)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllText(path, xml);
            return new FrameworkModuleSourceCatalog.SourceLocation
            {
                AssetPath = "Packages/example/" + name,
                PhysicalPath = path,
                PackageName = "example",
            };
        }
    }
}
