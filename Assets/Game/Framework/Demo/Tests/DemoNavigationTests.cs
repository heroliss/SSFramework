using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Framework.Demo.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.Demo.Tests
{
    /// <summary>锁定章节查找与 Editor 工具跳转的共享导航语义。</summary>
    public sealed class DemoNavigationTests
    {
        [Test]
        public void ChapterFilter_MatchesTeachingMetadataAndRequiresEveryTerm()
        {
            using var catalog = DemoModuleCatalog.Discover();
            IDemoModule module = catalog.Modules.Single(candidate => candidate.Id == "module-boundaries");

            Assert.IsTrue(DemoShellController.MatchesChapterFilter(module, null));
            Assert.IsTrue(DemoShellController.MatchesChapterFilter(module, "模块化 裁剪"),
                "多个关键词可以分别命中标题与简介。 ");
            Assert.IsTrue(DemoShellController.MatchesChapterFilter(module, "MODULE-BOUNDARIES"),
                "维护者应能按稳定 Id 定位，且英文匹配不区分大小写。 ");
            Assert.IsTrue(DemoShellController.MatchesChapterFilter(module, "进阶"));
            Assert.IsFalse(DemoShellController.MatchesChapterFilter(module, "模块化 不存在"),
                "多关键词查找采用 AND 语义，不能只命中其中一项就放行。 ");
            Assert.IsFalse(DemoShellController.MatchesChapterFilter(null, string.Empty));
        }

        [Test]
        public void ModuleSources_DelegateEditorMenuOpeningToSharedNavigation()
        {
            string modulesDirectory = Path.Combine(
                Application.dataPath,
                "Game/Framework/Demo/Scripts/Modules");

            foreach (string path in Directory.GetFiles(modulesDirectory, "*Module.cs"))
            {
                string source = File.ReadAllText(path);
                string codeOnly = CSharpLexicalMap.Create(source).CreateCodeOnlyText();
                Assert.That(codeOnly, Does.Not.Contain("EditorApplication.ExecuteMenuItem"),
                    $"{Path.GetFileName(path)} 不应绕过 DemoEditorNav 静默执行 Editor 菜单。 ");
                Assert.IsFalse(Regex.IsMatch(codeOnly, @"\bRunMenu\s*\("),
                    $"{Path.GetFileName(path)} 不应重新复制章节私有菜单包装。 ");
            }

            string navSource = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Game/Framework/Demo/Scripts/Core/DemoEditorNav.cs"));
            string navCode = CSharpLexicalMap.Create(navSource).CreateCodeOnlyText();
            Assert.AreEqual(1, Regex.Matches(navCode, @"\bEditorApplication\.ExecuteMenuItem\s*\(").Count,
                "Editor 菜单执行与失败反馈应只有一份真源。 ");
        }

        [Test]
        public void OpenMenu_EmptyPathFailsFastWithActionableFeedback()
        {
            LogAssert.Expect(LogType.Warning, "[Demo] 无法打开 Editor 工具：菜单路径为空。");

            Assert.IsFalse(DemoEditorNav.OpenMenu("  "));
        }
    }
}
