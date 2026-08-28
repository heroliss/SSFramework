using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定资源构建可以在没有 Boot、HybridCLR 与 dnlib 的工程中独立保留。</summary>
    public sealed class BuildEditorModuleBoundaryTests
    {
        private const string AssemblyDefinitionPath =
            "Assets/Game/Framework/Build/Editor/Game.Framework.Build.Editor.asmdef";

        [Test]
        public void ResourceBuildAssembly_DoesNotReferenceHotUpdateToolchain()
        {
            string[] references = typeof(FrameworkAssetBuilder).Assembly.GetReferencedAssemblies()
                .Select(assembly => assembly.Name)
                .ToArray();

            Assert.That(references, Does.Not.Contain("Game.Framework.Boot"));
            Assert.That(references, Does.Not.Contain("Game.Framework.Build.HybridCLR.Editor"));
            Assert.That(references, Does.Not.Contain("HybridCLR.Editor"));
            Assert.That(references, Does.Not.Contain("dnlib"));
        }

        [Test]
        public void ResourceBuildAsmdef_DeclaresOnlyItsOwnedToolchain()
        {
            AsmdefDeclaration declaration = ReadDeclaration(AssemblyDefinitionPath);

            Assert.That(declaration.references, Is.EquivalentTo(new[]
            {
                "Game.Framework",
                "Game.Framework.Editor",
                "YooAsset",
                "YooAsset.Editor",
            }));
            Assert.That(declaration.precompiledReferences, Is.Empty);
            Assert.That(declaration.autoReferenced, Is.False,
                "可删除的资源构建 Module 不应让 Assembly-CSharp-Editor 获得隐式引用。");
            Assert.That(declaration.overrideReferences, Is.True,
                "资源构建 Module 必须关闭预编译 DLL 的全局 Auto Reference。");
        }

        [TestCase(nameof(AssetBuildWindow))]
        [TestCase(nameof(FrameworkAssetBuildProfileEditor))]
        public void ResourceBuildEditors_ConsumeSharedOperationGate(string typeName)
        {
            string source = ReadScriptSource(typeName);

            Assert.That(source, Does.Contain("FrameworkEditorOperationGate.CanStart"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isPlayingOrWillChangePlaymode"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isCompiling"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isUpdating"));
            Assert.That(source, Does.Not.Contain("BuildPipeline.isBuildingPlayer"));
        }

        [Test]
        public void ResourceBuildActions_RecheckEnabledPackagesBeforeBuildOrDeploy()
        {
            string source = ReadScriptSource(nameof(AssetBuildMenu));

            Assert.That(source, Does.Contain("TryPrepareBuild(\"资源包构建\""));
            Assert.That(source, Does.Contain("TryPrepareBuild(\"资源包全量重建\""));
            Assert.That(source, Does.Contain("TryGetEnabledPackages(\"资源包部署\""));
            Assert.That(source, Does.Contain("FrameworkEditorOperationGate.EnsureCanStart(operation)"),
                "全量确认框和业务前置检查之前应先拒绝 Unity 忙碌状态。");

            int preflight = source.IndexOf("private static bool TryPrepareBuild", System.StringComparison.Ordinal);
            int runBuild = source.IndexOf("private static void RunBuild", preflight, System.StringComparison.Ordinal);
            int profileCheck = source.IndexOf(
                "TryGetProfile(operation", preflight, System.StringComparison.Ordinal);
            int gateCheck = source.IndexOf(
                "EnsureCanStart(operation)", preflight, System.StringComparison.Ordinal);
            int packagesCheck = source.IndexOf(
                "TryGetEnabledPackages(operation", preflight, System.StringComparison.Ordinal);
            Assert.That(preflight, Is.GreaterThanOrEqualTo(0));
            Assert.That(runBuild, Is.GreaterThan(preflight));
            Assert.That(profileCheck, Is.InRange(preflight, runBuild));
            Assert.That(gateCheck, Is.InRange(profileCheck + 1, runBuild));
            Assert.That(packagesCheck, Is.InRange(gateCheck + 1, runBuild),
                "动作预检必须保持 Profile → Unity Gate → 启用包的优先级。");

            int fullRebuild = source.IndexOf("internal static void FullRebuild", System.StringComparison.Ordinal);
            int prepare = source.IndexOf(
                "TryPrepareBuild(\"资源包全量重建\"", fullRebuild, System.StringComparison.Ordinal);
            string confirmationCall = "EditorUtility." + "DisplayDialog";
            int confirmation = source.IndexOf(
                confirmationCall, fullRebuild, System.StringComparison.Ordinal);
            Assert.That(fullRebuild, Is.GreaterThanOrEqualTo(0));
            Assert.That(prepare, Is.GreaterThan(fullRebuild));
            Assert.That(confirmation, Is.GreaterThan(prepare),
                "无效请求必须在弹确认框前拒绝，避免零启用包或 Unity 忙碌时仍打断用户。");
        }

        [Test]
        public void WorkbenchAvailability_MissingProfileBlocksAllActionsOnce()
        {
            var result = EvaluateAvailability(
                hasProfile: false,
                canEditProject: true,
                canRunAnyMode: true,
                enabledPackageCount: 1,
                deployDirectoryExists: true);

            Assert.That(result.BuildReady, Is.False);
            Assert.That(result.DeployReady, Is.False);
            Assert.That(result.ServeReady, Is.False);
            Assert.That(result.DeployReason, Is.EqualTo(result.BuildReason));
            Assert.That(result.ServeReason, Is.EqualTo(result.BuildReason));
        }

        [Test]
        public void WorkbenchAvailability_UnityGateWinsBeforeBusinessConditions()
        {
            var result = EvaluateAvailability(
                hasProfile: true,
                canEditProject: false,
                canRunAnyMode: false,
                enabledPackageCount: 0,
                deployDirectoryExists: false);

            Assert.That(result.BuildReason, Is.EqualTo("需要 Edit Mode"));
            Assert.That(result.DeployReason, Is.EqualTo("Unity 忙碌"));
            Assert.That(result.ServeReason, Is.EqualTo("Unity 忙碌"));
        }

        [Test]
        public void WorkbenchAvailability_NoEnabledPackagesBlocksBuildAndDeployButNotExistingServerRoot()
        {
            var result = EvaluateAvailability(
                hasProfile: true,
                canEditProject: true,
                canRunAnyMode: true,
                enabledPackageCount: 0,
                deployDirectoryExists: true);

            Assert.That(result.BuildReady, Is.False);
            Assert.That(result.DeployReady, Is.False);
            Assert.That(result.BuildReason, Does.Contain("没有启用"));
            Assert.That(result.ServeReady, Is.True,
                "本地服务只伺服已有 Deploy 目录，不应被当前包选择反向禁用。");
        }

        [Test]
        public void WorkbenchAvailability_MissingDeployRootOnlyBlocksLocalServer()
        {
            var result = EvaluateAvailability(
                hasProfile: true,
                canEditProject: true,
                canRunAnyMode: true,
                enabledPackageCount: 1,
                deployDirectoryExists: false);

            Assert.That(result.BuildReady, Is.True);
            Assert.That(result.DeployReady, Is.True);
            Assert.That(result.ServeReady, Is.False);
            Assert.That(result.ServeReason, Does.Contain("部署目录"));
        }

        private static AssetBuildWindow.ActionAvailability EvaluateAvailability(
            bool hasProfile,
            bool canEditProject,
            bool canRunAnyMode,
            int enabledPackageCount,
            bool deployDirectoryExists)
            => AssetBuildWindow.EvaluateActionAvailability(
                hasProfile: hasProfile,
                canEditProject: canEditProject,
                editModeReason: "需要 Edit Mode",
                canRunAnyMode: canRunAnyMode,
                anyModeReason: "Unity 忙碌",
                enabledPackageCount: enabledPackageCount,
                deployDirectoryExists: deployDirectoryExists);

        private static AsmdefDeclaration ReadDeclaration(string assetPath)
        {
            AssemblyDefinitionAsset asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(assetPath);
            Assert.That(asset, Is.Not.Null, $"找不到程序集定义：{assetPath}");
            return JsonUtility.FromJson<AsmdefDeclaration>(asset.text);
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

        [Serializable]
        private sealed class AsmdefDeclaration
        {
            public string[] references = Array.Empty<string>();
            public string[] precompiledReferences = Array.Empty<string>();
            public bool overrideReferences;
            public bool autoReferenced = true;
        }
    }
}
