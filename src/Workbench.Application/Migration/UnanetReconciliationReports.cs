using Workbench.Contracts.Migration;

namespace Workbench.Application.Migration;

/// <summary>
/// The Unanet reports compared during a parallel month: time or project cost for a closed month, by project and person.
/// They run through the same engine, mappings, sign-off and export as the Costpoint reconciliations; the difference is
/// that each differing row also gets a likely cause in plain words (<see cref="UnanetReconcileCauses"/>).
/// </summary>
public static class UnanetReconciliationReports
{
    private const string Sample = "samples/reconcile/unanet/";
    private static readonly string[] ProjectAliases = ["Project Code", "Project", "Project ID", "Project Number", "Project #"];

    private static ReconFieldDto Field(string name, string label, ReconFieldRole role, bool required, string description, ReconValueFormat format, params string[] aliases) =>
        new(name, label, role, required, description, [name, label, .. aliases], format);

    public static IReadOnlyList<ReconReportDto> All { get; } =
    [
        new(ReconReportKind.UnanetTimesheets, "Unanet timesheet totals by person and project",
            "Approved hours (and labor cost) per person and project for a closed month, from a Unanet time report. Compared by project, however the tasks are nested.",
            "the time report for the month, grouped by person and project (hours and, if it has them, cost)", [
                Field("Employee", "Employee", ReconFieldRole.Key, true, "Employee id (the same one used when people were imported).", ReconValueFormat.Money,
                    "Employee ID", "Employee Number", "Person ID", "Username", "User Name", "Person ID #"),
                Field("Project", "Project", ReconFieldRole.Key, true, "Unanet project code. Task columns are not needed: time is compared by project.", ReconValueFormat.Money, ProjectAliases),
                Field("Name", "Person", ReconFieldRole.Label, false, "Shown next to the key; not compared.", ReconValueFormat.Money, "Person", "Person Name", "Employee Name", "Name", "Full Name"),
                Field("Hours", "Hours", ReconFieldRole.Measure, true, "", ReconValueFormat.Hours, "Total Hours", "Approved Hours", "Hours Worked", "Time", "Hrs"),
                Field("LaborCost", "Labor cost", ReconFieldRole.Measure, false, "Hours at the cost rate in force on each day. Leave unmapped if the report has no cost.", ReconValueFormat.Money,
                    "Labor Cost", "Cost", "Labor Amount", "Total Cost", "Cost Amount"),
            ], Sample + "timesheet-totals.csv", UsesRange: true, UsesAsOf: false, ReconRange.Period, "Unanet"),

        new(ReconReportKind.UnanetProjectCost, "Unanet project cost by project",
            "Approved hours and labor cost per project for a closed month, from a Unanet project cost or project status report.",
            "the project cost report for the month, one row per project", [
                Field("Project", "Project", ReconFieldRole.Key, true, "Unanet project code.", ReconValueFormat.Money, ProjectAliases),
                Field("Name", "Project name", ReconFieldRole.Label, false, "Shown next to the key; not compared.", ReconValueFormat.Money, "Project Name", "Project Title", "Name", "Description"),
                Field("Hours", "Hours", ReconFieldRole.Measure, false, "", ReconValueFormat.Hours, "Total Hours", "Actual Hours", "Labor Hours", "Hours Worked", "Hrs"),
                Field("LaborCost", "Labor cost", ReconFieldRole.Measure, false, "Hours at the cost rate in force on each day.", ReconValueFormat.Money,
                    "Labor Cost", "Actual Labor Cost", "Labor Amount", "Cost", "Total Labor"),
            ], Sample + "project-cost.csv", UsesRange: true, UsesAsOf: false, ReconRange.Period, "Unanet"),
    ];
}
