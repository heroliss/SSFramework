using System;
using System.IO;
using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;

namespace Game.Framework.Config.Editor.Tests
{
    /// <summary>锁定 Luban Editor Module 自己拥有配置中心登记，中央窗口无需知道该可选程序集。</summary>
    public sealed class ConfigEditorCatalogRegistrationTests
    {
        [Test]
        public void LubanModule_RegistersOwnedConfiguration() =>
            Assert.That(FrameworkConfigRegistry.Snapshot().Select(item => item.Id), Does.Contain("luban"));

        [Test]
        public void LubanModule_RegistersOwnedOutputClaims() =>
            Assert.That(
                FrameworkGeneratedOutputClaimCatalog.SnapshotSources().Select(item => item.Id),
                Does.Contain(LubanCodeGenerator.OutputClaimSourceId));

        [Test]
        public void LubanWorkbench_ConsumesSharedOperationGate()
        {
            string source = ReadScriptSource(nameof(LubanConfigOverviewWindow));

            Assert.That(source, Does.Contain("FrameworkEditorOperationGate.CanStart"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isPlayingOrWillChangePlaymode"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isCompiling"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isUpdating"));
            Assert.That(source, Does.Not.Contain("BuildPipeline.isBuildingPlayer"));
        }

        [Test]
        public void ConfigEditorAssemblyAndOwnedTypes_UseConfigNamespace()
        {
            const string expectedNamespace = "Game.Framework.Config.Editor";
            Type[] ownedTypes =
            {
                typeof(LubanBuildMenu),
                typeof(LubanCodeGenerator),
                typeof(LubanConfigOverviewWindow),
                typeof(LubanConfigProfile),
                typeof(LubanGenerationTransaction),
            };

            Assert.That(ownedTypes.Select(type => type.Namespace).Distinct(),
                Is.EqualTo(new[] { expectedNamespace }));
            string assemblyDefinition = File.ReadAllText(
                Path.Combine(
                    Directory.GetParent(UnityEngine.Application.dataPath)!.FullName,
                    "Assets/Game/Framework/Config/Editor/Game.Framework.Config.Editor.asmdef"));
            Assert.That(assemblyDefinition,
                Does.Contain("\"rootNamespace\": \"Game.Framework.Config.Editor\""));
        }

        private static string ReadScriptSource(string typeName)
        {
            string[] paths = AssetDatabase.FindAssets(typeName + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/" + typeName + ".cs", StringComparison.Ordinal))
                .ToArray();
            Assert.That(paths, Has.Length.EqualTo(1), "应精确找到 owner Module 内的窗口源码。");
            return AssetDatabase.LoadAssetAtPath<MonoScript>(paths[0]).text;
        }
    }
}
