using System.IO;
using NUnit.Framework;

namespace Game.Framework.Editor.Tests
{
    /// <summary>锁定窗口可用态与动作二次门禁的同一状态真源。</summary>
    public sealed class FrameworkEditorOperationGateTests
    {
        [Test]
        public void IdleState_AllowsEditModeOperation()
        {
            bool ready = FrameworkEditorOperationGate.CanStart(
                new FrameworkEditorOperationState(false, false, false, false),
                requireEditMode: true,
                out string reason);

            Assert.IsTrue(ready);
            Assert.IsEmpty(reason);
        }

        [TestCase(true, false, false, false, true, "编译脚本")]
        [TestCase(false, true, false, false, true, "导入或刷新资源")]
        [TestCase(false, false, true, false, true, "构建 Player")]
        [TestCase(false, false, false, true, true, "切换 Play")]
        [TestCase(true, false, false, false, false, "编译脚本")]
        [TestCase(false, true, false, false, false, "导入或刷新资源")]
        [TestCase(false, false, true, false, false, "构建 Player")]
        public void BusyState_BlocksOperationWithActionableReason(
            bool compiling,
            bool updating,
            bool buildingPlayer,
            bool playing,
            bool requireEditMode,
            string expectedReason)
        {
            bool ready = FrameworkEditorOperationGate.CanStart(
                new FrameworkEditorOperationState(compiling, updating, buildingPlayer, playing),
                requireEditMode,
                out string reason);

            Assert.IsFalse(ready);
            StringAssert.Contains(expectedReason, reason);
        }

        [Test]
        public void PlayMode_IsAllowedOnlyWhenOperationExplicitlyOptsOutOfEditModeRequirement()
        {
            var state = new FrameworkEditorOperationState(false, false, false, true);

            Assert.IsTrue(FrameworkEditorOperationGate.CanStart(
                state, requireEditMode: false, out string reason));
            Assert.IsEmpty(reason);
        }

        [Test]
        public void MultipleBusySignals_UseStableMostImmediateReason()
        {
            var state = new FrameworkEditorOperationState(true, true, true, true);

            Assert.IsFalse(FrameworkEditorOperationGate.CanStart(
                state, requireEditMode: true, out string reason));
            StringAssert.Contains("编译脚本", reason,
                "窗口和动作日志必须在多个忙碌信号并存时给出稳定的第一原因。");
        }

        [Test]
        public void ServiceInstallerWorkbench_ConsumesSharedGateInsteadOfReimplementingUnityBusyChecks()
        {
            FrameworkModuleSourceCatalog.SourceLocation sourceLocation =
                FrameworkModuleSourceCatalog.FindUniqueFileInAssemblySource(
                    "ServiceInstallerOverviewWindow.cs", "Game.Framework.Editor");
            string source = File.ReadAllText(sourceLocation.PhysicalPath);

            AssertConsumesSharedGate(source, "ServiceInstallerOverviewWindow.cs");
        }

        private static void AssertConsumesSharedGate(string source, string fileName)
        {
            StringAssert.Contains("FrameworkEditorOperationGate.CanStart", source,
                fileName + " 必须在点击前消费共享门禁及其原因。");
            StringAssert.DoesNotContain("EditorApplication.isPlayingOrWillChangePlaymode", source,
                fileName + " 不得只手写 Play 判断而遗漏编译、导入与 Player 构建。");
            StringAssert.DoesNotContain("EditorApplication.isCompiling", source);
            StringAssert.DoesNotContain("EditorApplication.isUpdating", source);
            StringAssert.DoesNotContain("BuildPipeline.isBuildingPlayer", source);
        }
    }
}
