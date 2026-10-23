using Workbench.Domain;
using Workbench.Domain.Sheets;

namespace Workbench.Application.Sheets;

/// <summary>
/// The templates every New sheet gallery offers. Date cells written <c>day:N</c> become the date N days after the sheet
/// is created, and row Start/Finish days count from that day too, so the example plan always starts today.
/// </summary>
public static class BuiltInSheetTemplates
{
    public const string Prefix = "builtin:";

    public sealed record BuiltIn(string Id, string Name, string Description, string Category, SheetTemplateContent Content);

    public static IReadOnlyList<BuiltIn> All { get; } =
    [
        new(Prefix + "project-schedule", "Project schedule",
            "Phases, tasks and milestones on a Gantt chart, with status, owner and percent complete.", "Projects", ProjectSchedule()),
        new(Prefix + "issue-tracker", "Issue tracker",
            "Log issues with priority, owner and due date, and see how long each has been open.", "Projects", IssueTracker()),
        new(Prefix + "raid-log", "RAID log",
            "Risks, assumptions, issues and dependencies with owners, a probability × impact score and mitigations.", "Projects", RaidLog()),
        new(Prefix + "contract-deliverables", "Contract deliverables",
            "CDRLs and other deliverables by contract, with due dates, submission status and what's late.", "Contracts", ContractDeliverables()),
        new(Prefix + "onboarding-checklist", "Onboarding checklist",
            "Everything a new hire needs, from offer letter to the 30-day check-in, with owners and due dates.", "People", Onboarding()),
    ];

    public static BuiltIn? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static TemplateColumn Column(string key, string name, ColumnType type, string[]? options = null, string? formula = null) =>
        new() { Key = key, Name = name, Type = type, Options = options?.ToList() ?? [], Formula = formula };

    private static TemplateRow Row(string key, string title, string? parent = null, int? start = null, int? finish = null, bool milestone = false,
        params (string Column, string? Value)[] values) =>
        new()
        {
            Key = key,
            ParentKey = parent,
            Title = title,
            StartDay = start,
            FinishDay = finish,
            IsMilestone = milestone,
            Values = values.ToDictionary(v => v.Column, v => v.Value),
        };

    private static SheetTemplateContent ProjectSchedule() => new()
    {
        SchedulingEnabled = true,
        Columns =
        [
            Column("status", "Status", ColumnType.Dropdown, ["Not started", "In progress", "Complete", "On hold"]),
            Column("owner", "Owner", ColumnType.Person),
            Column("percent", "% complete", ColumnType.Number),
            Column("late", "Behind schedule", ColumnType.Checkbox, formula: "AND(ISDATE([Finish]), [Finish] < TODAY(), [Status] <> \"Complete\")"),
            Column("notes", "Notes", ColumnType.Text),
        ],
        Rows =
        [
            Row("p1", "Planning"),
            Row("kickoff", "Kickoff meeting", "p1", 0, 0, milestone: true, ("status", "Not started")),
            Row("reqs", "Gather requirements", "p1", 1, 7, values: ("status", "Not started")),
            Row("p2", "Execution"),
            Row("design", "Design", "p2", 8, 17, values: ("status", "Not started")),
            Row("build", "Build", "p2", 18, 39, values: ("status", "Not started")),
            Row("test", "Test and fix", "p2", 40, 49, values: ("status", "Not started")),
            Row("golive", "Go live", null, 50, 50, milestone: true, ("status", "Not started")),
        ],
    };

    private static SheetTemplateContent IssueTracker() => new()
    {
        Columns =
        [
            Column("status", "Status", ColumnType.Dropdown, ["Open", "In progress", "Resolved", "Closed"]),
            Column("priority", "Priority", ColumnType.Dropdown, ["High", "Medium", "Low"]),
            Column("reported", "Reported on", ColumnType.Date),
            Column("reporter", "Reported by", ColumnType.Person),
            Column("owner", "Assigned to", ColumnType.Person),
            Column("due", "Due", ColumnType.Date),
            Column("age", "Days open", ColumnType.Number, formula: "IF(OR(ISBLANK([Reported on]), [Status] = \"Closed\"), \"\", TODAY() - [Reported on])"),
            Column("resolution", "Resolution", ColumnType.Text),
        ],
        Rows =
        [
            Row("i1", "Example: report totals don't match the ledger", values: [("status", "Open"), ("priority", "High"), ("reported", "day:0"), ("due", "day:3")]),
            Row("i2", "Example: add a field to the intake form", values: [("status", "In progress"), ("priority", "Low"), ("reported", "day:0"), ("due", "day:14")]),
        ],
    };

    private static SheetTemplateContent RaidLog() => new()
    {
        Columns =
        [
            Column("type", "Type", ColumnType.Dropdown, ["Risk", "Assumption", "Issue", "Dependency"]),
            Column("owner", "Owner", ColumnType.Person),
            Column("probability", "Probability", ColumnType.Dropdown, ["High", "Medium", "Low"]),
            Column("impact", "Impact", ColumnType.Dropdown, ["High", "Medium", "Low"]),
            Column("score", "Score", ColumnType.Number,
                formula: "IF([Probability] = \"High\", 3, IF([Probability] = \"Medium\", 2, IF([Probability] = \"Low\", 1, 0))) * "
                    + "IF([Impact] = \"High\", 3, IF([Impact] = \"Medium\", 2, IF([Impact] = \"Low\", 1, 0)))"),
            Column("response", "Mitigation or response", ColumnType.Text),
            Column("status", "Status", ColumnType.Dropdown, ["Open", "Monitoring", "Closed"]),
            Column("review", "Next review", ColumnType.Date),
        ],
        Rows =
        [
            Row("r1", "Example risk: key engineer leaves mid-project",
                values: [("type", "Risk"), ("probability", "Medium"), ("impact", "High"), ("response", "Cross-train a second engineer"), ("status", "Open"), ("review", "day:14")]),
            Row("r2", "Example assumption: government furnished equipment arrives by month two",
                values: [("type", "Assumption"), ("probability", "Low"), ("impact", "Medium"), ("status", "Monitoring"), ("review", "day:30")]),
            Row("r3", "Example issue: test environment unavailable",
                values: [("type", "Issue"), ("probability", "High"), ("impact", "Medium"), ("status", "Open"), ("review", "day:7")]),
            Row("r4", "Example dependency: security accreditation before go-live",
                values: [("type", "Dependency"), ("probability", "Medium"), ("impact", "High"), ("status", "Open"), ("review", "day:21")]),
        ],
    };

    private static SheetTemplateContent ContractDeliverables() => new()
    {
        Columns =
        [
            Column("number", "Deliverable no.", ColumnType.Text),
            Column("contract", "Contract", ColumnType.Text),
            Column("owner", "Owner", ColumnType.Person),
            Column("due", "Due", ColumnType.Date),
            Column("submitted", "Submitted", ColumnType.Date),
            Column("status", "Status", ColumnType.Dropdown, ["Not started", "In progress", "Submitted", "Accepted", "Rejected"]),
            Column("days", "Days until due", ColumnType.Number, formula: "IF(OR(ISBLANK([Due]), ISDATE([Submitted])), \"\", [Due] - TODAY())"),
            Column("late", "Late", ColumnType.Checkbox, formula: "AND(ISDATE([Due]), [Due] < TODAY(), ISBLANK([Submitted]))"),
        ],
        Rows =
        [
            Row("d1", "Monthly status report", values: [("number", "A001"), ("due", "day:30"), ("status", "Not started")]),
            Row("d2", "Program management plan", values: [("number", "A002"), ("due", "day:45"), ("status", "Not started")]),
            Row("d3", "Quality assurance plan", values: [("number", "A003"), ("due", "day:60"), ("status", "Not started")]),
        ],
    };

    private static SheetTemplateContent Onboarding() => new()
    {
        Columns =
        [
            Column("stage", "When", ColumnType.Dropdown, ["Before day one", "First day", "First week", "First month"]),
            Column("owner", "Owner", ColumnType.Person),
            Column("due", "Due", ColumnType.Date),
            Column("done", "Done", ColumnType.Checkbox),
            Column("notes", "Notes", ColumnType.Text),
        ],
        Rows =
        [
            Row("o1", "Send the offer letter and start paperwork", values: [("stage", "Before day one"), ("due", "day:0")]),
            Row("o2", "Order a laptop and badge", values: [("stage", "Before day one"), ("due", "day:3")]),
            Row("o3", "Create accounts (email, Workbench, timekeeping)", values: [("stage", "Before day one"), ("due", "day:5")]),
            Row("o4", "Security briefing and NDA", values: [("stage", "First day"), ("due", "day:7")]),
            Row("o5", "Timesheet and charge code walkthrough", values: [("stage", "First day"), ("due", "day:7")]),
            Row("o6", "Benefits enrollment", values: [("stage", "First week"), ("due", "day:14")]),
            Row("o7", "30-day check-in", values: [("stage", "First month"), ("due", "day:37")]),
        ],
    };
}
