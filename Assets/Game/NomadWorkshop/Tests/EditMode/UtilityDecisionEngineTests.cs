using System.Collections.Generic;
using NUnit.Framework;

namespace Game.NomadWorkshop.Simulation.Tests
{
    /// <summary>锁定有界随机 Utility AI 的生存保护、组合需求、去重与确定性契约。</summary>
    public sealed class UtilityDecisionEngineTests
    {
        private readonly UtilityDecisionEngine _engine = new();

        [Test]
        public void CriticalThirst_RestrictsSelectionToEmergencyRecovery()
        {
            ResidentActionCandidate drink = Candidate("drink", "drink", "饮水", 0.02f);
            drink.DurationSeconds = 3f;
            drink.NeedEffects = new[] { new NeedEffect(ResidentNeed.Thirst, 0.75f) };

            ResidentActionCandidate repair = Candidate("repair", "repair", "维修动力核心", 0.05f);
            repair.WorkUrgency = 0.9f;
            ResidentActionCandidate paint = Candidate("paint", "leisure", "作画", 0.75f);

            ResidentDecisionResult result = _engine.Decide(Context(
                worldSeed: 17,
                sequence: 0,
                Needs(thirst: 0.95f, hunger: 0.2f, fatigue: 0.2f),
                drink, repair, paint));

            Assert.AreSame(drink, result.Selected);
            Assert.AreEqual(CandidateDecisionState.Selected, Trace(result, "drink").State);
            Assert.AreEqual(CandidateDecisionState.OutsideEmergencyPool, Trace(result, "repair").State);
            Assert.AreEqual(CandidateDecisionState.OutsideEmergencyPool, Trace(result, "paint").State);
        }

        [Test]
        public void MultiNeedMeal_OnlyScoresPressureThatActuallyExists()
        {
            ResidentActionCandidate meal = Candidate("meal", "eat", "吃一顿饭", 0f);
            meal.DurationSeconds = 5f;
            meal.NeedEffects = new[]
            {
                new NeedEffect(ResidentNeed.Hunger, 0.55f),
                new NeedEffect(ResidentNeed.Thirst, 0.2f),
                new NeedEffect(ResidentNeed.Fatigue, 0.1f),
            };

            ResidentDecisionResult pressured = _engine.Decide(Context(
                9, 2, Needs(thirst: 0.7f, hunger: 0.65f, fatigue: 0.55f), meal));
            ResidentDecisionResult satisfied = _engine.Decide(Context(
                9, 2, Needs(thirst: 0f, hunger: 0.65f, fatigue: 0.55f), meal));

            float pressuredBenefit = Trace(pressured, "meal").Score.NeedBenefit;
            float satisfiedBenefit = Trace(satisfied, "meal").Score.NeedBenefit;
            Assert.Greater(pressuredBenefit, satisfiedBenefit,
                "不口渴时，饭菜附带的补水不能继续贡献同样效用。 ");
        }

        [Test]
        public void SameSnapshot_ReproducesRollSelectionAndTrace()
        {
            ResidentActionCandidate repair = Candidate("repair", "repair", "维修", 0.55f);
            ResidentActionCandidate haul = Candidate("haul", "haul", "搬运", 0.52f);
            ResidentActionCandidate rest = Candidate("rest", "rest", "休息", 0.48f);
            ResidentDecisionContext context = Context(
                778, 31, Needs(thirst: 0.25f, hunger: 0.25f, fatigue: 0.25f), repair, haul, rest);

            ResidentDecisionResult first = _engine.Decide(context);
            ResidentDecisionResult second = _engine.Decide(context);

            Assert.AreEqual(first.RandomRoll, second.RandomRoll);
            Assert.AreEqual(first.Selected.Id, second.Selected.Id);
            Assert.AreEqual(first.Traces.Count, second.Traces.Count);
            for (int i = 0; i < first.Traces.Count; i++)
            {
                Assert.AreEqual(first.Traces[i].Candidate.Id, second.Traces[i].Candidate.Id);
                Assert.AreEqual(first.Traces[i].Score.Total, second.Traces[i].Score.Total);
                Assert.AreEqual(first.Traces[i].Probability, second.Traces[i].Probability);
                Assert.AreEqual(first.Traces[i].State, second.Traces[i].State);
            }
        }

        [Test]
        public void DifferentSeeds_VaryInsideShortlistButNeverSelectLowUtilityCandidate()
        {
            var selected = new HashSet<string>();
            for (int seed = 1; seed <= 64; seed++)
            {
                ResidentActionCandidate a = Candidate("a", "a", "合理 A", 0.6f);
                ResidentActionCandidate b = Candidate("b", "b", "合理 B", 0.6f);
                ResidentActionCandidate c = Candidate("c", "c", "合理 C", 0.6f);
                ResidentActionCandidate low = Candidate("low", "low", "明显低价值", 0.1f);
                ResidentDecisionResult result = _engine.Decide(Context(
                    seed, 0, Needs(0.2f, 0.2f, 0.2f), a, b, c, low));

                Assert.AreNotEqual("low", result.Selected.Id);
                selected.Add(result.Selected.Id);
            }

            Assert.Greater(selected.Count, 1, "改变世界 Seed 后，高质量候选之间应能出现自然变化。");
        }

        [Test]
        public void RepeatedTargets_DoNotMultiplyIntentProbability()
        {
            ResidentActionCandidate repairA = Candidate("repair", "repair", "维修", 0.56f);
            ResidentActionCandidate chairA = Candidate("chair-a", "leisure", "椅子 A", 0.5f);
            ResidentDecisionResult oneChair = _engine.Decide(Context(
                73, 8, Needs(0.2f, 0.2f, 0.2f), repairA, chairA));

            ResidentActionCandidate repairB = Candidate("repair", "repair", "维修", 0.56f);
            ResidentActionCandidate chairB1 = Candidate("chair-a", "leisure", "椅子 A", 0.5f);
            ResidentActionCandidate chairB2 = Candidate("chair-b", "leisure", "椅子 B", 0.5f);
            ResidentActionCandidate chairB3 = Candidate("chair-c", "leisure", "椅子 C", 0.5f);
            ResidentDecisionResult threeChairs = _engine.Decide(Context(
                73, 8, Needs(0.2f, 0.2f, 0.2f), repairB, chairB1, chairB2, chairB3));

            Assert.AreEqual(oneChair.Selected.IntentId, threeChairs.Selected.IntentId);
            Assert.AreEqual(
                Trace(oneChair, "chair-a").Probability,
                Trace(threeChairs, "chair-a").Probability,
                0.0000001d);
            Assert.AreEqual(CandidateDecisionState.SupersededByIntent, Trace(threeChairs, "chair-b").State);
            Assert.AreEqual(CandidateDecisionState.SupersededByIntent, Trace(threeChairs, "chair-c").State);
        }

        [Test]
        public void NoPositiveUtility_ReturnsNoSelectionWithTrace()
        {
            ResidentActionCandidate blocked = Candidate("blocked", "repair", "缺零件的维修", 1f);
            blocked.IsAvailable = false;
            blocked.BlockReason = "缺少零件";
            ResidentActionCandidate costly = Candidate("costly", "haul", "无收益搬运", 0.1f);
            costly.TravelCost = 0.2f;

            ResidentDecisionResult result = _engine.Decide(Context(
                1, 0, Needs(0f, 0f, 0f), blocked, costly));

            Assert.IsFalse(result.HasSelection);
            Assert.AreEqual("缺少零件", Trace(result, "blocked").Reason);
            Assert.AreEqual(CandidateDecisionState.OutsideShortlist, Trace(result, "costly").State);
        }

        private static ResidentActionCandidate Candidate(string id, string intent, string displayName, float baseUtility)
            => new(id, intent, displayName) { BaseUtility = baseUtility };

        private static ResidentDecisionContext Context(
            int worldSeed,
            long sequence,
            IReadOnlyList<ResidentNeedState> needs,
            params ResidentActionCandidate[] candidates)
            => new ResidentDecisionContext(worldSeed, 0xC0FFEEUL, sequence, needs, candidates);

        private static ResidentNeedState[] Needs(float thirst, float hunger, float fatigue, float health = 0.05f)
            => new[]
            {
                new ResidentNeedState(ResidentNeed.Thirst, thirst, 0.002f, 1.2f),
                new ResidentNeedState(ResidentNeed.Hunger, hunger, 0.0012f, 1f),
                new ResidentNeedState(ResidentNeed.Fatigue, fatigue, 0.0008f, 0.9f),
                new ResidentNeedState(ResidentNeed.Health, health, 0f, 1.4f),
            };

        private static CandidateDecisionTrace Trace(ResidentDecisionResult result, string candidateId)
        {
            for (int i = 0; i < result.Traces.Count; i++)
            {
                CandidateDecisionTrace trace = result.Traces[i];
                if (trace.Candidate.Id == candidateId) return trace;
            }
            Assert.Fail($"未找到候选轨迹：{candidateId}");
            return null;
        }
    }
}
