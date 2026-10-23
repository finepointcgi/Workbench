using System.Globalization;
using Workbench.Contracts.Automations;
using Workbench.Domain;
using Kind = Workbench.Contracts.Automations.AutomationRecipeBlankKind;

namespace Workbench.Application.Automations;

/// <summary>
/// The recipe gallery: plain sentences with blanks ("When {Status} changes to {Done}, notify {Owner}"), for sheets and for ERP
/// records (timesheets, expenses, bills, contracts, projects). Choosing one opens the builder pre-filled; nothing is saved until
/// you save. <see cref="Build"/> fills the blanks in; blanks left empty use their defaults (for columns, a column of that name).
/// </summary>
public static class AutomationTemplates
{
    public const string FundingLow = "funding-low";
    public const string ReceiptRequired = "receipt-required";
    public const string TimesheetEscalation = "timesheet-escalation";

    private static AutomationRecipient Rel(string name) => new(RecipientKind.Relationship, name);

    private static AutomationRecipient Role(string name) => new(RecipientKind.Role, name);

    private static AutomationRecipeBlankDto Blank(string key, string label, Kind kind, string? fallback = null, string? dependsOn = null) => new(key, label, kind, fallback, dependsOn);

    private sealed record Recipe(string Key, string Name, string Description, string Category, AutomationRecipeScope Scope, string Sentence,
        IReadOnlyList<AutomationRecipeBlankDto> Blanks, Func<Fill, SaveAutomationRequest> Make);

    private static readonly IReadOnlyList<Recipe> Recipes =
    [
        // ---- Sheets ----
        new("sheet-status-notify", "Status changes: notify the owner", "When a row's status changes to a value you choose, notify the people in a Person column.", "Sheets",
            AutomationRecipeScope.Sheet, "When {status} changes to {value}, notify {owner}",
            [Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Done", "status"), Blank("owner", "Person column", Kind.PersonColumn, "Owner")],
            f => f.Sheet($"When {f.Label("status")} is {f["value"]}, notify {f.Label("owner")}",
                new(AutomationTriggerKind.FieldChanged, FieldKey: f.Column("status"), ToValue: f["value"]), null,
                [new(AutomationActionKind.Notify, [new(RecipientKind.Field, f.Column("owner") ?? "")], "{Title} is now {" + f.Label("status") + "}")])),

        new("sheet-date-status", "Date arrives: move to In progress", "When a row's date arrives, set its status (for example to In progress).", "Sheets",
            AutomationRecipeScope.Sheet, "When {date} arrives, set {status} to {value}",
            [Blank("date", "Date column", Kind.DateColumn, AutomationSheets.Start), Blank("status", "Column", Kind.Column, "Status"),
                Blank("value", "Value", Kind.ColumnValue, "In progress", "status")],
            f => f.Sheet($"Move to {f["value"]} when {f.Label("date")} arrives",
                new(AutomationTriggerKind.DateField, FieldKey: f.Column("date"), OffsetDays: 0), null,
                [new(AutomationActionKind.SetField, FieldKey: f.Column("status"), Value: f["value"])])),

        new("sheet-form-assign", "Form submissions: assign someone", "When a form adds a row, put a person in a Person column (they're notified).", "Sheets",
            AutomationRecipeScope.Sheet, "When an item is added by a form, assign {owner} to {person}",
            [Blank("owner", "Person column", Kind.PersonColumn, "Owner"), Blank("person", "Person", Kind.Person)],
            f => f.Sheet($"Assign form submissions to {f["person"]}",
                new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.AddedByForm]), null,
                [new(AutomationActionKind.AssignPerson, FieldKey: f.Column("owner"), Value: f["person"])])),

        new("sheet-added-notify", "New rows: tell someone", "When anyone adds a row, notify a person.", "Sheets",
            AutomationRecipeScope.Sheet, "When an item is added, notify {person}",
            [Blank("person", "Person", Kind.Person)],
            f => f.Sheet($"Tell {f["person"]} about new rows",
                new(AutomationTriggerKind.RecordEvent, Events: [RecordEventKind.Created]), null,
                [new(AutomationActionKind.Notify, [new(RecipientKind.Person, f["person"] ?? "")], "New: {Title}")])),

        new("sheet-status-move", "Done rows: move to an archive sheet", "When a row's status changes to a value, move it (and its sub-items) to another sheet.", "Sheets",
            AutomationRecipeScope.Sheet, "When {status} changes to {value}, move the item to {sheet}",
            [Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Done", "status"), Blank("sheet", "Sheet", Kind.Sheet)],
            f => f.Sheet($"Move {f["value"]} rows to another sheet",
                new(AutomationTriggerKind.FieldChanged, FieldKey: f.Column("status"), ToValue: f["value"]), null,
                [new(AutomationActionKind.MoveRow, TargetSheetId: f.Guid("sheet"))])),

        new("sheet-status-copy", "Copy rows to another sheet", "When a row's status changes to a value, copy it to another sheet (for example a portfolio roll-up).", "Sheets",
            AutomationRecipeScope.Sheet, "When {status} changes to {value}, copy the item to {sheet}",
            [Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Done", "status"), Blank("sheet", "Sheet", Kind.Sheet)],
            f => f.Sheet($"Copy {f["value"]} rows to another sheet",
                new(AutomationTriggerKind.FieldChanged, FieldKey: f.Column("status"), ToValue: f["value"]), null,
                [new(AutomationActionKind.CopyRow, TargetSheetId: f.Guid("sheet"))])),

        new("sheet-date-remind", "Due soon: remind the owner", "A number of days before a date, notify the people in a Person column, unless the row is done.", "Sheets",
            AutomationRecipeScope.Sheet, "{days} days before {date}, remind {owner} unless {status} is {value}",
            [Blank("days", "Days", Kind.Number, "2"), Blank("date", "Date column", Kind.DateColumn, AutomationSheets.Finish), Blank("owner", "Person column", Kind.PersonColumn, "Owner"),
                Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Done", "status")],
            f => f.Sheet($"Remind {f.Label("owner")} {f.Int("days", 2)} days before {f.Label("date")}",
                new(AutomationTriggerKind.DateField, FieldKey: f.Column("date"), OffsetDays: -f.Int("days", 2)),
                f.Column("status") is { } status ? new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.NotEquals, status, f["value"])]) : null,
                [new(AutomationActionKind.Notify, [new(RecipientKind.Field, f.Column("owner") ?? "")], "Due {" + f.Label("date") + "}: {Title}")])),

        new("sheet-weekly-remind", "Weekly nudge on open rows", "Every week, remind the owner of each row that isn't done.", "Sheets",
            AutomationRecipeScope.Sheet, "Every {day}, remind {owner} about rows whose {status} isn't {value}",
            [Blank("day", "Day", Kind.Text, nameof(DayOfWeek.Monday)), Blank("owner", "Person column", Kind.PersonColumn, "Owner"),
                Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Done", "status")],
            f => f.Sheet($"Weekly reminder for rows that aren't {f["value"]}",
                new(AutomationTriggerKind.Schedule, Frequency: ScheduleFrequency.Weekly, Hour: 8,
                    DayOfWeek: Enum.TryParse<DayOfWeek>(f["day"], true, out var day) ? day : DayOfWeek.Monday),
                f.Column("status") is { } status ? new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.NotEquals, status, f["value"])]) : null,
                [new(AutomationActionKind.Notify, [new(RecipientKind.Field, f.Column("owner") ?? "")], "Still open: {Title}")])),

        new("sheet-status-subitem", "Blocked rows: add a follow-up sub-item", "When a row's status changes to a value, add a sub-item under it.", "Sheets",
            AutomationRecipeScope.Sheet, "When {status} changes to {value}, add the sub-item {title}",
            [Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Blocked", "status"), Blank("title", "Sub-item title", Kind.Text, "Unblock {Title}")],
            f => f.Sheet($"Add a sub-item when a row is {f["value"]}",
                new(AutomationTriggerKind.FieldChanged, FieldKey: f.Column("status"), ToValue: f["value"]), null,
                [new(AutomationActionKind.CreateSubItem, Value: f["title"])])),

        new("sheet-status-email", "Email someone on a status change", "When a row's status changes to a value, email a person (whatever their notification settings).", "Sheets",
            AutomationRecipeScope.Sheet, "When {status} changes to {value}, email {person}",
            [Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Blocked", "status"), Blank("person", "Person", Kind.Person)],
            f => f.Sheet($"Email {f["person"]} when a row is {f["value"]}",
                new(AutomationTriggerKind.FieldChanged, FieldKey: f.Column("status"), ToValue: f["value"]), null,
                [new(AutomationActionKind.SendEmail, [new(RecipientKind.Person, f["person"] ?? "")], "{Title} is {" + f.Label("status") + "}")])),

        new("sheet-status-approval", "Request approval", "When a row's status changes to a value, send it through an approval workflow and write the decision back.", "Sheets",
            AutomationRecipeScope.Sheet, "When {status} changes to {value}, request approval through {workflow}",
            [Blank("status", "Column", Kind.Column, "Status"), Blank("value", "Value", Kind.ColumnValue, "Ready for review", "status"),
                Blank("workflow", "Approval workflow", Kind.ApprovalWorkflow)],
            f => f.Sheet($"Request approval when a row is {f["value"]}",
                new(AutomationTriggerKind.FieldChanged, FieldKey: f.Column("status"), ToValue: f["value"]), null,
                [new(AutomationActionKind.RequireApproval, WorkflowKey: f["workflow"], FieldKey: f.Column("status"))])),

        // ---- ERP records ----
        new(FundingLow, "Funding running low: tell the PM", "When a contract's remaining funding drops under a percentage, notify its project managers (Limitation of Funds / Limitation of Cost).",
            "Contracts", AutomationRecipeScope.Record, "When funding drops below {n}%, notify the PM",
            [Blank("n", "Percent", Kind.Number, "25")],
            f => new SaveAutomationRequest($"Notify the PM when funding drops under {f.Int("n", 25)}%",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "contract", Threshold: ThresholdKind.ContractFundingBelow, Percent: f.Int("n", 25)),
                    null,
                    [
                        new(AutomationActionKind.Notify, [Rel(AutomationRelationships.ProjectManager), Role("Finance")],
                            $"Funding under {f.Int("n", 25)}% on {{contractNumber}}",
                            "{contractNumber} {name} has {fundingRemainingPercent}% of its funding left ({fundingRemaining} remaining). Consider a Limitation of Funds notice to the contracting officer."),
                    ]),
                $"Watches every open contract and notifies once each time funding crosses under {f.Int("n", 25)}%.", TemplateKey: FundingLow)),

        new("timesheet-late", "Late timesheets: remind the supervisor", "When a timesheet is submitted more than a number of days after its period ended, tell the employee's supervisor.",
            "Time", AutomationRecipeScope.Record, "When a timesheet is submitted more than {n} days late, remind the supervisor",
            [Blank("n", "Days", Kind.Number, "1")],
            f => new SaveAutomationRequest($"Remind the supervisor about timesheets submitted {f.Int("n", 1)}+ days late",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "timesheet", [RecordEventKind.Submitted]),
                    new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.GreaterThan, "daysLate", f.Int("n", 1).ToString(CultureInfo.InvariantCulture))]),
                    [new(AutomationActionKind.Notify, [Rel(AutomationRelationships.Supervisor)], "Late timesheet: {record}",
                        "This timesheet was submitted {daysLate} days after its period ended. Late time is a DCAA finding; please follow up.")]),
                TemplateKey: "timesheet-late")),

        new("timesheet-missing", "Missing timesheet reminder", "A number of days after a timesheet period ends, remind the employee and their supervisor.", "Time",
            AutomationRecipeScope.Record, "When a timesheet is {n} days overdue, remind the employee and supervisor",
            [Blank("n", "Days", Kind.Number, "1")],
            f => new SaveAutomationRequest("Remind about missing timesheets",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "employee", Threshold: ThresholdKind.TimesheetMissing, Days: f.Int("n", 1)),
                    null,
                    [new(AutomationActionKind.Notify, [Rel(AutomationRelationships.Employee), Rel(AutomationRelationships.Supervisor)], "Timesheet missing: {displayName}",
                        "{displayName} hasn't submitted the timesheet for the period that just ended.")]),
                TemplateKey: "timesheet-missing")),

        new(TimesheetEscalation, "Escalate timesheet approvals", "When a timesheet has waited for approval, remind the approver and tell their supervisor.", "Time",
            AutomationRecipeScope.Record, "When a timesheet has waited {n} business days for approval, escalate it",
            [Blank("n", "Business days", Kind.Number, "2")],
            f => new SaveAutomationRequest($"Escalate timesheet approvals after {f.Int("n", 2)} days",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "timesheet", Threshold: ThresholdKind.ApprovalWaiting, Days: f.Int("n", 2)),
                    null,
                    [
                        new(AutomationActionKind.Notify, [Rel(AutomationRelationships.Approvers), Rel(AutomationRelationships.ApproversSupervisors)],
                            $"Escalated: a timesheet has waited {f.Int("n", 2)} days for approval",
                            "The timesheet {record} is still waiting for approval. Approve or return it so payroll and billing aren't held up."),
                    ]),
                "Checks pending timesheet approvals and escalates each one once.", TemplateKey: TimesheetEscalation)),

        new(ReceiptRequired, "Receipts required over a limit", "Refuse to submit an expense report while a line over the limit has no receipt (FTR / DCAA documentation).", "Expenses",
            AutomationRecipeScope.Record, "When an expense line over ${n} has no receipt, refuse the submission",
            [Blank("n", "Amount", Kind.Number, "75")],
            f => new SaveAutomationRequest($"Require a receipt on expenses over ${f.Int("n", 75)}",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
                    new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.GreaterThan, "maxLineWithoutReceipt", f.Int("n", 75).ToString(CultureInfo.InvariantCulture))]),
                    [new(AutomationActionKind.Block, Message: $"Expense over ${f.Int("n", 75)} needs a receipt: attach one to each line of ${f.Int("n", 75)} or more before submitting.")]),
                "Validation rule: checked when an expense report is submitted.", TemplateKey: ReceiptRequired)),

        new("expense-large", "Large expense reports: tell Finance", "When an expense report over an amount is submitted, notify Finance.", "Expenses",
            AutomationRecipeScope.Record, "When an expense report over ${n} is submitted, notify Finance",
            [Blank("n", "Amount", Kind.Number, "2500")],
            f => new SaveAutomationRequest($"Tell Finance about expense reports over ${f.Int("n", 2500)}",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
                    new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.GreaterThan, "total", f.Int("n", 2500).ToString(CultureInfo.InvariantCulture))]),
                    [new(AutomationActionKind.Notify, [Role("Finance")], "Large expense report: {record}", "Total {total}, submitted by {actor}.")]),
                TemplateKey: "expense-large")),

        new("bill-approved", "Approved bills: tell someone", "When an AP bill is approved, notify a person (for example the payables lead).", "Bills",
            AutomationRecipeScope.Record, "When a bill is approved, notify {person}",
            [Blank("person", "Person", Kind.Person)],
            f => new SaveAutomationRequest("Tell the payables lead about approved bills",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "ap-bill", [RecordEventKind.Approved]),
                    null,
                    [new(AutomationActionKind.Notify, [new(RecipientKind.Person, f["person"] ?? "")], "Bill approved: {number}", "{invoiceNumber} for {amount}, due {dueDate}.")]),
                TemplateKey: "bill-approved")),

        new("budget-over", "Project over budget", "When a project's actual cost passes a share of its current budget, notify the PM and Finance.", "Projects",
            AutomationRecipeScope.Record, "When a project passes {n}% of its budget, notify the PM and Finance",
            [Blank("n", "Percent", Kind.Number, "90")],
            f => new SaveAutomationRequest($"Warn when a project passes {f.Int("n", 90)}% of budget",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.Threshold, "project", Threshold: ThresholdKind.ProjectBudgetOver, Percent: f.Int("n", 90)),
                    null,
                    [new(AutomationActionKind.Notify, [Rel(AutomationRelationships.ProjectManager), Role("Finance")], "{code} has used {budgetUsedPercent}% of its budget",
                        "Actual cost {actualCost} against a budget of {budget}. Review the estimate at completion.")]),
                TemplateKey: "budget-over")),

        new("period-end-task", "Contract ending: plan closeout", "Before a contract's period of performance ends, give its PMs a closeout to-do.", "Contracts",
            AutomationRecipeScope.Record, "{n} days before a contract ends, give the PM a closeout to-do",
            [Blank("n", "Days", Kind.Number, "90")],
            f => new SaveAutomationRequest($"Plan closeout {f.Int("n", 90)} days before a contract ends",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.DateField, "contract", FieldKey: "periodEnd", OffsetDays: -f.Int("n", 90), Hour: 7),
                    null,
                    [new(AutomationActionKind.CreateTask, [Rel(AutomationRelationships.ProjectManager)], "Plan closeout of {contractNumber}",
                        "The period of performance ends {periodEnd}. Start the option or follow-on decision and the closeout checklist.", DueInDays: 30)]),
                TemplateKey: "period-end-task")),

        new("large-requisition", "Extra approval for large requisitions", "Route requisitions over an amount through a record approval workflow.", "Purchasing",
            AutomationRecipeScope.Record, "When a requisition over ${n} is submitted, require approval through {workflow}",
            [Blank("n", "Amount", Kind.Number, "25000"), Blank("workflow", "Approval workflow", Kind.ApprovalWorkflow)],
            f => new SaveAutomationRequest($"Extra approval for requisitions over ${f.Int("n", 25000):N0}",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.RecordEvent, "requisition", [RecordEventKind.Submitted]),
                    new AutomationConditionGroup(GroupLogic.All, [new(ConditionSubject.Field, ConditionOperator.GreaterThan, "total", f.Int("n", 25000).ToString(CultureInfo.InvariantCulture))]),
                    [new(AutomationActionKind.RequireApproval, WorkflowKey: f["workflow"])]),
                TemplateKey: "large-requisition")),

        new("invoice-to-teams", "Post approved invoices to a channel", "When an invoice is approved, post it to a Teams or Slack channel (choose the connector).", "Billing",
            AutomationRecipeScope.Record, "When an invoice is approved, post it to a channel",
            [],
            _ => new SaveAutomationRequest("Post approved invoices to the billing channel",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.IntegrationEvent, EventKey: "invoice.approved"),
                    null,
                    [new(AutomationActionKind.PostToChat, Title: "Invoice approved: {number}", Message: "Total {total}, due {dueDate}.")]),
                TemplateKey: "invoice-to-teams")),

        new("capture-stage", "Tell the capture manager about stage changes", "When an opportunity moves stage, notify its capture and proposal managers.", "Business development",
            AutomationRecipeScope.Record, "When an opportunity changes stage, notify its capture managers",
            [],
            _ => new SaveAutomationRequest("Notify capture managers of stage changes",
                new AutomationDefinition(
                    new AutomationTrigger(AutomationTriggerKind.FieldChanged, "opportunity", FieldKey: "stage"),
                    null,
                    [new(AutomationActionKind.Notify, [new(RecipientKind.Field, "captureManagerEmail"), new(RecipientKind.Field, "proposalManagerEmail")],
                        "{number} moved to {stage}", "{name} is now in {stage}.")]),
                TemplateKey: "capture-stage")),
    ];

    public static IReadOnlyList<AutomationTemplateDto> All { get; } = Recipes
        .Select(r => new AutomationTemplateDto(r.Key, r.Name, r.Description, r.Category,
            r.Make(new Fill(r, null, [], new Dictionary<string, string?>())) with { TemplateKey = r.Key }, r.Scope, r.Sentence, r.Blanks))
        .ToList();

    public static AutomationTemplateDto? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    /// <summary>The recipe's automation with its blanks filled in; for sheet recipes, on <paramref name="sheetId"/> with its <paramref name="columns"/>.</summary>
    public static SaveAutomationRequest Build(string key, Guid? sheetId, IReadOnlyList<Column> columns, IReadOnlyDictionary<string, string?> blanks)
    {
        var recipe = Recipes.FirstOrDefault(r => r.Key == key) ?? throw new Contracts.WorkbenchNotFoundException($"There's no recipe \"{key}\".");
        if (recipe.Scope == AutomationRecipeScope.Sheet && sheetId is null)
        {
            throw new ArgumentException("This recipe is for a sheet: open it from the sheet's Automate button.");
        }

        return recipe.Make(new Fill(recipe, recipe.Scope == AutomationRecipeScope.Sheet ? sheetId : null, columns, blanks)) with { TemplateKey = key };
    }

    /// <summary>A recipe's blanks as they're filled in: the value given, else the blank's default (for columns, the sheet's column of that name).</summary>
    private sealed class Fill(Recipe recipe, Guid? sheetId, IReadOnlyList<Column> columns, IReadOnlyDictionary<string, string?> blanks)
    {
        public string? this[string key] =>
            blanks.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : recipe.Blanks.FirstOrDefault(b => b.Key == key)?.Default;

        /// <summary>A column blank: the column key given (if the sheet has it), else the column named like the default, else the first of the right kind.</summary>
        public string? Column(string key)
        {
            var blank = recipe.Blanks.First(b => b.Key == key);
            bool Fits(Column c) => blank.Kind switch
            {
                Kind.PersonColumn => c.Type == ColumnType.Person,
                Kind.DateColumn => c.Type == ColumnType.Date,
                _ => c.Formula is null,
            };

            if (blanks.TryGetValue(key, out var given) && !string.IsNullOrWhiteSpace(given))
            {
                return AutomationSheets.IsBuiltInField(given) || columns.Any(c => c.Id.ToString() == given && Fits(c)) ? given : null;
            }

            if (AutomationSheets.IsBuiltInField(blank.Default))
            {
                return blank.Default;
            }

            var named = columns.FirstOrDefault(c => Fits(c) && c.Name.Equals(blank.Default, StringComparison.OrdinalIgnoreCase))
                ?? (blank.Kind == Kind.Column ? columns.FirstOrDefault(c => c.Type == ColumnType.Dropdown) : columns.FirstOrDefault(Fits));
            return named?.Id.ToString();
        }

        /// <summary>The chosen column's name, for the automation's name and messages.</summary>
        public string Label(string key) => Column(key) switch
        {
            AutomationSheets.Title => "Title",
            AutomationSheets.Start => "Start",
            AutomationSheets.Finish => "Finish",
            { } id => columns.FirstOrDefault(c => c.Id.ToString() == id)?.Name ?? recipe.Blanks.First(b => b.Key == key).Default ?? key,
            null => recipe.Blanks.First(b => b.Key == key).Default ?? key,
        };

        public int Int(string key, int fallback) => int.TryParse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Abs(n) : fallback;

        public Guid? Guid(string key) => System.Guid.TryParse(this[key], out var id) ? id : null;

        public SaveAutomationRequest Sheet(string name, AutomationTrigger trigger, AutomationConditionGroup? conditions, IReadOnlyList<AutomationAction> actions) =>
            new(name, new AutomationDefinition(trigger with { EntityType = AutomationSheets.EntityType, SheetId = sheetId }, conditions, actions), recipe.Description);
    }
}
