using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace Game.Framework.Editor.Tests
{
    /// <summary>防止可复用 Framework 源码重新把当前仓库的 Demo、业务程序集或目录当成产品默认值。</summary>
    public sealed class FrameworkProjectAgnosticTests
    {
        [Test]
        public void ReusableFrameworkSources_DoNotHardCodeRepositorySpecificProjects()
        {
            string[] forbidden =
            {
                "Assets/Game/Framework",
                "Assets/Game/Outpost",
                "Assets/Game/Main",
                "Assets/Game/Settings",
                "Assets/Scripts/UI",
                "Game.Framework.Demo",
                "Game.Main.GameEntry",
                "Game.UI",
                "DemoScene",
                "Outpost",
            };
            var violations = new List<string>();
            foreach (string assetPath in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase)))
            {
                string normalized = assetPath.Replace('\\', '/');
                if (normalized.Contains("/Demo/") || normalized.Contains("/Test/") ||
                    normalized.Contains("/Tests/"))
                    continue;

                string assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath);
                if (string.IsNullOrEmpty(assemblyName) ||
                    (!assemblyName.Equals(FrameworkModuleAudit.CoreAssemblyName,
                         System.StringComparison.Ordinal) &&
                     !assemblyName.StartsWith(FrameworkModuleAudit.CoreAssemblyName + ".",
                         System.StringComparison.Ordinal)))
                    continue;

                FrameworkModuleSourceCatalog.SourceLocation location =
                    FrameworkModuleSourceCatalog.Resolve(assetPath);
                string source = File.ReadAllText(location.PhysicalPath);
                foreach (string token in forbidden)
                    if (source.Contains(token))
                        violations.Add(location.AssetPath + " → " + token);
            }

            Assert.That(violations, Is.Empty,
                "Framework 通用源码可动态展示当前工程证据，但不能硬编码仓库 Demo/业务项目：\n" +
                string.Join("\n", violations));
        }
    }
}
