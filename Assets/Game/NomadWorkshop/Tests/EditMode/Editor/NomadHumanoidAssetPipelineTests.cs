using System.Linq;
using Game.NomadWorkshop.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.NomadWorkshop.Editor.Tests
{
    /// <summary>把 Avatar、动作、材质和 Controller 的导入结论固化为可重复门禁。</summary>
    public sealed class NomadHumanoidAssetPipelineTests
    {
        private static readonly (string Path, bool Loop)[] ExpectedClips =
        {
            (NomadHumanoidAssetPipeline.ClipFolder + "/Resident_Idle.anim", true),
            (NomadHumanoidAssetPipeline.ClipFolder + "/Resident_Walk.anim", true),
            (NomadHumanoidAssetPipeline.ClipFolder + "/Resident_Pickup.anim", false),
            (NomadHumanoidAssetPipeline.ClipFolder + "/Resident_Work.anim", false),
            (NomadHumanoidAssetPipeline.ClipFolder + "/Resident_Rest.anim", true),
        };

        [Test]
        public void CharacterModelHasValidHumanAvatarAndStableImporterPolicy()
        {
            HumanoidAssetAudit audit = NomadHumanoidAssetPipeline.Audit(
                NomadHumanoidAssetPipeline.CharacterModelPath);
            Assert.IsTrue(audit.HasAvatar);
            Assert.IsTrue(audit.AvatarIsValid);
            Assert.IsTrue(audit.AvatarIsHuman);
            Assert.That(audit.ClipNames, Is.Empty, "角色模型不应偷偷携带另一套动作真值。");

            var importer = AssetImporter.GetAtPath(NomadHumanoidAssetPipeline.CharacterModelPath) as ModelImporter;
            Assert.IsNotNull(importer);
            Assert.AreEqual(ModelImporterAnimationType.Human, importer.animationType);
            Assert.AreEqual(ModelImporterAvatarSetup.CreateFromThisModel, importer.avatarSetup);
            Assert.IsFalse(importer.importAnimation);
            Assert.IsFalse(importer.isReadable);
        }

        [Test]
        public void SelectedClipsAreFiveHumanMotionsWithIntentionalLoopPolicy()
        {
            NomadHumanoidAssetPipeline.ValidateGeneratedArtifacts();
            Assert.AreEqual(5, ExpectedClips.Length);
            for (int i = 0; i < ExpectedClips.Length; i++)
            {
                (string path, bool expectedLoop) = ExpectedClips[i];
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                Assert.IsNotNull(clip, path);
                Assert.IsTrue(clip.humanMotion, path);
                Assert.AreEqual(expectedLoop, clip.isLooping, path);
                Assert.Greater(clip.length, 0.5f, path);
                Assert.AreEqual(30f, clip.frameRate, 0.01f, path);
            }
        }

        [Test]
        public void ControllerExposesOnlyFiveStableSemanticStates()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                NomadHumanoidAssetPipeline.ControllerPath);
            Assert.IsNotNull(controller);
            Assert.AreEqual(1, controller.layers.Length);

            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            string[] actualNames = stateMachine.states.Select(child => child.state.name).ToArray();
            string[] expectedNames =
            {
                ResidentAnimationStates.Idle,
                ResidentAnimationStates.Move,
                ResidentAnimationStates.Pickup,
                ResidentAnimationStates.Work,
                ResidentAnimationStates.Rest,
            };
            CollectionAssert.AreEquivalent(expectedNames, actualNames);
            Assert.AreEqual(ResidentAnimationStates.Idle, stateMachine.defaultState.name);
            Assert.That(stateMachine.states.All(child => child.state.motion is AnimationClip), Is.True);
        }

        [Test]
        public void CharacterMaterialsUseUrpLitAndNormalMapsAreImportedAsNormals()
        {
            string[] materialPaths =
            {
                NomadHumanoidAssetPipeline.BodyMaterialPath,
                NomadHumanoidAssetPipeline.EyeMaterialPath,
                NomadHumanoidAssetPipeline.HairMaterialPath,
            };
            for (int i = 0; i < materialPaths.Length; i++)
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPaths[i]);
                Assert.IsNotNull(material, materialPaths[i]);
                Assert.AreEqual("Universal Render Pipeline/Lit", material.shader.name, materialPaths[i]);
            }

            string assetFolder = "Assets/Game/NomadWorkshop/ThirdParty/QuaterniusUniversalBaseCharacters/";
            foreach (string fileName in new[] { "T_Superhero_Male_Normal.png", "T_Eye_Normal.png" })
            {
                var importer = AssetImporter.GetAtPath(assetFolder + fileName) as TextureImporter;
                Assert.IsNotNull(importer, fileName);
                Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType, fileName);
                Assert.IsFalse(importer.sRGBTexture, fileName);
            }
        }

        [Test]
        public void FacilityAnchorRequiresAStandPointAndKeepsPresentationOnlyData()
        {
            var facility = new GameObject("Facility");
            var standPoint = new GameObject("StandPoint");
            try
            {
                standPoint.transform.SetParent(facility.transform, false);
                FacilityInteractionAnchor anchor = facility.AddComponent<FacilityInteractionAnchor>();
                Assert.Throws<System.ArgumentException>(() =>
                    anchor.ConfigureRuntime("", ResidentAnimationSemantic.Work, standPoint.transform));

                anchor.ConfigureRuntime("repair", ResidentAnimationSemantic.Work, standPoint.transform);
                Assert.IsTrue(anchor.IsConfigured);
                Assert.AreEqual("repair", anchor.ActionId);
                Assert.AreEqual(ResidentAnimationSemantic.Work, anchor.AnimationSemantic);
                Assert.AreSame(standPoint.transform, anchor.StandPoint);
            }
            finally
            {
                Object.DestroyImmediate(facility);
            }
        }
    }
}
