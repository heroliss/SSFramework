using UnityEngine;

namespace Game.NomadWorkshop
{
    /// <summary>模拟行动到表现动作的稳定语义；它不暴露第三方 Clip 名称。</summary>
    public enum ResidentAnimationSemantic
    {
        Idle,
        Move,
        Pickup,
        Work,
        Rest,
    }

    /// <summary>Animator Controller 的状态契约，由生成工具和运行时共同使用。</summary>
    public static class ResidentAnimationStates
    {
        public const string Idle = "Idle";
        public const string Move = "Move";
        public const string Pickup = "Pickup";
        public const string Work = "Work";
        public const string Rest = "Rest";

        public static string GetStateName(ResidentAnimationSemantic semantic)
        {
            return semantic switch
            {
                ResidentAnimationSemantic.Move => Move,
                ResidentAnimationSemantic.Pickup => Pickup,
                ResidentAnimationSemantic.Work => Work,
                ResidentAnimationSemantic.Rest => Rest,
                _ => Idle,
            };
        }
    }

    /// <summary>
    /// 将一个已验证的 Humanoid Prefab 和共享 Animator Controller 接到居民表现根。
    /// 模拟仍拥有位置、朝向和行动结果；本组件只切换动画，不接受 Root Motion 反向改写模拟。
    /// </summary>
    public sealed class ResidentHumanoidPresentation : MonoBehaviour
    {
        private Animator _animator;
        private Transform _visualRoot;
        private ResidentAnimationSemantic? _currentSemantic;

        public bool IsReady =>
            _animator != null &&
            _animator.avatar != null &&
            _animator.avatar.isValid &&
            _animator.avatar.isHuman;

        public Transform VisualRoot => _visualRoot;

        public Animator Animator => _animator;

        /// <summary>
        /// 创建表现实例并验证 Avatar 与五个稳定状态。失败时返回 false，由调用方保留程序假人回退。
        /// </summary>
        public bool TryInitialize(GameObject humanoidPrefab, RuntimeAnimatorController controller)
        {
            if (humanoidPrefab == null || controller == null) return false;

            GameObject instance = Instantiate(humanoidPrefab, transform, false);
            instance.name = "HumanoidVisual";
            _visualRoot = instance.transform;
            _visualRoot.localPosition = Vector3.zero;
            _visualRoot.localRotation = Quaternion.identity;
            _visualRoot.localScale = Vector3.one;

            _animator = instance.GetComponentInChildren<Animator>(true);
            if (_animator == null)
            {
                Destroy(instance);
                _visualRoot = null;
                return false;
            }

            _animator.runtimeAnimatorController = controller;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.updateMode = AnimatorUpdateMode.Normal;
            _animator.Rebind();
            _animator.Update(0f);

            if (!IsReady || !HasAllRequiredStates())
            {
                Destroy(instance);
                _animator = null;
                _visualRoot = null;
                return false;
            }

            SetSemantic(ResidentAnimationSemantic.Idle, true);
            return true;
        }

        public void SetSemantic(ResidentAnimationSemantic semantic, bool restart = false)
        {
            if (!IsReady || (!restart && _currentSemantic == semantic)) return;

            int stateHash = Animator.StringToHash(ResidentAnimationStates.GetStateName(semantic));
            _animator.CrossFadeInFixedTime(stateHash, 0.12f, 0, 0f);
            _currentSemantic = semantic;
        }

        /// <summary>让表现速度跟随当前模拟倍率；传入 0 可冻结姿态，不改变 Animator 的状态所有权。</summary>
        public void SetPlaybackSpeed(float speed)
        {
            if (_animator != null) _animator.speed = Mathf.Max(0f, speed);
        }

        private bool HasAllRequiredStates()
        {
            string[] names =
            {
                ResidentAnimationStates.Idle,
                ResidentAnimationStates.Move,
                ResidentAnimationStates.Pickup,
                ResidentAnimationStates.Work,
                ResidentAnimationStates.Rest,
            };

            for (int i = 0; i < names.Length; i++)
            {
                if (!_animator.HasState(0, Animator.StringToHash(names[i]))) return false;
            }

            return true;
        }
    }
}
