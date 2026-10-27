namespace Workbench.Contracts.ProjectAccounting;

/// <summary>One step of setting up a company in Workbench, in the order to do them.</summary>
public sealed record SetupStep(string Group, string Title, string Hint, string Href, bool Done);

/// <summary>
/// The getting-started steps, shared by the Setup page and the administrator's to-do on Home so they never disagree about what's
/// next. Done-ness comes from <see cref="SetupChecklistDto"/>.
/// </summary>
public static class SetupSteps
{
    public static IReadOnlyList<SetupStep> For(SetupChecklistDto c) =>
    [
        new("Company", "Users and sign-in", "Invite the people who'll use Workbench and give Finance and timekeepers their roles.", "admin/users", c.HasInvitedUsers),
        new("Company", "Fiscal calendar", "At least one open accounting month.", "setup/fiscal-calendar", c.HasOpenFiscalPeriods),
        new("Company", "Chart of accounts", "Review the general ledger accounts (a GovCon default is ready to use).", "accounting/accounts", c.HasChartOfAccounts),
        new("Company", "Bank accounts", "The accounts you pay from and get paid into, for reconciliation.", "cash", c.HasBankAccount),
        new("People", "Labor categories", "Job titles used for budgets and bill rates.", "setup/labor-categories", c.HasLaborCategories),
        new("People", "Employees", "Everyone who records time, with their supervisor.", "setup/people", c.HasEmployees),
        new("People", "Cost rates", "What an hour of each person's time costs (hours × cost rate = labor cost).", "setup/people", c.HasCostRates),
        new("People", "Payroll provider", "Where approved hours are sent for paychecks (Paylocity and others).", "payroll", c.HasPayrollProvider),
        new("People", "Approval workflows", "Who approves timesheets, expenses, purchases and invoices.", "setup/approval-workflows", c.HasApprovalWorkflows),
        new("Contracts and projects", "Customer and contract", "With its bill rates and CLINs; fund each CLIN once it exists.", "setup/contracts", c.HasCustomerAndContract),
        new("Contracts and projects", "Start a project", "From the contract: pick its labor categories and a project manager. The CLINs and tasks become the WBS, the charge codes people record time to.", "setup/projects", c.HasProjectWithWbs),
        new("Contracts and projects", "First approved plan", "The project manager plans people and hours; approving the plan makes it the budget and opens the project for time.", "setup/projects", c.HasChargeAssignments),
        new("Contracts and projects", "Indirect rates", "Fringe, overhead and G&A rates used for burdened cost and billing.", "accounting/indirect-rates", c.HasIndirectRates),
        new("Contracts and projects", "Billing details", "Invoice numbering and customer billing addresses for each contract.", "billing/contracts", c.HasBillingDetails),
    ];
}
