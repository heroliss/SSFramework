using System;
using System.Collections.Generic;

namespace Game.NomadWorkshop.Simulation
{
    /// <summary>Foundation Prototype 首批参与居民决策的归一化需求。</summary>
    public enum ResidentNeed
    {
        Thirst = 0,
        Hunger = 1,
        Fatigue = 2,
        Health = 3,
        Count = 4,
    }

    /// <summary>候选在一次决策中的筛选和选择终态，用于测试与开发诊断。</summary>
    public enum CandidateDecisionState
    {
        Ineligible,
        SupersededByIntent,
        OutsideEmergencyPool,
        OutsideShortlist,
        Shortlisted,
        Selected,
    }

    /// <summary>
    /// 某项需求在当前决策时刻的状态。Deficit 为 0 表示满足、1 表示达到危险上限；
    /// GrowthPerSecond 描述不采取恢复行动时的自然恶化速度。
    /// </summary>
    public readonly struct ResidentNeedState
    {
        public ResidentNeedState(ResidentNeed need, float deficit, float growthPerSecond, float importance = 1f)
        {
            Need = need;
            Deficit = deficit;
            GrowthPerSecond = growthPerSecond;
            Importance = importance;
        }

        public ResidentNeed Need { get; }
        public float Deficit { get; }
        public float GrowthPerSecond { get; }
        public float Importance { get; }
    }

    /// <summary>候选行动完成时对一项需求产生的恢复量。</summary>
    public readonly struct NeedEffect
    {
        public NeedEffect(ResidentNeed need, float restore)
        {
            Need = need;
            Restore = restore;
        }

        public ResidentNeed Need { get; }
        public float Restore { get; }
    }

    /// <summary>
    /// 一次决策中的具体可执行目标，例如“在 A 饮水机喝水”或“维修动力核心”。
    /// IntentId 表示行为意图；多个同意图目标会先归并，避免设施数量放大该意图的抽中概率。
    /// </summary>
    public sealed class ResidentActionCandidate
    {
        public ResidentActionCandidate(string id, string intentId, string displayName)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("候选 id 不能为空。", nameof(id));
            if (string.IsNullOrWhiteSpace(intentId)) throw new ArgumentException("行动意图 id 不能为空。", nameof(intentId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("候选显示名不能为空。", nameof(displayName));

            Id = id;
            IntentId = intentId;
            DisplayName = displayName;
        }

        public string Id { get; }
        public string IntentId { get; }
        public string DisplayName { get; }
        public float DurationSeconds { get; set; }
        public float BaseUtility { get; set; }
        public float WorkUrgency { get; set; }
        public float PlayerPriority { get; set; }
        public float DependencyValue { get; set; }
        public float SkillFit { get; set; }
        public float PersonalAffinity { get; set; }
        public float WaitingAge { get; set; }
        public float ContinuityBonus { get; set; }
        public float TravelCost { get; set; }
        public float DurationCost { get; set; }
        public float ResourceCost { get; set; }
        public float RiskCost { get; set; }
        public float SwitchCost { get; set; }
        public float EmergencyPriority { get; set; }
        public bool IsAvailable { get; set; } = true;
        public string BlockReason { get; set; } = string.Empty;
        public NeedEffect[] NeedEffects { get; set; } = Array.Empty<NeedEffect>();
        public string[] ReservationKeys { get; set; } = Array.Empty<string>();
    }

    /// <summary>一次居民决策的不可变输入快照。</summary>
    public sealed class ResidentDecisionContext
    {
        public ResidentDecisionContext(
            int worldSeed,
            ulong residentId,
            long decisionSequence,
            IReadOnlyList<ResidentNeedState> needs,
            IReadOnlyList<ResidentActionCandidate> candidates)
        {
            WorldSeed = worldSeed;
            ResidentId = residentId;
            DecisionSequence = decisionSequence;
            Needs = needs ?? throw new ArgumentNullException(nameof(needs));
            Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        }

        public int WorldSeed { get; }
        public ulong ResidentId { get; }
        public long DecisionSequence { get; }
        public IReadOnlyList<ResidentNeedState> Needs { get; }
        public IReadOnlyList<ResidentActionCandidate> Candidates { get; }
    }

    /// <summary>Utility AI 的可调选择政策；数值是原型假设，不是永久平衡契约。</summary>
    public sealed class UtilityDecisionPolicy
    {
        public float NeedPressureExponent { get; set; } = 2.4f;
        public float CriticalDeficit { get; set; } = 0.82f;
        public float CriticalPressureBoost { get; set; } = 2.5f;
        public float EmergencyWorkThreshold { get; set; } = 0.8f;
        public float RelativeShortlistThreshold { get; set; } = 0.62f;
        public int MaxShortlistCount { get; set; } = 4;
        public float NormalTemperature { get; set; } = 0.16f;
        public float EmergencyTemperature { get; set; } = 0.035f;
        public float MinimumUtility { get; set; } = 0.0001f;
    }

    /// <summary>候选总效用的分项快照，供开发面板解释选择依据。</summary>
    public readonly struct UtilityScoreBreakdown
    {
        public UtilityScoreBreakdown(
            float baseBenefit,
            float needBenefit,
            float workBenefit,
            float personalBenefit,
            float persistenceBenefit,
            float executionCost)
        {
            BaseBenefit = baseBenefit;
            NeedBenefit = needBenefit;
            WorkBenefit = workBenefit;
            PersonalBenefit = personalBenefit;
            PersistenceBenefit = persistenceBenefit;
            ExecutionCost = executionCost;
        }

        public float BaseBenefit { get; }
        public float NeedBenefit { get; }
        public float WorkBenefit { get; }
        public float PersonalBenefit { get; }
        public float PersistenceBenefit { get; }
        public float ExecutionCost { get; }
        public float Total => BaseBenefit + NeedBenefit + WorkBenefit + PersonalBenefit + PersistenceBenefit - ExecutionCost;
    }

    /// <summary>一个候选在本次决策中的完整可解释轨迹。</summary>
    public sealed class CandidateDecisionTrace
    {
        internal CandidateDecisionTrace(ResidentActionCandidate candidate)
        {
            Candidate = candidate;
        }

        public ResidentActionCandidate Candidate { get; }
        public UtilityScoreBreakdown Score { get; internal set; }
        public CandidateDecisionState State { get; internal set; }
        public bool IsEmergency { get; internal set; }
        public double Probability { get; internal set; }
        public string Reason { get; internal set; } = string.Empty;
    }

    /// <summary>一次决策的选中项、确定性随机数和全部候选轨迹。</summary>
    public sealed class ResidentDecisionResult
    {
        internal ResidentDecisionResult(
            ResidentActionCandidate selected,
            double randomRoll,
            IReadOnlyList<CandidateDecisionTrace> traces)
        {
            Selected = selected;
            RandomRoll = randomRoll;
            Traces = traces;
        }

        public ResidentActionCandidate Selected { get; }
        public double RandomRoll { get; }
        public IReadOnlyList<CandidateDecisionTrace> Traces { get; }
        public bool HasSelection => Selected != null;
    }
}
