using System;
using System.Collections.Generic;

namespace Game.Framework.Demo.Core
{
    /// <summary>Host 可观察到的教学元素；只描述语义，不依赖最终 VisualElement 层级或 USS class。</summary>
    internal enum DemoTeachingElement
    {
        Positioning,
        Section,
        Note,
        SubNote,
        Tip,
        Step,
        Concept,
        Table,
        Action,
        Value,
        CodeReference,
        Unavailable,
    }

    /// <summary>
    /// 单次真实章节 Build 的语义轨迹。它是 Demo 教学内容与自动化之间的 Seam：
    /// Module 仍只调用 Host Interface 构建 UI，目录则从同一调用中得到可验证事实，无需猜测源码或 CSS。
    /// </summary>
    internal sealed class DemoTeachingTrace
    {
        private readonly List<DemoTeachingElement> _elements = new();

        internal IReadOnlyList<DemoTeachingElement> Elements => _elements;
        internal int PositioningCount { get; private set; }
        internal int SectionCount { get; private set; }
        internal int ExplanationCount { get; private set; }
        internal int StepCount { get; private set; }
        internal int StructuredExplanationCount { get; private set; }
        internal int ActionCount { get; private set; }
        internal int ValueCount { get; private set; }
        internal int CodeReferenceCount { get; private set; }
        internal bool IsUnavailable { get; private set; }
        internal string UnavailableReason { get; private set; }
        internal string UnavailableRecovery { get; private set; }
        internal string UnavailableContinuation { get; private set; }

        internal void Record(DemoTeachingElement element, CodeRef code = default)
        {
            _elements.Add(element);
            if (code.HasTarget) CodeReferenceCount++;

            switch (element)
            {
                case DemoTeachingElement.Positioning:
                    PositioningCount++;
                    SectionCount++;
                    break;
                case DemoTeachingElement.Section:
                    SectionCount++;
                    break;
                case DemoTeachingElement.Note:
                case DemoTeachingElement.SubNote:
                case DemoTeachingElement.Tip:
                    ExplanationCount++;
                    break;
                case DemoTeachingElement.Step:
                    ExplanationCount++;
                    StepCount++;
                    StructuredExplanationCount++;
                    break;
                case DemoTeachingElement.Concept:
                case DemoTeachingElement.Table:
                    ExplanationCount++;
                    StructuredExplanationCount++;
                    break;
                case DemoTeachingElement.Action:
                    ActionCount++;
                    break;
                case DemoTeachingElement.Value:
                    ValueCount++;
                    break;
                case DemoTeachingElement.CodeReference:
                    break;
                case DemoTeachingElement.Unavailable:
                    IsUnavailable = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(element), element, null);
            }
        }

        internal void RecordUnavailable(string reason, string recovery, string continuation, CodeRef setupCode)
        {
            UnavailableReason = reason;
            UnavailableRecovery = recovery;
            UnavailableContinuation = continuation;
            Record(DemoTeachingElement.Unavailable, setupCode);
        }
    }

    /// <summary>
    /// Demo 教学契约的唯一判定点。正常章节按教学形态检查；环境缺依赖时改查“原因—恢复—继续学习”降级闭环。
    /// </summary>
    internal static class DemoTeachingContract
    {
        internal static void Validate(IDemoModule module, DemoTeachingTrace trace)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (trace == null) throw new ArgumentNullException(nameof(trace));

            var problems = trace.IsUnavailable
                ? ValidateUnavailable(trace)
                : ValidateNormal(module, trace);
            if (problems.Count == 0) return;

            throw new InvalidOperationException(
                $"[DemoTeaching] 章节 '{module.Id}'（{module.TeachingKind}）教学契约无效：\n  · " +
                string.Join("\n  · ", problems));
        }

        private static List<string> ValidateUnavailable(DemoTeachingTrace trace)
        {
            var problems = new List<string>();
            if (trace.Elements.Count == 0 || trace.Elements[0] != DemoTeachingElement.Unavailable)
                problems.Add("降级页必须在输出普通教学内容前调用 AddUnavailable");
            if (trace.Elements.Count != 1)
                problems.Add("降级页只能调用一次 AddUnavailable，不要再混入正常章节教学元素");
            if (string.IsNullOrWhiteSpace(trace.UnavailableReason))
                problems.Add("缺少不可用原因");
            if (string.IsNullOrWhiteSpace(trace.UnavailableRecovery))
                problems.Add("缺少可执行的恢复方式");
            if (string.IsNullOrWhiteSpace(trace.UnavailableContinuation))
                problems.Add("缺少不阻断学习的继续入口");
            if (trace.CodeReferenceCount < 1)
                problems.Add("降级页至少需要一处接线源码引用");
            return problems;
        }

        private static List<string> ValidateNormal(IDemoModule module, DemoTeachingTrace trace)
        {
            var problems = new List<string>();
            if (trace.PositioningCount != 1)
                problems.Add($"必须且只能调用一次 AddPositioning，当前 {trace.PositioningCount} 次");
            if (trace.Elements.Count == 0 || trace.Elements[0] != DemoTeachingElement.Positioning)
                problems.Add("AddPositioning 必须是第一个教学元素");
            if (trace.SectionCount < 3)
                problems.Add($"至少需要定位、主体与边界/小结三个小节，当前 {trace.SectionCount} 个");
            if (trace.ExplanationCount < 2)
                problems.Add($"至少需要两处解释性内容，当前 {trace.ExplanationCount} 处");

            switch (module.TeachingKind)
            {
                case DemoTeachingKind.Capability:
                    if (trace.ActionCount < 1)
                        problems.Add("能力章至少需要一个可操作入口");
                    break;
                case DemoTeachingKind.Concept:
                    if (trace.StructuredExplanationCount < 2)
                        problems.Add("概念章至少需要两个步骤、概念条目或对比表");
                    if (trace.CodeReferenceCount < 1)
                        problems.Add("概念章至少需要一处真实源码引用");
                    break;
                case DemoTeachingKind.Workflow:
                    if (trace.StepCount < 2)
                        problems.Add("工作流章至少需要两个有序步骤");
                    if (trace.ActionCount < 1)
                        problems.Add("工作流章至少需要一个可执行入口");
                    break;
                default:
                    problems.Add($"未知教学形态：{module.TeachingKind}");
                    break;
            }

            return problems;
        }
    }
}
