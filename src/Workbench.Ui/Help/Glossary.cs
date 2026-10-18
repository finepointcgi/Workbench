namespace Workbench.Ui.Help;

/// <summary>One glossary entry: the short term shown on the page, its spelled-out name and a plain-English explanation.</summary>
public sealed record GlossaryEntry(string Key, string Term, string Name, string Explanation)
{
    /// <summary>What the tooltip says: the spelled-out name (when it differs from the term) and the explanation.</summary>
    public string Tooltip => string.Equals(Name, Term, StringComparison.OrdinalIgnoreCase) ? Explanation : $"{Name}: {Explanation}";
}

/// <summary>
/// Plain-English explanations of the government-contracting and accounting terms the pages use, for
/// <see cref="Term"/> (<c>&lt;Term Key="eac"&gt;EAC&lt;/Term&gt;</c>). Keys are lower-case; add new terms here.
/// </summary>
public static class Glossary
{
    private static readonly GlossaryEntry[] All =
    [
        // Costs and charging
        new("odc", "ODC", "Other direct cost", "A cost other than labor that's charged straight to a project, such as travel, materials or a vendor's invoice."),
        new("wbs", "WBS", "Work breakdown structure", "A project's outline of tasks. The lowest-level tasks are the charge codes you record time and costs against."),
        new("charge-code", "Charge code", "Charge code", "The project task you record your hours or costs against, for example \"Requirements review (1001.01.02)\"."),
        new("direct", "Direct", "Direct cost", "Work or spending for one specific project or contract, billed to that customer."),
        new("indirect", "Indirect", "Indirect cost", "Work or spending that supports the whole company (training, proposals, admin). It's spread over projects through overhead and G&A rates."),
        new("labor-category", "Labor category", "Labor category", "A job title used for pricing and billing, such as \"Senior Engineer\". Contracts set a bill rate for each one."),
        new("standard-hours", "Standard hours", "Standard hours", "The hours you're expected to record in a period (usually 40 a week), less holidays."),
        new("burdened", "Burdened", "Burdened cost", "The full cost: raw labor or expense plus fringe, overhead and G&A added on top."),
        new("burden", "Burden", "Burden", "The fringe, overhead and G&A costs added on top of raw labor or expense to get its full cost."),
        new("fringe", "Fringe", "Fringe", "The cost of employee benefits (paid leave, health insurance, payroll taxes), spread over labor as a percentage."),
        new("overhead", "Overhead", "Overhead", "The cost of supporting project work (facilities, supervision, equipment), spread over direct labor as a percentage."),
        new("ga", "G&A", "General and administrative", "The cost of running the company (executives, accounting, HR), spread over total project cost as a percentage."),
        new("indirect-rate", "Indirect rate", "Indirect rate", "A percentage (fringe, overhead or G&A) added to direct costs so each project carries its share of company costs."),
        new("provisional-rate", "Provisional rate", "Provisional billing rate", "An indirect rate used for billing during the year, before the actual rate is known. It's trued up to actuals afterwards."),
        new("unallowable", "Unallowable", "Unallowable cost", "A cost the government won't pay for under FAR 31.205 (alcohol, entertainment, some travel). It's recorded but kept out of bills and rates."),
        new("allowable", "Allowable", "Allowable cost", "A cost the government accepts as chargeable under FAR 31.2, so it can be billed and included in rates."),
        new("allocation", "Allocation", "Allocation", "Spreading an indirect cost pool over the projects that benefit from it, using the indirect rate."),

        // Budgets and earned value
        new("bac", "BAC", "Budget at completion", "The total approved budget for the work."),
        new("etc", "ETC", "Estimate to complete", "How much more the work is expected to cost from today until it's finished."),
        new("eac", "EAC", "Estimate at completion", "What the work is expected to cost in total when it's finished: spent so far plus the estimate to complete."),
        new("vac", "VAC", "Variance at completion", "Budget at completion minus estimate at completion. Negative means the work is expected to finish over budget."),
        new("ev", "EV", "Earned value", "The budgeted value of the work actually done so far."),
        new("pv", "PV", "Planned value", "The budgeted value of the work that was scheduled to be done by now."),
        new("ac", "AC", "Actual cost", "What the work done so far actually cost."),
        new("cv", "CV", "Cost variance", "Earned value minus actual cost. Negative means the work done cost more than planned."),
        new("sv", "SV", "Schedule variance", "Earned value minus planned value. Negative means less work is done than scheduled."),
        new("cpi", "CPI", "Cost performance index", "Earned value ÷ actual cost. 1.0 is on budget; below 1.0 means over budget."),
        new("spi", "SPI", "Schedule performance index", "Earned value ÷ planned value. 1.0 is on schedule; below 1.0 means behind schedule."),
        new("bcws", "BCWS", "Budgeted cost of work scheduled", "The planned value: the budget for the work that was scheduled to be done by now."),
        new("bcwp", "BCWP", "Budgeted cost of work performed", "The earned value: the budget for the work actually done so far."),
        new("acwp", "ACWP", "Actual cost of work performed", "What the work done so far actually cost."),
        new("pmb", "PMB", "Performance measurement baseline", "The time-phased budget that earned value is measured against."),
        new("mr", "MR", "Management reserve", "Budget held back for unplanned work within the project's scope, not yet assigned to any task."),
        new("var", "VAR", "Variance analysis report", "A required explanation of why a control account is over or under its cost or schedule thresholds, and what's being done about it."),
        new("tcpi", "TCPI", "To-complete performance index", "How efficient the remaining work must be to finish on budget. Above 1.0 means you have to do better than planned."),
        new("baseline", "Baseline", "Baseline", "The approved, locked plan (budget and schedule) that progress is measured against."),
        new("control-account", "Control account", "Control account", "A part of the project, usually one WBS branch, where budget, schedule and actual cost are managed and reported together."),
        new("work-package", "Work package", "Work package", "A lowest-level piece of work with its own budget and schedule; earned value is measured on it."),
        new("burn", "Burn", "Burn rate", "How fast the budget or funding is being spent."),

        // Contracts, funding and billing
        new("clin", "CLIN", "Contract line item number", "A separately priced line on a government contract. Each one is funded and billed on its own."),
        new("ceiling", "Ceiling", "Ceiling", "The most the customer will pay: the contract value or a cap on hours, cost or rates. Costs above it can't be billed."),
        new("funded", "Funded", "Funded amount", "Money the government has actually obligated so far. You can't bill past it, even if the contract value is higher."),
        new("funding", "Funding", "Funding", "Money the government has obligated to the contract so far. Work and billing must stay within it."),
        new("contract-value", "Contract value", "Contract value", "The total price of the contract if every option is exercised. Usually more than is funded today."),
        new("ffp", "FFP", "Firm fixed price", "A contract paid at a set price no matter what the work costs."),
        new("tm", "T&M", "Time and materials", "A contract billed by the hour at each labor category's rate, plus materials at cost."),
        new("cost-plus", "Cost-plus", "Cost-plus", "A contract that pays allowable costs plus a fee."),
        new("fee", "Fee", "Fee", "The profit on a cost-plus contract, on top of allowable cost."),
        new("fee-withhold", "Fee withhold", "Fee withhold", "Part of the fee the government holds back on cost-plus invoices until the contract is closed out."),
        new("retainage", "Retainage", "Retainage", "Part of each invoice the customer holds back until the work is accepted or the contract closes."),
        new("unbilled", "Unbilled", "Unbilled", "Work done and costs incurred that haven't been invoiced yet."),
        new("aging", "Aging", "Aging", "How long unpaid invoices have been outstanding, grouped by days past the invoice date."),
        new("wawf", "WAWF", "Wide Area Workflow", "The Defense Department's system for submitting invoices and receiving reports."),
        new("closeout", "Closeout", "Contract closeout", "Finishing a contract: final rates, final invoice, releasing withholds and closing the books."),

        // Revenue and accounting
        new("revenue-recognition", "Revenue recognition", "Revenue recognition", "Deciding how much of the contract's value has been earned this period, by the project's revenue method."),
        new("percent-complete", "Percent complete", "Percent complete", "Revenue earned in proportion to the share of the total estimated cost spent so far."),
        new("accrual", "Accrual", "Accrual", "Recording revenue or cost in the period it was earned or incurred, even if it hasn't been invoiced or paid yet."),
        new("incurred-cost", "Incurred cost submission", "Incurred cost submission", "The annual report of actual indirect rates and costs contractors send the DCAA within six months of year-end."),
        new("dcaa", "DCAA", "Defense Contract Audit Agency", "The government auditors who review contractors' timekeeping, accounting and billing."),
        new("journal-entry", "Journal entry", "Journal entry", "A record of debits and credits posted to the general ledger."),
        new("fiscal-period", "Fiscal period", "Fiscal period", "An accounting month. Time and costs post to the period they fall in; closed periods can't change."),

        // Purchasing and assets
        new("po", "PO", "Purchase order", "The order sent to a vendor that commits the company to buy something at an agreed price."),
        new("requisition", "Requisition", "Purchase requisition", "A request to buy something. Once approved, purchasing turns it into a purchase order."),
        new("committed", "Committed", "Committed cost", "Money promised on purchase orders that hasn't been billed yet."),
        new("three-way-match", "Three-way match", "Three-way match", "Checking that a vendor's invoice agrees with the purchase order and with what was received before it's paid."),
        new("match-exception", "Match exception", "Three-way match exception", "A vendor invoice whose quantity or price doesn't agree with the purchase order or what was received, so it's held until someone resolves it."),
        new("capitalize", "Capitalize", "Capitalize", "Record a purchase as a fixed asset and spread its cost over its useful life (depreciation) instead of expensing it all at once."),
        new("depreciation", "Depreciation", "Depreciation", "Spreading a fixed asset's cost over the years it's used."),
        new("subcontract", "Subcontract", "Subcontract", "Work on your contract done by another company, paid through their invoices."),
        new("ap", "AP", "Accounts payable", "Bills from vendors the company owes and hasn't paid yet."),

        // Resourcing
        new("allocation-hours", "Allocation", "Allocation", "Hours a person is planned to work on a project in a week."),
        new("capacity", "Capacity", "Capacity", "The hours a person can work in a week: standard hours less holidays, approved leave and time for non-project work."),
        new("utilization", "Utilization", "Utilization", "The share of a person's available hours spent on billable or direct work."),
    ];

    private static readonly Dictionary<string, GlossaryEntry> ByKey = All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<GlossaryEntry> Entries => All;

    /// <summary>The entry for a key (case-insensitive), or null when the glossary doesn't have it.</summary>
    public static GlossaryEntry? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var entry) ? entry : null;
}
