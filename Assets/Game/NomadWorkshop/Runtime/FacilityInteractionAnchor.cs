using System;
using UnityEngine;

namespace Game.NomadWorkshop
{
    /// <summary>
    /// 设施与居民之间的表现接缝：声明站位、朝向、动作语义和可选手部目标。
    /// 它不负责占用、寻路或任务结算；这些仍由模拟与执行层拥有。
    /// </summary>
    public sealed class FacilityInteractionAnchor : MonoBehaviour
    {
        [SerializeField] private string actionId;
        [SerializeField] private ResidentAnimationSemantic animationSemantic;
        [SerializeField] private Transform standPoint;
        [SerializeField] private Transform primaryHandTarget;

        public string ActionId => actionId;

        public ResidentAnimationSemantic AnimationSemantic => animationSemantic;

        public Transform StandPoint => standPoint;

        public Transform PrimaryHandTarget => primaryHandTarget;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(actionId) && standPoint != null;

        /// <summary>供程序灰盒创建锚点；正式设施 Prefab 应改由 Inspector 固化同一组引用。</summary>
        public void ConfigureRuntime(
            string configuredActionId,
            ResidentAnimationSemantic semantic,
            Transform configuredStandPoint,
            Transform configuredPrimaryHandTarget = null)
        {
            if (string.IsNullOrWhiteSpace(configuredActionId))
                throw new ArgumentException("设施交互锚点必须提供行动 id。", nameof(configuredActionId));
            if (configuredStandPoint == null)
                throw new ArgumentNullException(nameof(configuredStandPoint));

            actionId = configuredActionId;
            animationSemantic = semantic;
            standPoint = configuredStandPoint;
            primaryHandTarget = configuredPrimaryHandTarget;
        }
    }
}
