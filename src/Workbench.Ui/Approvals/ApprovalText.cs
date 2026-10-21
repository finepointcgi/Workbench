using Workbench.Contracts.Approvals;

namespace Workbench.Ui.Approvals;

/// <summary>Plain-language descriptions of workflow rules for the setup screens.</summary>
public static class ApprovalText
{
    public static string Rule(ApproverRuleKind kind) => kind switch
    {
        ApproverRuleKind.Supervisor => "The submitter's supervisor",
        ApproverRuleKind.ProjectManager => "Each charged project's manager",
        ApproverRuleKind.OrgUnitManager => "Each org unit's approver",
        ApproverRuleKind.OrgRole => "Anyone with an org role",
        ApproverRuleKind.SpecificPeople => "Each of these people",
        _ => "Any one of these people",
    };

    public static string Self(SelfApprovalPolicy policy) => policy switch
    {
        SelfApprovalPolicy.Skip => "Skip the approval (also when nobody is found)",
        SelfApprovalPolicy.SubmitterSupervisor => "Send it to the submitter's supervisor",
        SelfApprovalPolicy.Unassigned => "Leave it waiting for an administrator",
        _ => "Let the submitter approve (no independent review)",
    };

    public static string Condition(ApprovalConditionKind kind) => kind switch
    {
        ApprovalConditionKind.AmountOver => "Total is over",
        ApprovalConditionKind.AmountAtMost => "Total is at most",
        ApprovalConditionKind.AmountOverPolicyThreshold => "Total is over the policy threshold",
        ApprovalConditionKind.ProjectIn => "Charges one of these projects",
        ApprovalConditionKind.ContractIn => "Charges one of these contracts",
        ApprovalConditionKind.OrgUnitIn => "Charges (or submitter) in one of these org units",
        ApprovalConditionKind.HasUnallowableCost => "Has an unallowable cost",
        ApprovalConditionKind.CostTypeIn => "Has one of these cost types",
        ApprovalConditionKind.HasAccountCharges => "Has GL-account charges",
        _ => "Has charges no other project manager can approve",
    };

    public static string Summary(ApprovalStepDefinition step)
    {
        var who = step.Approver.Kind switch
        {
            ApproverRuleKind.OrgRole => step.Approver.CustomRoleId is not null ? "anyone with the chosen custom role" : $"anyone in {step.Approver.Role}",
            ApproverRuleKind.SpecificPeople => $"each of {string.Join(", ", step.Approver.People ?? [])}",
            ApproverRuleKind.AnyOf => $"any of {string.Join(", ", step.Approver.People ?? [])}",
            _ => Rule(step.Approver.Kind).ToLowerInvariant(),
        };
        var when = step.Conditions is { Count: > 0 } c
            ? $" when {string.Join(step.Match == ConditionMatch.Any ? " or " : " and ", c.Select(x => Condition(x.Kind).ToLowerInvariant() + (x.Amount is { } a ? $" {a:C0}" : "")))}"
            : "";
        return $"{step.Sequence}. {step.Name}: {who}{when}";
    }
}
