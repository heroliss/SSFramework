using System;
using System.Collections.Generic;

namespace Game.NomadWorkshop.Simulation
{
    /// <summary>
    /// 纯 C#、无 Unity 依赖的居民效用选择器。它只回答“下一步做什么”，不拥有寻路、动画、
    /// 资源结算或任务生命周期；调用方应在选中后原子预留目标，再交给行动执行器。
    /// </summary>
    public sealed class UtilityDecisionEngine
    {
        /// <summary>
        /// 在不可变快照上完成候选过滤、同意图归并、紧急保护、短名单与确定性 Softmax 抽样。
        /// 配置错误抛出异常；没有正效用候选时返回无选择结果，并保留全部诊断轨迹。
        /// </summary>
        public ResidentDecisionResult Decide(ResidentDecisionContext context, UtilityDecisionPolicy policy = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            policy ??= new UtilityDecisionPolicy();
            ValidatePolicy(policy);

            ResidentNeedState[] needs = BuildNeedTable(context.Needs);
            var orderedCandidates = new List<ResidentActionCandidate>(context.Candidates.Count);
            for (int i = 0; i < context.Candidates.Count; i++)
            {
                ResidentActionCandidate candidate = context.Candidates[i]
                    ?? throw new ArgumentException($"候选列表第 {i} 项为 null。", nameof(context));
                orderedCandidates.Add(candidate);
            }
            orderedCandidates.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            EnsureUniqueCandidateIds(orderedCandidates);

            var traces = new List<CandidateDecisionTrace>(orderedCandidates.Count);
            for (int i = 0; i < orderedCandidates.Count; i++)
            {
                ResidentActionCandidate candidate = orderedCandidates[i];
                var trace = new CandidateDecisionTrace(candidate);
                traces.Add(trace);

                if (!candidate.IsAvailable)
                {
                    trace.State = CandidateDecisionState.Ineligible;
                    trace.Reason = string.IsNullOrWhiteSpace(candidate.BlockReason)
                        ? "候选当前不可执行"
                        : candidate.BlockReason;
                    continue;
                }

                trace.Score = ScoreCandidate(candidate, needs, policy, out bool criticalNeed);
                trace.IsEmergency = criticalNeed || candidate.EmergencyPriority >= policy.EmergencyWorkThreshold;
                trace.State = trace.Score.Total >= policy.MinimumUtility
                    ? CandidateDecisionState.Shortlisted
                    : CandidateDecisionState.OutsideShortlist;
                if (trace.State == CandidateDecisionState.OutsideShortlist)
                    trace.Reason = "总效用低于最小阈值";
            }

            List<CandidateDecisionTrace> intentWinners = SelectIntentWinners(traces);
            if (intentWinners.Count == 0)
                return new ResidentDecisionResult(null, DecisionRandom.Sample01(context), traces);

            var emergencyPool = new List<CandidateDecisionTrace>();
            for (int i = 0; i < intentWinners.Count; i++)
            {
                if (intentWinners[i].IsEmergency) emergencyPool.Add(intentWinners[i]);
            }

            List<CandidateDecisionTrace> selectionPool;
            bool emergencyDecision = emergencyPool.Count > 0;
            if (emergencyDecision)
            {
                selectionPool = emergencyPool;
                for (int i = 0; i < intentWinners.Count; i++)
                {
                    CandidateDecisionTrace trace = intentWinners[i];
                    if (trace.IsEmergency) continue;
                    trace.State = CandidateDecisionState.OutsideEmergencyPool;
                    trace.Reason = "存在可执行的紧急候选";
                }
            }
            else
            {
                selectionPool = intentWinners;
            }

            selectionPool.Sort(CompareByUtilityThenId);
            List<CandidateDecisionTrace> shortlist = BuildShortlist(selectionPool, policy);
            double roll = DecisionRandom.Sample01(context);
            ResidentActionCandidate selected = SelectBySoftmax(
                shortlist,
                emergencyDecision ? policy.EmergencyTemperature : policy.NormalTemperature,
                roll);

            for (int i = 0; i < shortlist.Count; i++)
            {
                CandidateDecisionTrace trace = shortlist[i];
                if (ReferenceEquals(trace.Candidate, selected))
                {
                    trace.State = CandidateDecisionState.Selected;
                    trace.Reason = "命中确定性加权随机区间";
                }
                else
                {
                    trace.State = CandidateDecisionState.Shortlisted;
                    trace.Reason = "进入短名单但本次未抽中";
                }
            }

            return new ResidentDecisionResult(selected, roll, traces);
        }

        private static ResidentNeedState[] BuildNeedTable(IReadOnlyList<ResidentNeedState> source)
        {
            int count = (int)ResidentNeed.Count;
            var table = new ResidentNeedState[count];
            var seen = new bool[count];
            for (int i = 0; i < count; i++)
                table[i] = new ResidentNeedState((ResidentNeed)i, 0f, 0f, 1f);
            for (int i = 0; i < source.Count; i++)
            {
                ResidentNeedState state = source[i];
                int index = (int)state.Need;
                if (index < 0 || index >= count)
                    throw new ArgumentOutOfRangeException(nameof(source), state.Need, "未知的居民需求类型。");
                if (seen[index])
                    throw new ArgumentException($"需求 {state.Need} 在同一快照中重复。", nameof(source));
                if (state.GrowthPerSecond < 0f)
                    throw new ArgumentOutOfRangeException(nameof(source), "需求自然变化速度不能为负。恢复应由行动效果表达。");
                if (state.Importance < 0f)
                    throw new ArgumentOutOfRangeException(nameof(source), "需求重要度不能为负。");
                if (state.Deficit < 0f || state.Deficit > 1f)
                    throw new ArgumentOutOfRangeException(nameof(source), "归一化需求缺口必须在 [0, 1] 内。");

                table[index] = state;
                seen[index] = true;
            }
            return table;
        }

        private static void EnsureUniqueCandidateIds(IReadOnlyList<ResidentActionCandidate> candidates)
        {
            for (int i = 1; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i - 1].Id, candidates[i].Id, StringComparison.Ordinal))
                    throw new ArgumentException($"候选 id '{candidates[i].Id}' 重复。", nameof(candidates));
            }
        }

        private static UtilityScoreBreakdown ScoreCandidate(
            ResidentActionCandidate candidate,
            IReadOnlyList<ResidentNeedState> needs,
            UtilityDecisionPolicy policy,
            out bool criticalNeed)
        {
            if (candidate.DurationSeconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(candidate), candidate.DurationSeconds, "行动时长不能为负。");

            int needCount = (int)ResidentNeed.Count;
            NeedEffect[] effects = candidate.NeedEffects ?? Array.Empty<NeedEffect>();
            for (int i = 0; i < effects.Length; i++)
            {
                NeedEffect effect = effects[i];
                int index = (int)effect.Need;
                if (index < 0 || index >= needCount)
                    throw new ArgumentOutOfRangeException(nameof(candidate), effect.Need, "候选包含未知需求效果。");
                if (effect.Restore < 0f)
                    throw new ArgumentOutOfRangeException(nameof(candidate), effect.Restore, "恢复量不能为负。");
            }

            float needBenefit = 0f;
            criticalNeed = false;
            for (int i = 0; i < needCount; i++)
            {
                float restore = 0f;
                for (int effectIndex = 0; effectIndex < effects.Length; effectIndex++)
                {
                    if ((int)effects[effectIndex].Need == i) restore += effects[effectIndex].Restore;
                }
                if (restore <= 0f) continue;

                ResidentNeedState state = needs[i];
                float predicted = Clamp01(state.Deficit + state.GrowthPerSecond * candidate.DurationSeconds);
                float after = Clamp01(predicted - restore);
                float beforePressure = EvaluatePressure(predicted, policy);
                float afterPressure = EvaluatePressure(after, policy);
                needBenefit += Math.Max(0f, beforePressure - afterPressure) * state.Importance;
                if (predicted >= policy.CriticalDeficit) criticalNeed = true;
            }

            float workBenefit = candidate.WorkUrgency + candidate.PlayerPriority + candidate.DependencyValue;
            float personalBenefit = candidate.SkillFit + candidate.PersonalAffinity;
            float persistenceBenefit = candidate.WaitingAge + candidate.ContinuityBonus;
            float executionCost = candidate.TravelCost + candidate.DurationCost + candidate.ResourceCost
                                  + candidate.RiskCost + candidate.SwitchCost;
            return new UtilityScoreBreakdown(
                candidate.BaseUtility,
                needBenefit,
                workBenefit,
                personalBenefit,
                persistenceBenefit,
                executionCost);
        }

        private static float EvaluatePressure(float deficit, UtilityDecisionPolicy policy)
        {
            float value = Clamp01(deficit);
            double pressure = Math.Pow(value, policy.NeedPressureExponent);
            if (value > policy.CriticalDeficit)
            {
                float criticalRange = 1f - policy.CriticalDeficit;
                float normalized = (value - policy.CriticalDeficit) / criticalRange;
                pressure += policy.CriticalPressureBoost * normalized * normalized;
            }
            return (float)pressure;
        }

        private static List<CandidateDecisionTrace> SelectIntentWinners(IReadOnlyList<CandidateDecisionTrace> traces)
        {
            var winnersByIntent = new Dictionary<string, CandidateDecisionTrace>(StringComparer.Ordinal);
            for (int i = 0; i < traces.Count; i++)
            {
                CandidateDecisionTrace candidate = traces[i];
                if (candidate.State != CandidateDecisionState.Shortlisted) continue;

                if (!winnersByIntent.TryGetValue(candidate.Candidate.IntentId, out CandidateDecisionTrace current))
                {
                    winnersByIntent.Add(candidate.Candidate.IntentId, candidate);
                    continue;
                }

                if (IsBetter(candidate, current))
                {
                    current.State = CandidateDecisionState.SupersededByIntent;
                    current.Reason = $"同意图存在更优目标：{candidate.Candidate.DisplayName}";
                    winnersByIntent[candidate.Candidate.IntentId] = candidate;
                }
                else
                {
                    candidate.State = CandidateDecisionState.SupersededByIntent;
                    candidate.Reason = $"同意图存在更优目标：{current.Candidate.DisplayName}";
                }
            }

            var winners = new List<CandidateDecisionTrace>(winnersByIntent.Count);
            foreach (CandidateDecisionTrace winner in winnersByIntent.Values) winners.Add(winner);
            return winners;
        }

        private static List<CandidateDecisionTrace> BuildShortlist(
            IReadOnlyList<CandidateDecisionTrace> orderedPool,
            UtilityDecisionPolicy policy)
        {
            var shortlist = new List<CandidateDecisionTrace>(Math.Min(policy.MaxShortlistCount, orderedPool.Count));
            float best = orderedPool[0].Score.Total;
            float threshold = best * policy.RelativeShortlistThreshold;
            for (int i = 0; i < orderedPool.Count; i++)
            {
                CandidateDecisionTrace trace = orderedPool[i];
                if (shortlist.Count < policy.MaxShortlistCount && trace.Score.Total >= threshold)
                {
                    shortlist.Add(trace);
                    continue;
                }

                trace.State = CandidateDecisionState.OutsideShortlist;
                trace.Reason = shortlist.Count >= policy.MaxShortlistCount
                    ? "超过短名单数量上限"
                    : "效用低于本次相对阈值";
            }
            return shortlist;
        }

        private static ResidentActionCandidate SelectBySoftmax(
            IReadOnlyList<CandidateDecisionTrace> shortlist,
            float temperature,
            double roll)
        {
            if (shortlist.Count == 0) return null;
            if (shortlist.Count == 1 || temperature <= 0.000001f)
            {
                shortlist[0].Probability = 1d;
                return shortlist[0].Candidate;
            }

            double best = shortlist[0].Score.Total;
            var weights = new double[shortlist.Count];
            double sum = 0d;
            for (int i = 0; i < shortlist.Count; i++)
            {
                double weight = Math.Exp((shortlist[i].Score.Total - best) / temperature);
                weights[i] = weight;
                sum += weight;
            }

            double cumulative = 0d;
            for (int i = 0; i < shortlist.Count; i++)
            {
                double probability = weights[i] / sum;
                shortlist[i].Probability = probability;
                cumulative += probability;
                if (roll < cumulative) return shortlist[i].Candidate;
            }

            return shortlist[shortlist.Count - 1].Candidate;
        }

        private static int CompareByUtilityThenId(CandidateDecisionTrace left, CandidateDecisionTrace right)
        {
            int score = right.Score.Total.CompareTo(left.Score.Total);
            return score != 0 ? score : string.CompareOrdinal(left.Candidate.Id, right.Candidate.Id);
        }

        private static bool IsBetter(CandidateDecisionTrace candidate, CandidateDecisionTrace current)
        {
            int score = candidate.Score.Total.CompareTo(current.Score.Total);
            return score > 0 || score == 0 && string.CompareOrdinal(candidate.Candidate.Id, current.Candidate.Id) < 0;
        }

        private static void ValidatePolicy(UtilityDecisionPolicy policy)
        {
            if (policy.NeedPressureExponent <= 0f)
                throw new ArgumentOutOfRangeException(nameof(policy), "需求压力指数必须大于零。");
            if (policy.CriticalDeficit <= 0f || policy.CriticalDeficit >= 1f)
                throw new ArgumentOutOfRangeException(nameof(policy), "危险需求阈值必须在 (0, 1) 内。");
            if (policy.CriticalPressureBoost < 0f)
                throw new ArgumentOutOfRangeException(nameof(policy), "危险压力增益不能为负。");
            if (policy.EmergencyWorkThreshold < 0f)
                throw new ArgumentOutOfRangeException(nameof(policy), "紧急工作阈值不能为负。");
            if (policy.RelativeShortlistThreshold < 0f || policy.RelativeShortlistThreshold > 1f)
                throw new ArgumentOutOfRangeException(nameof(policy), "短名单相对阈值必须在 [0, 1] 内。");
            if (policy.MaxShortlistCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(policy), "短名单数量必须大于零。");
            if (policy.NormalTemperature < 0f || policy.EmergencyTemperature < 0f)
                throw new ArgumentOutOfRangeException(nameof(policy), "随机温度不能为负。");
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            return value > 1f ? 1f : value;
        }

        private static class DecisionRandom
        {
            public static double Sample01(ResidentDecisionContext context)
            {
                ulong value = unchecked((uint)context.WorldSeed);
                value ^= Mix(context.ResidentId + 0x9E3779B97F4A7C15UL);
                value ^= Mix(unchecked((ulong)context.DecisionSequence) + 0xD1B54A32D192ED03UL);
                value = Mix(value);
                return (value >> 11) * (1d / 9007199254740992d);
            }

            private static ulong Mix(ulong value)
            {
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
