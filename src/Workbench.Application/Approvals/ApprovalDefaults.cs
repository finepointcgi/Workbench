using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Contracts.Approvals;

namespace Workbench.Application.Approvals;

/// <summary>
/// The built-in workflows: exactly the fixed rules each module enforced before workflows were configurable. A document
/// type without a stored workflow gets its default (version 1) the first time it's used or shown.
/// </summary>
public static class ApprovalDefaults
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Name(string documentType) => $"{ApprovalDocumentTypes.Label(documentType)} approval";

    public static ApprovalWorkflowDefinition For(string documentType) => documentType switch
    {
        // DCAA: the supervisor approves; employees without one go to the administrators. Nobody approves their own.
        // Leave requests always followed the timesheet rule.
        ApprovalDocumentTypes.Timesheet or ApprovalDocumentTypes.LeaveRequest => new([
            new("Supervisor", 1, new(ApproverRuleKind.Supervisor), Fallback: new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Admin),
                WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
        ]),
        ApprovalDocumentTypes.ExpenseReport => new([
            new("Supervisor", 1, new(ApproverRuleKind.Supervisor), WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
        ]),

        // A vendor ODC goes to the project manager, or to their supervisor when the PM entered it.
        ApprovalDocumentTypes.VendorOdc => new([
            new("Project manager", 1, new(ApproverRuleKind.ProjectManager), WhenSubmitterIsApprover: SelfApprovalPolicy.SubmitterSupervisor),
        ]),

        // The managers of the charged projects (never the requester), then Finance over the purchasing threshold, for
        // account lines, or for projects without another manager. Administrators may decide for a PM.
        ApprovalDocumentTypes.Requisition => new([
            new("Project managers", 1, new(ApproverRuleKind.ProjectManager), WhenSubmitterIsApprover: SelfApprovalPolicy.Skip, AdminsMayAct: true),
            new("Finance", 2, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance),
                Conditions: [new(ApprovalConditionKind.AmountOverPolicyThreshold), new(ApprovalConditionKind.HasUnroutedCharges)],
                Match: ConditionMatch.Any, WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
        ]),

        // Project and subcontract lines to their project manager, account lines (and lines no other PM can take) to
        // Finance, in parallel. Administrators may decide any line.
        ApprovalDocumentTypes.ApBill => new([
            new("Project managers", 1, new(ApproverRuleKind.ProjectManager), WhenSubmitterIsApprover: SelfApprovalPolicy.Skip, AdminsMayAct: true),
            new("Finance", 1, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance),
                Conditions: [new(ApprovalConditionKind.HasUnroutedCharges)], WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned, AdminsMayAct: true),
        ]),

        // Finance approves invoices and posts manual journal entries, as before (no separate reviewer).
        ApprovalDocumentTypes.Invoice or ApprovalDocumentTypes.JournalEntry => new([
            new("Finance", 1, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance), WhenSubmitterIsApprover: SelfApprovalPolicy.Allow),
        ]),

        // HR: an employee's change to their own record goes to HR (administrators are members of every HR step).
        ApprovalDocumentTypes.HrChange => new([
            new("HR", 1, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Hr), WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned, AdminsMayAct: true),
        ]),

        // Pay changes: HR, then Finance when the annualized increase is over Hr:CompensationFinanceThreshold.
        ApprovalDocumentTypes.Compensation => new([
            new("HR", 1, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Hr), WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned, AdminsMayAct: true),
            new("Finance", 2, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance),
                Conditions: [new(ApprovalConditionKind.AmountOverPolicyThreshold)], WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
        ]),

        _ => throw new ArgumentException($"Unknown document type \"{documentType}\"."),
    };

    public static string Serialize(ApprovalWorkflowDefinition definition) => JsonSerializer.Serialize(definition, Json);

    public static ApprovalWorkflowDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<ApprovalWorkflowDefinition>(json, Json) ?? new ApprovalWorkflowDefinition([]);
}
