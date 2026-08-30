using System;
using System.Linq;
using Game.Framework.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Game.Framework.Build.Tests
{
    /// <summary>锁定热更新构建是资源构建的可删除下游，并验证既有 Profile 在程序集迁移后仍可加载。</summary>
    public sealed class HybridClrBuildModuleBoundaryTests
    {
        private const string AssemblyDefinitionPath =
            "Assets/Game/Framework/Build/HybridCLR/Editor/Game.Framework.Build.HybridCLR.Editor.asmdef";
        private const string ExistingProfilePath = "Assets/Game/Settings/FrameworkHotUpdateProfile.asset";
        private const string ProfileScriptGuid = "879c7d85708f1fe45a4c19bb4f116929";

        [Test]
        public void HybridClrBuildAssembly_ReferencesRequiredDownstreamToolchain()
        {
            string[] references = typeof(FrameworkHotUpdateBuilder).Assembly.GetReferencedAssemblies()
                .Select(assembly => assembly.Name)
                .ToArray();

            Assert.That(references, Does.Contain("Game.Framework.Build.Editor"));
            Assert.That(references, Does.Contain("Game.Framework.Boot"));
            Assert.That(references, Does.Contain("HybridCLR.Editor"));
            Assert.That(references, Does.Contain("dnlib"));
        }

        [Test]
        public void HybridClrBuildAsmdef_DeclaresOneWayDownstreamDependencies()
        {
            AsmdefDeclaration declaration = ReadDeclaration(AssemblyDefinitionPath);

            Assert.That(declaration.references, Is.EquivalentTo(new[]
            {
                "Game.Framework",
                "Game.Framework.Editor",
                "Game.Framework.Boot",
                "Game.Framework.Build.Editor",
                "YooAsset",
                "YooAsset.Editor",
                "HybridCLR.Editor",
            }));
            Assert.That(declaration.precompiledReferences, Is.EquivalentTo(new[] { "dnlib.dll" }));
            Assert.That(declaration.autoReferenced, Is.False,
                "可删除的热更新构建 Module 不应让 Assembly-CSharp-Editor 获得隐式引用。");
            Assert.That(declaration.overrideReferences, Is.True,
                "热更新构建 Module 必须显式声明自己的预编译 DLL 闭包。");
        }

        [Test]
        public void ExistingHotUpdateProfiles_RemainLoadableAfterAssemblySplit()
        {
            string[] paths = AssetDatabase.FindAssets("t:" + nameof(FrameworkHotUpdateProfile))
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.That(paths, Is.Not.Empty,
                "工程中已有热更新 Profile，却无法按类型检索；程序集迁移兼容性门禁不能空跑。");
            Assert.That(paths, Does.Contain(ExistingProfilePath),
                $"既有热更新 Profile 未被新程序集识别：{ExistingProfilePath}");
            Assert.That(AssetDatabase.GUIDToAssetPath(ProfileScriptGuid),
                Is.EqualTo("Assets/Game/Framework/Build/HybridCLR/Editor/FrameworkHotUpdateProfile.cs"),
                "移动脚本时必须保留 MonoScript GUID，否则既有 Profile 会丢失类型。");

            foreach (string path in paths)
            {
                FrameworkHotUpdateProfile profile =
                    AssetDatabase.LoadAssetAtPath<FrameworkHotUpdateProfile>(path);
                Assert.That(profile, Is.Not.Null,
                    $"热更新 Profile 无法加载：{path}。请检查脚本 GUID 与程序集迁移兼容性。");
                Assert.That(profile.GetType().Assembly.GetName().Name,
                    Is.EqualTo("Game.Framework.Build.HybridCLR.Editor"),
                    $"热更新 Profile 仍来自旧程序集：{path}");
            }
        }

        [Test]
        public void HotUpdateProfile_UsesSharedStableDiscoveryWithoutPrivateInvalidation()
        {
            string folderName = "__HotUpdateProfileCatalogTests_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                CreateProfile(folderPath + "/Second.asset");
                CreateProfile(folderPath + "/First.asset");
                FrameworkEditorProfileCatalog.Refresh(new[] { typeof(FrameworkHotUpdateProfile) });
                var paths = FrameworkEditorProfileCatalog.GetPaths(typeof(FrameworkHotUpdateProfile));
                int warningCount = 0;
                Application.LogCallback capture = (condition, _, type) =>
                {
                    if (type == LogType.Warning && condition.StartsWith(
                            "[热更构建] 找到多个热更 profile", StringComparison.Ordinal))
                        warningCount++;
                };
                Application.logMessageReceived += capture;
                try
                {
                    Assert.That(FrameworkHotUpdateProfile.TryResolve(out var first), Is.True);
                    Assert.That(AssetDatabase.GetAssetPath(first), Is.EqualTo(paths[0]));
                    int revision = FrameworkEditorProfileCatalog.Revision;
                    Assert.That(FrameworkHotUpdateProfile.TryResolve(out var second), Is.True);
                    Assert.That(second, Is.SameAs(first));
                    Assert.That(FrameworkEditorProfileCatalog.Revision, Is.EqualTo(revision));
                }
                finally
                {
                    Application.logMessageReceived -= capture;
                }

                Assert.That(warningCount, Is.EqualTo(1),
                    "同一 Catalog revision 内重复绘制不能重复刷出单例冲突 Warning。");

                string source = ReadScriptSource(nameof(FrameworkHotUpdateProfile));
                Assert.That(source, Does.Contain("FrameworkEditorProfileCatalog.TryResolveFirst"));
                Assert.That(source, Does.Contain("FrameworkEditorProfileCatalog.Refresh"));
                Assert.That(source, Does.Contain("GetExistingProfileOrThrow"));
                AssertSafeCreationOrdering(source);
                Assert.That(source, Does.Not.Contain("EditorApplication.projectChanged"));
                Assert.That(source, Does.Not.Contain(
                    "AssetDatabase.FindAssets(\"t:\" + nameof(FrameworkHotUpdateProfile))"),
                    "只禁止重复的 Profile 发现；创建默认程序集仍可扫描 AssemblyDefinitionAsset。");
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
                FrameworkEditorProfileCatalog.Invalidate();
            }
        }

        [Test]
        public void HotUpdateWorkbench_ConsumesSharedOperationGate()
        {
            string source = ReadScriptSource(nameof(HotUpdateBuildWindow));

            Assert.That(source, Does.Contain("FrameworkEditorOperationGate.CanStart"));
            Assert.That(source, Does.Contain("primaryRequireEditMode: null"),
                "只读校验不应被误建模为允许 Play 的副作用动作。");
            Assert.That(source, Does.Not.Contain("EditorApplication.isPlayingOrWillChangePlaymode"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isCompiling"));
            Assert.That(source, Does.Not.Contain("EditorApplication.isUpdating"));
            Assert.That(source, Does.Not.Contain("BuildPipeline.isBuildingPlayer"));
        }

        [Test]
        public void StepAvailability_MissingHotUpdateProfile_ShowsOneSharedReason()
        {
            var result = EvaluateStep(
                hasProfile: false,
                primaryPrerequisitesReady: true,
                primaryGateReady: true,
                secondaryGateReady: false);

            Assert.That(result.PrimaryReady, Is.False);
            Assert.That(result.PrimaryReason, Is.EqualTo("缺少热更配置"));
            Assert.That(result.SecondaryReady, Is.False);
            Assert.That(result.ShowSecondaryReason, Is.False,
                "共享 Profile 缺失只应解释一次，不能追加空白的次操作原因。");
        }

        [Test]
        public void StepAvailability_BusinessPrerequisiteWinsOverBusyGate()
        {
            var result = EvaluateStep(
                hasProfile: true,
                primaryPrerequisitesReady: false,
                primaryGateReady: false,
                secondaryGateReady: true);

            Assert.That(result.PrimaryReady, Is.False);
            Assert.That(result.PrimaryReason, Is.EqualTo("缺少资源构建配置"),
                "动作层会先拒绝缺失业务配置，窗口必须保持同一原因优先级。");
        }

        [Test]
        public void StepAvailability_ReadOnlyPrimaryCanRemainReadyWhileSecondaryExplainsGate()
        {
            var result = EvaluateStep(
                hasProfile: true,
                primaryPrerequisitesReady: true,
                primaryGateReady: true,
                secondaryGateReady: false);

            Assert.That(result.PrimaryReady, Is.True);
            Assert.That(result.SecondaryReady, Is.False);
            Assert.That(result.ShowSecondaryReason, Is.True);
            Assert.That(result.SecondaryReason, Is.EqualTo("Unity 忙碌"));
        }

        [Test]
        public void StepAvailability_SameGateReasonIsShownOnlyOnce()
        {
            var result = EvaluateStep(
                hasProfile: true,
                primaryPrerequisitesReady: true,
                primaryGateReady: false,
                secondaryGateReady: false);

            Assert.That(result.PrimaryReason, Is.EqualTo("Unity 忙碌"));
            Assert.That(result.ShowSecondaryReason, Is.False);
        }

        private static HotUpdateBuildWindow.StepAvailability EvaluateStep(
            bool hasProfile,
            bool primaryPrerequisitesReady,
            bool primaryGateReady,
            bool secondaryGateReady)
            => HotUpdateBuildWindow.EvaluateStepAvailability(
                hasProfile,
                primaryPrerequisitesReady,
                missingProfileReason: "缺少热更配置",
                primaryPrerequisiteReason: "缺少资源构建配置",
                primaryGateReady: primaryGateReady,
                primaryGateReason: "Unity 忙碌",
                hasSecondary: true,
                secondaryGateReady: secondaryGateReady,
                secondaryGateReason: "Unity 忙碌");

        private static AsmdefDeclaration ReadDeclaration(string assetPath)
        {
            AssemblyDefinitionAsset asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(assetPath);
            Assert.That(asset, Is.Not.Null, $"找不到程序集定义：{assetPath}");
            return JsonUtility.FromJson<AsmdefDeclaration>(asset.text);
        }

        private static void CreateProfile(string path) =>
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<FrameworkHotUpdateProfile>(), path);

        private static void AssertSafeCreationOrdering(string source)
        {
            int create = source.IndexOf("AssetDatabase.CreateAsset(profile, path);", StringComparison.Ordinal);
            int firstRefresh = source.IndexOf("FrameworkEditorProfileCatalog.Refresh", StringComparison.Ordinal);
            int lastRefresh = source.LastIndexOf("FrameworkEditorProfileCatalog.Refresh", StringComparison.Ordinal);
            int ensureDirectory = source.IndexOf("FrameworkProjectSettingsLocation.EnsureDirectory", StringComparison.Ordinal);
            int collisionCheck = source.IndexOf("GetExistingProfileOrThrow", StringComparison.Ordinal);
            int effectiveCheck = source.IndexOf("effective != profile", StringComparison.Ordinal);
            Assert.That(create, Is.GreaterThan(firstRefresh), "创建前必须强制刷新，修复尚未送达的 projectChanged。 ");
            Assert.That(ensureDirectory, Is.GreaterThan(firstRefresh), "确认确实缺少 Profile 前不得创建默认目录。 ");
            Assert.That(collisionCheck, Is.GreaterThan(ensureDirectory));
            Assert.That(create, Is.GreaterThan(collisionCheck), "CreateAsset 前必须拒绝固定路径碰撞。 ");
            Assert.That(lastRefresh, Is.GreaterThan(create), "创建后必须刷新并验证实际生效项。 ");
            Assert.That(effectiveCheck, Is.GreaterThan(create), "创建后必须确认新资产就是 stable-first 生效项。 ");
        }

        private static string ReadScriptSource(string typeName)
        {
            string[] paths = AssetDatabase.FindAssets(typeName + " t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith("/" + typeName + ".cs", StringComparison.Ordinal))
                .ToArray();
            Assert.That(paths, Has.Length.EqualTo(1), "应精确找到热更新构建 owner Module 内的源码。");
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
