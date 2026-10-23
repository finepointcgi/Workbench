using Workbench.Contracts.Approvals;

namespace Workbench.Ui.Setup.Approvals;

/// <summary>
/// The one-click "project managers approve time charged to their projects" option: a timesheet workflow step whose
/// approver is each charged project's manager, in parallel with the first step (by default, the supervisor's).
/// </summary>
public static class ProjectManagerTimeApproval
{
    public const string StepName = "Project managers";

    public static bool IsOn(ApprovalWorkflowDefinition definition) =>
        definition.Steps.Any(s => s.Approver.Kind == ApproverRuleKind.ProjectManager);

    public static ApprovalWorkflowDefinition With(ApprovalWorkflowDefinition definition, bool on)
    {
        if (!on)
        {
            return new ApprovalWorkflowDefinition(definition.Steps.Where(s => s.Approver.Kind != ApproverRuleKind.ProjectManager).ToList());
        }

        if (IsOn(definition))
        {
            return definition;
        }

        // Same order as the first step, so both review at once. Projects without a manager, and a manager's own time on
        // their project, skip this step (the supervisor still reviews the whole timesheet).
        var first = definition.Steps.Count == 0 ? 1 : definition.Steps.Min(s => s.Sequence);
        return new ApprovalWorkflowDefinition([.. definition.Steps,
            new ApprovalStepDefinition(StepName, first, new ApproverRule(ApproverRuleKind.ProjectManager), WhenSubmitterIsApprover: SelfApprovalPolicy.Skip)]);
    }
}
