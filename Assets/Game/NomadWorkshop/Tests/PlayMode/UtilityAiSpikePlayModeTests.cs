using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.NomadWorkshop.PlayMode.Tests
{
    /// <summary>验证灰盒展示器能在隔离场景生成 3D 世界，并把纯 C# 决策推进成可观察移动。</summary>
    public sealed class UtilityAiSpikePlayModeTests
    {
        private Scene _previousScene;
        private Scene _testScene;
        private GameObject _cameraObject;
        private GameObject _root;
        private Camera _camera;
        private UtilityAiSpikeController _controller;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _previousScene = SceneManager.GetActiveScene();
            _testScene = SceneManager.CreateScene("NomadWorkshopUtilityAiSpikeTest");
            Assert.IsTrue(SceneManager.SetActiveScene(_testScene));

            _cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            _camera = _cameraObject.AddComponent<Camera>();
            _cameraObject.AddComponent<AudioListener>();

            _root = new GameObject("NomadWorkshop Utility AI Spike Test");
            _controller = _root.AddComponent<UtilityAiSpikeController>();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) Object.Destroy(_root);
            if (_cameraObject != null) Object.Destroy(_cameraObject);
            yield return null;

            if (_previousScene.IsValid() && _previousScene.isLoaded)
                Assert.IsTrue(SceneManager.SetActiveScene(_previousScene));
            if (_testScene.IsValid() && _testScene.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(_testScene);
                while (unload != null && !unload.isDone) yield return null;
            }
        }

        [UnityTest]
        public IEnumerator SpikeBuildsOneCameraResidentAndLiveDecision()
        {
            Assert.IsTrue(_controller.HasGeneratedResident);
            Assert.IsNotNull(_controller.LastDecision);
            Assert.IsTrue(_controller.LastDecision.HasSelection);
            Assert.GreaterOrEqual(_controller.DecisionCount, 1);
            Assert.AreSame(_camera, FindOnlyCamera(_testScene));
            Assert.AreEqual(6, _controller.InteractionAnchorCount);
            Assert.IsFalse(_controller.HasHumanoidResident,
                "未提供资产引用的隔离测试必须证明程序假人回退仍可用。");

            Transform resident = _root.transform.Find("Resident_Ada");
            Assert.IsNotNull(resident);
            Vector3 start = resident.position;
            yield return null;
            yield return null;
            Assert.Greater(Vector3.Distance(start, resident.position), 0.001f,
                "选出行动后，展示层应开始向设施交互位移动。 ");
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator SelectedHumanoidAndControllerInstantiateAcrossAllSemanticStates()
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Game/NomadWorkshop/ThirdParty/QuaterniusUniversalBaseCharacters/Superhero_Male_FullBody.fbx");
            RuntimeAnimatorController animatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/Game/NomadWorkshop/Animation/NomadResident.controller");
            Assert.IsNotNull(model);
            Assert.IsNotNull(animatorController);

            var host = new GameObject("Humanoid Presentation Test");
            try
            {
                ResidentHumanoidPresentation presentation = host.AddComponent<ResidentHumanoidPresentation>();
                Assert.IsTrue(presentation.TryInitialize(model, animatorController));
                Assert.IsTrue(presentation.IsReady);
                Assert.IsFalse(presentation.Animator.applyRootMotion);

                ResidentAnimationSemantic[] semantics =
                {
                    ResidentAnimationSemantic.Idle,
                    ResidentAnimationSemantic.Move,
                    ResidentAnimationSemantic.Pickup,
                    ResidentAnimationSemantic.Work,
                    ResidentAnimationSemantic.Rest,
                };
                for (int i = 0; i < semantics.Length; i++)
                {
                    ResidentAnimationSemantic semantic = semantics[i];
                    presentation.SetSemantic(semantic, true);
                    presentation.Animator.Update(0.2f);
                    Assert.IsTrue(
                        presentation.Animator.GetCurrentAnimatorStateInfo(0).IsName(
                            ResidentAnimationStates.GetStateName(semantic)),
                        semantic.ToString());
                }
                yield return null;
            }
            finally
            {
                Object.Destroy(host);
            }
        }
#endif

        private static Camera FindOnlyCamera(Scene scene)
        {
            Camera found = null;
            Camera[] cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera candidate = cameras[i];
                if (candidate.gameObject.scene != scene) continue;
                Assert.IsNull(found, "隔离场景内不应重复创建主相机。");
                found = candidate;
            }
            return found;
        }
    }
}
