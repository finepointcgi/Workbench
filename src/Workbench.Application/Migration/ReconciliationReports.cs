using Workbench.Contracts.Migration;

namespace Workbench.Application.Migration;

/// <summary>
/// The Costpoint reports Workbench reconciles against during the parallel month: their key, label and amount fields,
/// the header spellings matched automatically, and the default tolerances (to the penny; rates to 0.0001).
/// </summary>
public static class ReconciliationReports
{
    public const string Sample = "samples/reconcile/";

    private static ReconFieldDto Key(string name, string label, string description, params string[] aliases) =>
        new(name, label, ReconFieldRole.Key, true, description, [name, label, .. aliases]);

    private static ReconFieldDto Label(string name, string label, params string[] aliases) =>
        new(name, label, ReconFieldRole.Label, false, "Shown next to the key; not compared.", [name, label, .. aliases]);

    private static ReconFieldDto Money(string name, string label, bool required, string description, params string[] aliases) =>
        new(name, label, ReconFieldRole.Measure, required, description, [name, label, .. aliases]);

    private static ReconFieldDto Hours(string name, string label, bool required, string description, params string[] aliases) =>
        new(name, label, ReconFieldRole.Measure, required, description, [name, label, .. aliases], ReconValueFormat.Hours);

    private static ReconFieldDto Rate(string name, string label, string description, params string[] aliases) =>
        new(name, label, ReconFieldRole.Measure, false, description, [name, label, .. aliases], ReconValueFormat.Rate, 0.0001m);

    private static readonly string[] AccountAliases = ["ACCT_ID", "Account", "Account ID", "Account Number", "Acct", "Acct ID", "Account No"];
    private static readonly string[] ProjectAliases = ["PROJ_ID", "Project", "Project ID", "Project Code", "Project Number", "WBS", "Project/WBS"];

    private static IReadOnlyList<ReconReportDto>? _all;

    public static IReadOnlyList<ReconReportDto> All => _all ??= [.. Costpoint, .. UnanetReconciliationReports.All];

    private static IReadOnlyList<ReconReportDto> Costpoint { get; } =
    [
        new(ReconReportKind.TrialBalance, "Trial balance",
            "Ending balance by account for the period (debit positive), optionally its opening balance and period activity.",
            "Trial Balance report (General Ledger › Reports), by account", [
                Key("Account", "Account", "Account number. Summary accounts are compared when the report lists them.", AccountAliases),
                Label("Name", "Account name", "ACCT_NAME", "Account Name", "Account Description", "Description"),
                Money("Ending", "Ending balance", true, "Signed debit-positive, or map debit and credit balance columns (Column − Minus column).",
                    "END_BAL", "Ending Balance", "Balance", "Net Balance", "YTD Balance", "Debit Balance", "Ending Debit"),
                Money("Opening", "Opening balance", false, "Balance before the period.", "BEG_BAL", "Beginning Balance", "Opening Balance", "Begin Balance"),
                Money("PeriodDebit", "Period debits", false, "", "PER_DR", "Period Debits", "Debits", "Debit Activity", "Period Debit"),
                Money("PeriodCredit", "Period credits", false, "", "PER_CR", "Period Credits", "Credits", "Credit Activity", "Period Credit"),
            ], Sample + "trial-balance.csv", UsesRange: false, UsesAsOf: false, ReconRange.Period),

        new(ReconReportKind.ProjectCost, "Project cost by element",
            "Direct labor, fringe, overhead, G&A and ODCs by project (or WBS), burdened at provisional rates, unallowable excluded.",
            "Project Status Report / Project Cost by Element (Projects › Reports)", [
                Key("Project", "Project", "Project id (or WBS id when comparing WBS elements).", ProjectAliases),
                Label("Name", "Project name", "PROJ_NAME", "Project Name", "Description"),
                Money("DirectLabor", "Direct labor", false, "", "DIRECT_LABOR", "Direct Labor", "Labor", "DL", "Direct Labor Cost"),
                Money("Fringe", "Fringe", false, "", "FRINGE", "Fringe", "Fringe Benefits", "Fringe Burden"),
                Money("Overhead", "Overhead", false, "", "OVERHEAD", "Overhead", "OH", "Overhead Burden"),
                Money("GeneralAndAdministrative", "G&A", false, "", "GA", "G&A", "G and A", "General and Administrative", "G&A Burden"),
                Money("DirectOdc", "ODCs", false, "", "ODC", "ODCs", "Other Direct Costs", "Non-Labor", "Direct ODC", "Non Labor"),
                Money("Total", "Total cost", false, "", "TOTAL_COST", "Total Cost", "Total", "Total Burdened Cost"),
            ], Sample + "project-cost.csv", UsesRange: true, UsesAsOf: false, ReconRange.Period),

        new(ReconReportKind.LaborHours, "Labor distribution",
            "Approved hours (and labor cost at cost rates) by employee and project.",
            "Labor Distribution report (Labor › Reports), by employee and project", [
                Key("Employee", "Employee", "Employee id.", "EMPL_ID", "Employee ID", "Emp ID", "Employee Number", "Empl Id"),
                Key("Project", "Project", "Project id (or WBS id when comparing WBS elements).", ProjectAliases),
                Label("Name", "Employee name", "EMPL_NAME", "Employee Name", "LAST_FIRST_NAME", "Name"),
                Hours("Hours", "Hours", true, "", "HOURS", "ENTERED_HRS", "Hours", "Total Hours", "Labor Hours", "Hrs"),
                Money("LaborCost", "Labor cost", false, "Hours at the cost rate in force on each day.", "LAB_AMT", "Labor Amount", "Labor Cost", "Amount", "Cost"),
            ], Sample + "labor-hours.csv", UsesRange: true, UsesAsOf: false, ReconRange.Period),

        new(ReconReportKind.ArAging, "AR aging",
            "Open receivables by customer and days past due, as of the period end.",
            "Accounts Receivable Aging report (Accounts Receivable › Reports), by customer", [
                Key("Customer", "Customer", "Customer id.", "CUST_ID", "Customer ID", "Customer Code", "Customer"),
                Label("Name", "Customer name", "CUST_NAME", "Customer Name", "Name"),
                Money("Current", "Current", false, "Not yet due.", "CURRENT", "Current", "Not Due"),
                Money("Days1To30", "1–30 days", false, "", "1-30", "1 - 30", "1 to 30", "0-30"),
                Money("Days31To60", "31–60 days", false, "", "31-60", "31 - 60", "31 to 60", "Over 30"),
                Money("Days61To90", "61–90 days", false, "", "61-90", "61 - 90", "61 to 90", "Over 60"),
                Money("Over90", "Over 90 days", false, "", "OVER_90", "Over 90", "91+", "90+", "> 90"),
                Money("Total", "Total open", true, "", "TOTAL", "Total", "Total Open", "Balance", "Open Balance", "Total Due"),
            ], Sample + "ar-aging.csv", UsesRange: false, UsesAsOf: true, ReconRange.Period),

        new(ReconReportKind.ApAging, "AP aging",
            "Open payables (excluding retainage) by vendor and days past due, as of the period end, plus retainage held.",
            "Accounts Payable Aging report (Accounts Payable › Reports), by vendor", [
                Key("Vendor", "Vendor", "Vendor id.", "VEND_ID", "Vendor ID", "Vendor Code", "Vendor"),
                Label("Name", "Vendor name", "VEND_NAME", "Vendor Name", "Name"),
                Money("Current", "Current", false, "Not yet due.", "CURRENT", "Current", "Not Due"),
                Money("Days1To30", "1–30 days", false, "", "1-30", "1 - 30", "1 to 30", "0-30"),
                Money("Days31To60", "31–60 days", false, "", "31-60", "31 - 60", "31 to 60"),
                Money("Days61To90", "61–90 days", false, "", "61-90", "61 - 90", "61 to 90"),
                Money("Over90", "Over 90 days", false, "", "OVER_90", "Over 90", "91+", "90+", "> 90"),
                Money("Total", "Total open", true, "Excluding retainage.", "TOTAL", "Total", "Total Open", "Balance", "Open Balance", "Total Due"),
                Money("Retainage", "Retainage", false, "", "RETAINAGE", "Retainage", "Retention", "Retainage Held"),
            ], Sample + "ap-aging.csv", UsesRange: false, UsesAsOf: true, ReconRange.Period),

        new(ReconReportKind.RevenueByProject, "Revenue by project",
            "Revenue posted to the GL by project (credits less debits on revenue accounts).",
            "Project Revenue / Revenue Summary report (Projects › Revenue), by project", [
                Key("Project", "Project", "Project id (or WBS id when comparing WBS elements).", ProjectAliases),
                Label("Name", "Project name", "PROJ_NAME", "Project Name", "Description"),
                Money("Revenue", "Revenue", true, "", "REV_AMT", "Revenue", "Revenue Amount", "Total Revenue", "Period Revenue", "Amount"),
            ], Sample + "revenue-by-project.csv", UsesRange: true, UsesAsOf: false, ReconRange.Period),

        new(ReconReportKind.IndirectRates, "Indirect rate calculations",
            "Pool cost, allocation base and actual rate per pool (fringe, overhead, G&A) from GL balances, year to date by default.",
            "Pool Rate / Allocation report (Indirect Cost › Reports), year to date", [
                Key("Pool", "Pool", "Fringe, Overhead or G&A (Costpoint pool names are translated with key rewrites).", "POOL", "Pool", "Pool Name", "POOL_NO", "Pool Description"),
                Money("PoolCost", "Pool cost", false, "", "POOL_COST", "Pool Cost", "Pool Amount", "Pool Costs", "Numerator"),
                Money("Base", "Allocation base", false, "", "ALLOC_BASE", "Allocation Base", "Base", "Base Amount", "Denominator"),
                Rate("Rate", "Rate", "As a fraction (0.3215). For a percentage column (32.15%) set the scale to 0.01.", "RATE", "Rate", "Actual Rate", "Rate %", "Pool Rate", "YTD Rate"),
            ], Sample + "indirect-rates.csv", UsesRange: true, UsesAsOf: false, ReconRange.YearToDate),
    ];

    public static ReconReportDto Get(ReconReportKind kind) => All.First(r => r.Kind == kind);

    /// <summary>Key rewrites every new mapping of a report starts with.</summary>
    public static IReadOnlyDictionary<string, string> DefaultRewrites(ReconReportKind kind) => kind == ReconReportKind.IndirectRates
        ? new Dictionary<string, string>
        {
            ["FRINGE BENEFITS"] = "Fringe",
            ["FRINGE POOL"] = "Fringe",
            ["OH"] = "Overhead",
            ["OVERHEAD POOL"] = "Overhead",
            ["GA"] = "G&A",
            ["G & A"] = "G&A",
            ["GENERAL AND ADMINISTRATIVE"] = "G&A",
            ["GENERAL & ADMINISTRATIVE"] = "G&A",
        }
        : new Dictionary<string, string>();

    public static IReadOnlyList<string> DefaultSkipPrefixes { get; } = ["Total", "Grand Total", "Report Total", "Subtotal"];
}
