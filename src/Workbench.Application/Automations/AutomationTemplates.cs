using Workbench.Contracts.Automations;

namespace Workbench.Application.Automations;

/// <summary>Ready-made automations for common GovCon cases. Choosing one opens the builder pre-filled; nothing is saved until you save.</summary>
public static class AutomationTemplates
{
    public const string FundingLow = "funding-low";
    public const string ReceiptRequired = "receipt-required";
    public const string TimesheetEscalation = "timesheet-escalation";

    private static AutomationRecipient Rel(string name) => new(RecipientKind.Relationship, name);

    private static AutomationRecipient Role(string name) => new(RecipientKind.Role, name);

    public static IReadOnlyList<AutomationTemplateDto> All { get; } =
    [
        new(FundingLow, "Funding running low: tell the PM", "When a contract's remaining funding drops under 25%, notify its project managers (Limitation of Funds / Limitation of Cost).", "Contracts",
            new SaveAutomationRequest("Notify the PM when funding drops under 25%",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "contract", Threshold: ThresholdKind.ContractFundingBelow, Percent: 25m),
                    null,
                    [
                        new(AutomationActionKind.Notify, [Rel(AutomationRelationships.ProjectManager), Role("Finance")],
                            "Funding under 25% on {contractNumber}",
                            "{contractNumber} {name} has {fundingRemainingPercent}% of its funding left ({fundingRemaining} remaining). Consider a Limitation of Funds notice to the contracting officer."),
                    ]),
                "Watches every open contract hourly and notifies once each time funding crosses under 25%.", TemplateKey: FundingLow)),

        new(ReceiptRequired, "Receipts required over $75", "Refuse to submit an expense report while a line over $75 has no receipt (FTR / DCAA documentation).", "Expenses",
            new SaveAutomationRequest("Require a receipt on expenses over $75",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
                    new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.GreaterThan, "maxLineWithoutReceipt", "75")]),
                    [new(AutomationActionKind.Block, Message: "Expense over $75 needs a receipt: attach one to each line of $75 or more before submitting.")]),
                "Validation rule: checked when an expense report is submitted.", TemplateKey: ReceiptRequired)),

        new(TimesheetEscalation, "Escalate timesheet approvals", "When a timesheet has waited 2 business days for approval, remind the approver and tell their supervisor.", "Time",
            new SaveAutomationRequest("Escalate timesheet approvals after 2 days",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "timesheet", Threshold: ThresholdKind.ApprovalWaiting, Days: 2),
                    null,
                    [
                        new(AutomationActionKind.Notify, [Rel(AutomationRelationships.Approvers), Rel(AutomationRelationships.ApproversSupervisors)],
                            "Escalated: a timesheet has waited 2 days for approval",
                            "The timesheet {record} is still waiting for approval. Approve or return it so payroll and billing aren't held up."),
                    ]),
                "Checks pending timesheet approvals and escalates each one once.", TemplateKey: TimesheetEscalation)),

        new("timesheet-missing", "Missing timesheet reminder", "The day after a timesheet period's deadline, remind the employee and their supervisor.", "Time",
            new SaveAutomationRequest("Remind about missing timesheets",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "employee", Threshold: ThresholdKind.TimesheetMissing, Days: 1),
                    null,
                    [new(AutomationActionKind.Notify, [Rel(AutomationRelationships.Employee), Rel(AutomationRelationships.Supervisor)], "Timesheet missing: {displayName}",
                        "{displayName} hasn't submitted the timesheet for the period that just ended.")]),
                TemplateKey: "timesheet-missing")),

        new("budget-over", "Project over budget", "When a project's actual cost passes 90% of its current budget, notify the PM and Finance.", "Projects",
            new SaveAutomationRequest("Warn when a project passes 90% of budget",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "project", Threshold: ThresholdKind.ProjectBudgetOver, Percent: 90m),
                    null,
                    [new(AutomationActionKind.Notify, [Rel(AutomationRelationships.ProjectManager), Role("Finance")], "{code} has used {budgetUsedPercent}% of its budget",
                        "Actual cost {actualCost} against a budget of {budget}. Review the estimate at completion.")]),
                TemplateKey: "budget-over")),

        new("period-end-task", "Contract ending: plan closeout", "90 days before a contract's period of performance ends, give its PMs a closeout to-do.", "Contracts",
            new SaveAutomationRequest("Plan closeout 90 days before a contract ends",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.DateField, "contract", FieldKey: "periodEnd", OffsetDays: -90, Hour: 7),
                    null,
                    [new(AutomationActionKind.CreateTask, [Rel(AutomationRelationships.ProjectManager)], "Plan closeout of {contractNumber}",
                        "The period of performance ends {periodEnd}. Start the option or follow-on decision and the closeout checklist.", DueInDays: 30)]),
                TemplateKey: "period-end-task")),

        new("large-requisition", "Extra approval for large requisitions", "Route requisitions over $25,000 through a record approval workflow (choose one after creating it in Setup › Approval workflows).", "Purchasing",
            new SaveAutomationRequest("Extra approval for requisitions over $25,000",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "requisition", [RecordEventKind.Submitted]),
                    new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.GreaterThan, "total", "25000")]),
                    [new(AutomationActionKind.RequireApproval)]),
                TemplateKey: "large-requisition")),

        new("invoice-to-teams", "Post approved invoices to a channel", "When an invoice is approved, post it to a Teams or Slack channel (choose the connector).", "Billing",
            new SaveAutomationRequest("Post approved invoices to the billing channel",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.IntegrationEvent, EventKey: "invoice.approved"),
                    null,
                    [new(AutomationActionKind.PostToChat, Title: "Invoice approved: {number}", Message: "Total {total}, due {dueDate}.")]),
                TemplateKey: "invoice-to-teams")),

        new("capture-stage", "Tell the capture manager about stage changes", "When an opportunity moves stage, notify its capture and proposal managers.", "Business development",
            new SaveAutomationRequest("Notify capture managers of stage changes",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.FieldChanged, "opportunity", FieldKey: "stage"),
                    null,
                    [new(AutomationActionKind.Notify, [new(RecipientKind.Field, "captureManagerEmail"), new(RecipientKind.Field, "proposalManagerEmail")],
                        "{number} moved to {stage}", "{name} is now in {stage}.")]),
                TemplateKey: "capture-stage")),
    ];

    public static AutomationTemplateDto? Find(string key) => All.FirstOrDefault(t => t.Key == key);
}
