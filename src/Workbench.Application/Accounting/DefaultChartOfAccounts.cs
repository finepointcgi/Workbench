using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>
/// The default chart of accounts for a government services contractor: balance sheet accounts, revenue by contract
/// type, direct costs, the fringe, overhead and G&amp;A pools, and segregated unallowable costs (FAR 31.201-6), plus
/// posting rules that map timesheets and ODCs onto it.
/// </summary>
public static class DefaultChartOfAccounts
{
    private sealed record Def(string Number, string Name, AccountType Type, string? Parent = null, CostClassification Class = CostClassification.None, string? Description = null);

    private static readonly Def[] Accounts =
    [
        new("1000", "Assets", AccountType.Asset),
        new("1010", "Cash - operating", AccountType.Asset, "1000"),
        new("1100", "Accounts receivable", AccountType.Asset, "1000"),
        new("1110", "Accounts receivable - billed", AccountType.Asset, "1100"),
        new("1120", "Accounts receivable - unbilled", AccountType.Asset, "1100", Description: "Earned on contracts but not yet invoiced"),
        new("1300", "Prepaid expenses", AccountType.Asset, "1000"),
        new("1500", "Property and equipment", AccountType.Asset, "1000"),
        new("1510", "Computer equipment", AccountType.Asset, "1500"),
        new("1520", "Furniture and office equipment", AccountType.Asset, "1500"),
        new("1530", "Leasehold improvements", AccountType.Asset, "1500"),
        new("1590", "Accumulated depreciation", AccountType.Asset, "1500", Description: "Contra asset: depreciation posted by the fixed asset runs"),

        new("2000", "Liabilities", AccountType.Liability),
        new("2010", "Accounts payable", AccountType.Liability, "2000", Description: "Vendor ODCs and subcontract invoices"),
        new("2020", "AP retainage", AccountType.Liability, "2000", Description: "Subcontract retainage withheld from AP bills until release"),
        new("2100", "Accrued payroll", AccountType.Liability, "2000", Description: "Labor cost from approved timesheets until payroll is paid"),
        new("2110", "Accrued employee reimbursements", AccountType.Liability, "2000", Description: "Approved expense reports not yet reimbursed"),
        new("2200", "Accrued liabilities", AccountType.Liability, "2000"),

        new("3000", "Equity", AccountType.Equity),
        new("3100", "Paid-in capital", AccountType.Equity, "3000"),
        new("3200", "Retained earnings", AccountType.Equity, "3000"),

        new("4000", "Revenue", AccountType.Revenue),
        new("4010", "Revenue - cost-plus contracts", AccountType.Revenue, "4000"),
        new("4020", "Revenue - time and materials", AccountType.Revenue, "4000"),
        new("4030", "Revenue - labor hour", AccountType.Revenue, "4000"),
        new("4040", "Revenue - firm fixed price", AccountType.Revenue, "4000"),
        new("4900", "Gain (loss) on disposal of assets", AccountType.Revenue, Description: "Fixed asset disposals: proceeds less net book value"),
        new("4910", "Interest income", AccountType.Revenue, "4000", Description: "Bank interest booked from bank reconciliation"),

        new("5000", "Direct costs", AccountType.Expense),
        new("5010", "Direct labor", AccountType.Expense, "5000", CostClassification.DirectLabor),
        new("5100", "Direct ODCs", AccountType.Expense, "5000"),
        new("5110", "Direct travel", AccountType.Expense, "5100", CostClassification.DirectOdc),
        new("5120", "Direct materials", AccountType.Expense, "5100", CostClassification.DirectOdc),
        new("5130", "Direct equipment", AccountType.Expense, "5100", CostClassification.DirectOdc),
        new("5140", "Subcontracts", AccountType.Expense, "5100", CostClassification.DirectOdc),
        new("5150", "Consultants", AccountType.Expense, "5100", CostClassification.DirectOdc),
        new("5190", "Other direct costs", AccountType.Expense, "5100", CostClassification.DirectOdc),

        new("6000", "Fringe pool", AccountType.Expense),
        new("6010", "Paid leave and holidays", AccountType.Expense, "6000", CostClassification.Fringe),
        new("6020", "Payroll taxes", AccountType.Expense, "6000", CostClassification.Fringe),
        new("6030", "Health and retirement benefits", AccountType.Expense, "6000", CostClassification.Fringe),
        new("6040", "Fringe administration labor", AccountType.Expense, "6000", CostClassification.IndirectLabor),

        new("6100", "Overhead pool", AccountType.Expense),
        new("6110", "Overhead labor", AccountType.Expense, "6100", CostClassification.IndirectLabor),
        new("6120", "Facilities and rent", AccountType.Expense, "6100", CostClassification.Overhead),
        new("6130", "Overhead supplies", AccountType.Expense, "6100", CostClassification.Overhead),
        new("6140", "Depreciation", AccountType.Expense, "6100", CostClassification.Overhead, "Fixed asset depreciation (FAR 31.205-11)"),

        new("6200", "G&A pool", AccountType.Expense),
        new("6210", "G&A labor", AccountType.Expense, "6200", CostClassification.IndirectLabor, "Includes bid and proposal labor"),
        new("6220", "Professional fees", AccountType.Expense, "6200", CostClassification.GeneralAndAdministrative),
        new("6230", "Office and IT", AccountType.Expense, "6200", CostClassification.GeneralAndAdministrative),
        new("6240", "Bank fees", AccountType.Expense, "6200", CostClassification.GeneralAndAdministrative, "Bank service charges booked from bank reconciliation"),

        new("7000", "Unallowable costs", AccountType.Expense),
        new("7010", "Unallowable - entertainment and alcohol", AccountType.Expense, "7000", CostClassification.Unallowable, "FAR 31.205-14, 31.205-51"),
        new("7020", "Unallowable - travel over per diem", AccountType.Expense, "7000", CostClassification.Unallowable, "FAR 31.205-46"),
        new("7090", "Unallowable - other", AccountType.Expense, "7000", CostClassification.Unallowable),

        // Indirect rates: burden applied to projects (debit, with project/WBS segments) against a contra account in each pool.
        new("5900", "Applied indirect costs", AccountType.Expense),
        new("5910", "Applied fringe", AccountType.Expense, "5900", CostClassification.AppliedBurden),
        new("5920", "Applied overhead", AccountType.Expense, "5900", CostClassification.AppliedBurden),
        new("5930", "Applied G&A", AccountType.Expense, "5900", CostClassification.AppliedBurden),
        new("6090", "Fringe applied", AccountType.Expense, "6000", CostClassification.AppliedBurden, "Contra: fringe allocated to projects"),
        new("6190", "Overhead applied", AccountType.Expense, "6100", CostClassification.AppliedBurden, "Contra: overhead allocated to projects"),
        new("6290", "G&A applied", AccountType.Expense, "6200", CostClassification.AppliedBurden, "Contra: G&A allocated to projects"),
    ];

    private static readonly (PostingRuleKind Kind, string Key, string Account)[] Rules =
    [
        (PostingRuleKind.DirectLabor, "", "5010"),
        (PostingRuleKind.IndirectLabor, nameof(IndirectPool.Overhead), "6110"),
        (PostingRuleKind.IndirectLabor, nameof(IndirectPool.GeneralAndAdministrative), "6210"),
        (PostingRuleKind.IndirectLabor, nameof(IndirectPool.Fringe), "6040"),
        (PostingRuleKind.LeaveLabor, "", "6010"),
        (PostingRuleKind.Odc, nameof(OdcType.Travel), "5110"),
        (PostingRuleKind.Odc, nameof(OdcType.Materials), "5120"),
        (PostingRuleKind.Odc, nameof(OdcType.Equipment), "5130"),
        (PostingRuleKind.Odc, nameof(OdcType.Subcontract), "5140"),
        (PostingRuleKind.Odc, nameof(OdcType.Consultant), "5150"),
        (PostingRuleKind.Odc, nameof(OdcType.Other), "5190"),
        (PostingRuleKind.UnallowableOdc, "", "7090"),
        (PostingRuleKind.UnallowableOdc, nameof(OdcType.Travel), "7020"),
        (PostingRuleKind.UnallowableOdc, nameof(OdcType.Other), "7010"),
        (PostingRuleKind.AccruedPayroll, "", "2100"),
        (PostingRuleKind.AccountsPayable, "", "2010"),
        (PostingRuleKind.AccruedReimbursement, "", "2110"),
    ];

    /// <summary>New account and rule entities (not yet added to a context).</summary>
    public static (IReadOnlyList<Account> Accounts, IReadOnlyList<PostingRule> Rules) Build(DateTime utcNow, string actor)
    {
        var byNumber = new Dictionary<string, Account>();
        foreach (var def in Accounts)
        {
            byNumber[def.Number] = new Account
            {
                Number = def.Number,
                Name = def.Name,
                Type = def.Type,
                NormalBalance = Account.DefaultNormalBalance(def.Type),
                ParentId = def.Parent is null ? null : byNumber[def.Parent].Id,
                CostClass = def.Class,
                Description = def.Description,
                CreatedAtUtc = utcNow,
            };
        }

        var rules = Rules.Select(r => new PostingRule { Kind = r.Kind, Key = r.Key, AccountId = byNumber[r.Account].Id, UpdatedBy = actor, UpdatedAtUtc = utcNow }).ToList();
        return (byNumber.Values.ToList(), rules);
    }

    /// <summary>Default indirect pools for WBS codes used by the development seed (overhead is the default anyway).</summary>
    public static IReadOnlyDictionary<string, IndirectPool> SeedPools { get; } = new Dictionary<string, IndirectPool>
    {
        ["IND.GA"] = IndirectPool.GeneralAndAdministrative,
        ["IND.BP"] = IndirectPool.GeneralAndAdministrative,
    };
}
