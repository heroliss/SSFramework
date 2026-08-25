using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Game.Framework.Editor.Tests
{
    /// <summary>防止可复用 Framework 源码重新把当前仓库的 Demo、业务程序集或目录当成产品默认值。</summary>
    public sealed class FrameworkProjectAgnosticTests
    {
        [Test]
        public void ReusableFrameworkSources_DoNotHardCodeRepositorySpecificProjects()
        {
            string editorAsmdefPath = AssetDatabase.FindAssets("Game.Framework.Editor t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .First(path => Path.GetFileName(path) == "Game.Framework.Editor.asmdef");
            string editorDirectory = Path.GetDirectoryName(Path.GetFullPath(editorAsmdefPath));
            string root = Directory.GetParent(editorDirectory)!.FullName;
            string[] forbidden =
            {
                "Assets/Game/Framework/Demo",
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
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/Demo/") || normalized.Contains("/Test/") ||
                    normalized.Contains("/Tests/"))
                    continue;

                string source = File.ReadAllText(file);
                foreach (string token in forbidden)
                    if (source.Contains(token))
                        violations.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + " → " + token);
            }

            Assert.That(violations, Is.Empty,
                "Framework 通用源码可动态展示当前工程证据，但不能硬编码仓库 Demo/业务项目：\n" +
                string.Join("\n", violations));
        }
    }
}
