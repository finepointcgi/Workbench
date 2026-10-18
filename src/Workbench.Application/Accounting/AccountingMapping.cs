using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

internal static class AccountingMapping
{
    public static AccountKind ToContract(this AccountType v) => (AccountKind)(int)v;
    public static AccountType ToDomain(this AccountKind v) => (AccountType)(int)v;
    public static BalanceSide ToContract(this NormalBalance v) => (BalanceSide)(int)v;
    public static NormalBalance ToDomain(this BalanceSide v) => (NormalBalance)(int)v;
    public static CostClass ToContract(this CostClassification v) => (CostClass)(int)v;
    public static CostClassification ToDomain(this CostClass v) => (CostClassification)(int)v;
    public static JournalEntrySource ToContract(this JournalSource v) => (JournalEntrySource)(int)v;
    public static JournalSource ToDomain(this JournalEntrySource v) => (JournalSource)(int)v;
    public static JournalEntryState ToContract(this JournalStatus v) => (JournalEntryState)(int)v;
    public static JournalStatus ToDomain(this JournalEntryState v) => (JournalStatus)(int)v;
    public static PostingRuleType ToContract(this PostingRuleKind v) => (PostingRuleType)(int)v;
    public static PostingRuleKind ToDomain(this PostingRuleType v) => (PostingRuleKind)(int)v;
    public static CostPool ToContract(this IndirectPool v) => (CostPool)(int)v;
    public static IndirectPool ToDomain(this CostPool v) => (IndirectPool)(int)v;

    /// <summary>Debit-positive balance of a set of lines.</summary>
    public static decimal Net(this IEnumerable<JournalLine> lines) => lines.Sum(l => l.Debit - l.Credit);

    public static string Label(CostPool pool) => pool switch
    {
        CostPool.GeneralAndAdministrative => "G&A",
        _ => pool.ToString(),
    };
}

/// <summary>Codes and names for the segments on journal lines, loaded once per operation.</summary>
internal sealed class LedgerLookups
{
    public required Dictionary<Guid, Account> Accounts { get; init; }
    public required Dictionary<Guid, FiscalPeriod> Periods { get; init; }
    public required Dictionary<Guid, string> OrgUnits { get; init; }
    public required Dictionary<Guid, string> Projects { get; init; }
    public required Dictionary<Guid, WbsElement> Wbs { get; init; }

    public static async Task<LedgerLookups> LoadAsync(IWorkbenchDbContext db, CancellationToken ct) => new()
    {
        Accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct),
        Periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
        OrgUnits = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, ct),
        Projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, ct),
        Wbs = await db.Set<WbsElement>().AsNoTracking().ToDictionaryAsync(w => w.Id, ct),
    };

    public string PeriodName(Guid id) => Periods.TryGetValue(id, out var p) ? p.Name : "";

    public JournalEntrySummaryDto Summary(JournalEntry e) => new(
        e.Id, e.Number, e.Date, e.FiscalPeriodId, PeriodName(e.FiscalPeriodId), e.Source.ToContract(), e.SourceId, e.Description,
        e.Status.ToContract(), e.TotalDebit, e.TotalCredit, e.CreatedBy, e.PostedBy, e.PostedAtUtc, e.ReversalOfId, e.ReversedById);

    public JournalEntryDto Detail(JournalEntry e) => new(
        Summary(e),
        e.Lines.OrderBy(l => l.LineNumber).Select(Line).ToList(),
        e.ReversalReason,
        e.SourceRevision);

    public JournalLineDto Line(JournalLine l)
    {
        Accounts.TryGetValue(l.AccountId, out var account);
        return new JournalLineDto(
            l.Id, l.LineNumber, l.AccountId, account?.Number ?? "?", account?.Name ?? "Unknown account",
            l.OrgUnitId, l.OrgUnitId is { } o ? OrgUnits.GetValueOrDefault(o) : null,
            l.ProjectId, l.ProjectId is { } p ? Projects.GetValueOrDefault(p) : null,
            l.WbsElementId, l.WbsElementId is { } w && Wbs.TryGetValue(w, out var wbs) ? wbs.Code : null,
            l.Debit, l.Credit, l.Memo);
    }
}
